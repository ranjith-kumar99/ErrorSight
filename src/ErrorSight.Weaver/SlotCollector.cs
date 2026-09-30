using ErrorSight.Runtime.Metadata;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ErrorSight.Weaver;

/// <summary>A value the injected filter will read, plus the IL that pushes it (boxed) onto the stack.</summary>
internal sealed class CapturedSlot
{
    public required WovenSlot Meta { get; init; }
    public required List<Instruction> Loader { get; init; }
    /// <summary>Scope boundaries for locals, resolved to IL offsets after weaving.</summary>
    public List<(string Name, InstructionOffset Start, InstructionOffset End)>? Scopes { get; init; }
}

internal enum MethodShape
{
    Normal,
    StateMachineMoveNext,
    DisplayClassLambda,
    StaticLambda,
}

/// <summary>
/// Decides which parameters, locals and state-machine fields a method's filter captures.
/// Only values that can be boxed safely are included; everything else is skipped silently.
/// </summary>
internal static class SlotCollector
{
    public static List<CapturedSlot> Collect(MethodDefinition method, MethodShape shape)
    {
        var slots = new List<CapturedSlot>();
        var body = method.Body;
        var self = method.DeclaringType;

        // ── this ───────────────────────────────────────────────────────────────
        if (method.HasThis && shape is MethodShape.Normal or MethodShape.DisplayClassLambda)
        {
            var selfType = CecilHelpers.SelfType(self);
            if (!self.IsValueType)
            {
                slots.Add(Slot("this", SlotKind.This,
                    shape == MethodShape.DisplayClassLambda ? SlotFlags.Closure : SlotFlags.None,
                    CecilHelpers.FriendlyTypeName(selfType),
                    Instruction.Create(OpCodes.Ldarg, body.ThisParameter)));
            }
            else if (CecilHelpers.IsCapturable(selfType))
            {
                slots.Add(Slot("this", SlotKind.This, SlotFlags.None, CecilHelpers.FriendlyTypeName(selfType),
                    Instruction.Create(OpCodes.Ldarg, body.ThisParameter),
                    Instruction.Create(OpCodes.Ldobj, selfType),
                    Instruction.Create(OpCodes.Box, selfType)));
            }
        }

        // ── state machine fields (hoisted locals and parameters) ───────────────
        if (shape == MethodShape.StateMachineMoveNext)
        {
            foreach (var field in self.Fields)
            {
                if (field.IsStatic) continue;

                var isClosure = CecilHelpers.IsDisplayClass(field.FieldType);
                var name = isClosure ? "<closure>" : CecilHelpers.SourceNameOfField(field.Name);
                if (name is null || !CecilHelpers.IsCapturable(field.FieldType)) continue;

                var flags = isClosure ? SlotFlags.Closure : SlotFlags.None;
                var fieldReference = CecilHelpers.SelfField(field);
                slots.Add(Slot(name, SlotKind.Field, flags, CecilHelpers.FriendlyTypeName(field.FieldType),
                    Instruction.Create(OpCodes.Ldarg, body.ThisParameter),
                    Instruction.Create(OpCodes.Ldfld, fieldReference),
                    Instruction.Create(OpCodes.Box, fieldReference.FieldType)));
            }
        }

        // ── parameters ─────────────────────────────────────────────────────────
        foreach (var parameter in method.Parameters)
        {
            var type = parameter.ParameterType;
            var flags = SlotFlags.None;

            if (type is ByReferenceType byRef)
            {
                var element = byRef.ElementType;
                if (!CecilHelpers.IsCapturable(element)) continue;
                var isClosure = CecilHelpers.IsDisplayClass(element); // local function over a struct closure
                if (isClosure) flags |= SlotFlags.Closure;
                var name = isClosure ? "<closure>" : parameter.Name;
                if (string.IsNullOrEmpty(name)) continue;

                slots.Add(Slot(name, SlotKind.Argument, flags, CecilHelpers.FriendlyTypeName(element),
                    Instruction.Create(OpCodes.Ldarg, parameter),
                    Instruction.Create(OpCodes.Ldobj, element),
                    Instruction.Create(OpCodes.Box, element)));
            }
            else
            {
                if (!CecilHelpers.IsCapturable(type)) continue;
                var isClosure = CecilHelpers.IsDisplayClass(type);
                if (isClosure) flags |= SlotFlags.Closure;
                var name = isClosure ? "<closure>" : parameter.Name;
                if (string.IsNullOrEmpty(name)) continue;

                slots.Add(Slot(name, SlotKind.Argument, flags, CecilHelpers.FriendlyTypeName(type),
                    Instruction.Create(OpCodes.Ldarg, parameter),
                    Instruction.Create(OpCodes.Box, type)));
            }
        }

        // ── named locals (from the PDB) ────────────────────────────────────────
        foreach (var (variable, names) in NamedLocals(method))
        {
            if (variable.IsPinned || !CecilHelpers.IsCapturable(variable.VariableType)) continue;

            var isClosure = CecilHelpers.IsDisplayClass(variable.VariableType) ||
                            names.Any(n => n.Name.StartsWith("CS$<>8__locals", StringComparison.Ordinal));
            var userNames = names.Where(n => !CecilHelpers.IsCompilerGeneratedName(n.Name)).ToList();
            if (!isClosure && userNames.Count == 0) continue;

            var primary = isClosure ? "<closure>" : userNames[0].Name;
            var flags = isClosure ? SlotFlags.Closure : SlotFlags.None;

            slots.Add(new CapturedSlot
            {
                Meta = new WovenSlot
                {
                    Name = primary,
                    Kind = SlotKind.Local,
                    Flags = flags,
                    TypeName = CecilHelpers.FriendlyTypeName(variable.VariableType),
                },
                Loader =
                [
                    Instruction.Create(OpCodes.Ldloc, variable),
                    Instruction.Create(OpCodes.Box, variable.VariableType),
                ],
                Scopes = isClosure ? null : userNames,
            });
        }

        return slots;
    }

    /// <summary>
    /// Every named local in the method's PDB scopes, grouped by slot (Release builds reuse
    /// a slot for several locals in disjoint scopes).
    /// </summary>
    public static List<(VariableDefinition Variable, List<(string Name, InstructionOffset Start, InstructionOffset End)> Names)>
        NamedLocals(MethodDefinition method)
    {
        var result = new Dictionary<VariableDefinition, List<(string, InstructionOffset, InstructionOffset)>>();
        var root = method.DebugInformation?.Scope;
        if (root is null || !method.Body.HasVariables) return new();

        void Visit(ScopeDebugInformation scope)
        {
            if (scope.HasVariables)
            {
                foreach (var variable in scope.Variables)
                {
                    if (variable.IsDebuggerHidden || string.IsNullOrEmpty(variable.Name)) continue;
                    var index = variable.Index;
                    if (index < 0 || index >= method.Body.Variables.Count) continue;
                    var definition = method.Body.Variables[index];
                    if (!result.TryGetValue(definition, out var names))
                        result[definition] = names = new();
                    names.Add((variable.Name, scope.Start, scope.End));
                }
            }

            if (scope.HasScopes)
                foreach (var child in scope.Scopes) Visit(child);
        }

        Visit(root);
        return result.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static CapturedSlot Slot(string name, SlotKind kind, SlotFlags flags, string typeName, params Instruction[] loader) => new()
    {
        Meta = new WovenSlot { Name = name, Kind = kind, Flags = flags, TypeName = typeName },
        Loader = loader.ToList(),
    };
}
