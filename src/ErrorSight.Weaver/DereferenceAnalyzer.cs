using System.Globalization;
using ErrorSight.Runtime.Metadata;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ErrorSight.Weaver;

/// <summary>An instruction that dereferences <see cref="Receiver"/> (throws NullReferenceException if it is null).</summary>
internal sealed record AccessRecord(Instruction Instruction, AccessKind Kind, string Receiver, string Result, string? Index);

/// <summary>A store of <see cref="Value"/> into the named local <see cref="Variable"/>.</summary>
internal sealed record AssignmentRecord(Instruction Instruction, string Variable, string Value);

/// <summary>
/// Reconstructs source-like expressions (e.g. <c>order.Customer.Address</c>) for every instruction that
/// dereferences an object, by simulating the IL evaluation stack symbolically within each basic block.
///
/// At runtime the throw site's IL offset (or source line) selects the candidate accesses, and the captured
/// values decide which receiver was actually null. Precision is best-effort: anything the simulation cannot
/// name becomes "?" and is dropped.
///
/// Stores into named locals are recorded too (<c>address = customer.Address</c>), so a null local can be traced
/// back to the expression it came from.
/// </summary>
internal static class DereferenceAnalyzer
{
    private const string Unknown = "?";
    /// <summary>A value that changes over time (loop index, current element): rendered as <c>[…]</c>.</summary>
    private const string Reassigned = "…";
    private const string Closure = "\u0001closure";
    private const int MaxExpressionLength = 200;

    public static (List<AccessRecord> Accesses, List<AssignmentRecord> Assignments) Analyze(MethodDefinition method, MethodShape shape)
    {
        var body = method.Body;
        var records = new List<AccessRecord>();
        var assignments = new List<AssignmentRecord>();

        var localNames = SlotCollector.NamedLocals(method).ToDictionary(x => x.Variable, x => x.Names);

        // Compiler temporaries (unnamed locals, state-machine bookkeeping fields) carry the expression last
        // stored in them — but only when stored exactly once; loop counters and the like become "…".
        var temps = new Dictionary<VariableDefinition, string>();
        var fieldTemps = new Dictionary<string, string>(StringComparer.Ordinal);
        var storeCounts = new Dictionary<object, int>();
        foreach (var instruction in body.Instructions)
        {
            object? target = instruction.OpCode.Code switch
            {
                Code.Stloc => instruction.Operand,
                Code.Stfld => ((FieldReference)instruction.Operand).Name,
                _ => null,
            };
            // Clearing a temp (`= null` after a foreach) does not make its meaningful value ambiguous.
            if (target is not null && instruction.Previous?.OpCode.Code != Code.Ldnull)
                storeCounts[target] = storeCounts.GetValueOrDefault(target) + 1;
        }
        string TempValue(object key, string value) => storeCounts.GetValueOrDefault(key) > 1 ? Reassigned : value;

        var blockStarts = new HashSet<Instruction>();
        var handlerStarts = new HashSet<Instruction>();
        foreach (var instruction in body.Instructions)
        {
            switch (instruction.Operand)
            {
                case Instruction target: blockStarts.Add(target); break;
                case Instruction[] targets: foreach (var t in targets) blockStarts.Add(t); break;
            }
        }
        foreach (var handler in body.ExceptionHandlers)
        {
            if (handler.HandlerStart is not null) handlerStarts.Add(handler.HandlerStart);
            if (handler.FilterStart is not null) handlerStarts.Add(handler.FilterStart);
        }

        var stack = new List<string>();

        string Pop()
        {
            if (stack.Count == 0) return Unknown;
            var value = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return value;
        }

        void Push(string value) => stack.Add(value.Length > MaxExpressionLength ? Unknown : value);

        List<string> PopMany(int count)
        {
            var values = new List<string>(count);
            for (var i = 0; i < count; i++) values.Add(Pop());
            values.Reverse();
            return values;
        }

        void Record(Instruction instruction, AccessKind kind, string receiver, string result, string? index = null)
        {
            if (!IsNameable(receiver)) return;
            records.Add(new AccessRecord(instruction, kind, receiver, Clean(result), index is null ? null : Clean(index)));
        }

        void RecordAssignment(Instruction instruction, string variable, string value)
        {
            if (value != "null" && !CanBeNull(value)) return;
            if (value == variable || value.Contains(Closure, StringComparison.Ordinal)) return;
            assignments.Add(new AssignmentRecord(instruction, variable, value));
        }

        foreach (var instruction in body.Instructions)
        {
            if (handlerStarts.Contains(instruction))
            {
                stack.Clear();
                stack.Add(Unknown); // the exception object
            }
            else if (blockStarts.Contains(instruction))
            {
                stack.Clear();
            }

            var opcode = instruction.OpCode;
            switch (opcode.Code)
            {
                case Code.Ldarg:
                case Code.Ldarga:
                    Push(ArgumentExpression((ParameterDefinition)instruction.Operand, body, shape));
                    break;

                case Code.Ldloc:
                case Code.Ldloca:
                {
                    var variable = (VariableDefinition)instruction.Operand;
                    if (CecilHelpers.IsDisplayClass(variable.VariableType)) Push(Closure);
                    else if (LocalName(localNames, variable, instruction) is { } name) Push(name);
                    else Push(temps.TryGetValue(variable, out var expr) ? expr : Unknown);
                    break;
                }

                case Code.Stloc:
                {
                    var variable = (VariableDefinition)instruction.Operand;
                    var value = Pop();
                    if (!localNames.ContainsKey(variable))
                    {
                        if (value != "null") temps[variable] = TempValue(variable, value);
                    }
                    else if (LocalName(localNames, variable, instruction) is { } name)
                    {
                        RecordAssignment(instruction, name, value);
                    }
                    break;
                }

                case Code.Ldfld:
                case Code.Ldflda:
                {
                    var field = (FieldReference)instruction.Operand;
                    var receiver = Pop();
                    if (receiver == Closure)
                    {
                        Push(CecilHelpers.IsDisplayClass(field.FieldType)
                            ? Closure
                            : CecilHelpers.SourceNameOfField(field.Name)
                              ?? fieldTemps.GetValueOrDefault(field.Name, Unknown));
                        break;
                    }

                    var result = Member(receiver, FieldMemberName(field));
                    if (!IsValueType(field.DeclaringType)) Record(instruction, AccessKind.Member, receiver, result);
                    Push(result);
                    break;
                }

                case Code.Stfld:
                {
                    var field = (FieldReference)instruction.Operand;
                    var value = Pop();
                    var receiver = Pop();
                    if (receiver == Closure && value != "null" && CecilHelpers.SourceNameOfField(field.Name) is null)
                        fieldTemps[field.Name] = TempValue(field.Name, value);
                    // Hoisted locals of async/iterator methods and variables captured by lambdas live in fields.
                    if (receiver == Closure && CecilHelpers.SourceNameOfField(field.Name) is { } hoisted && hoisted != "this" &&
                        !CecilHelpers.IsDisplayClass(field.FieldType))
                        RecordAssignment(instruction, hoisted, value);
                    if (receiver != Closure && !IsValueType(field.DeclaringType))
                        Record(instruction, AccessKind.Member, receiver, Member(receiver, FieldMemberName(field)));
                    break;
                }

                case Code.Ldsfld:
                case Code.Ldsflda:
                {
                    var field = (FieldReference)instruction.Operand;
                    Push(CecilHelpers.IsCompilerGeneratedName(field.DeclaringType.Name) || CecilHelpers.IsCompilerGeneratedName(field.Name)
                        ? Unknown
                        : $"{CecilHelpers.TypeDisplayName(field.DeclaringType)}.{FieldMemberName(field)}");
                    break;
                }

                case Code.Call:
                case Code.Callvirt:
                case Code.Newobj:
                    SimulateCall(instruction, (MethodReference)instruction.Operand);
                    break;

                case Code.Ldlen:
                {
                    var receiver = Pop();
                    var result = Member(receiver, "Length");
                    Record(instruction, AccessKind.Member, receiver, result);
                    Push(result);
                    break;
                }

                case Code.Ldelema:
                case Code.Ldelem_Any:
                case Code.Ldelem_I:
                case Code.Ldelem_I1:
                case Code.Ldelem_I2:
                case Code.Ldelem_I4:
                case Code.Ldelem_I8:
                case Code.Ldelem_R4:
                case Code.Ldelem_R8:
                case Code.Ldelem_Ref:
                case Code.Ldelem_U1:
                case Code.Ldelem_U2:
                case Code.Ldelem_U4:
                {
                    var index = Pop();
                    var receiver = Pop();
                    var result = Element(receiver, index);
                    Record(instruction, AccessKind.Element, receiver, result, index);
                    Push(result);
                    break;
                }

                case Code.Stelem_Any:
                case Code.Stelem_I:
                case Code.Stelem_I1:
                case Code.Stelem_I2:
                case Code.Stelem_I4:
                case Code.Stelem_I8:
                case Code.Stelem_R4:
                case Code.Stelem_R8:
                case Code.Stelem_Ref:
                {
                    Pop();
                    var index = Pop();
                    var receiver = Pop();
                    Record(instruction, AccessKind.Element, receiver, Element(receiver, index), index);
                    break;
                }

                case Code.Unbox:
                case Code.Unbox_Any:
                {
                    var receiver = Pop();
                    if (opcode.Code == Code.Unbox || IsValueType((TypeReference)instruction.Operand))
                        Record(instruction, AccessKind.Member, receiver, receiver);
                    Push(receiver);
                    break;
                }

                case Code.Isinst:
                {
                    // `x as T` is a common source of nulls, so it is kept visible: (x as T).Member
                    var value = Pop();
                    Push(value is Unknown or Closure or Reassigned
                        ? Unknown
                        : $"({value} as {CecilHelpers.FriendlyTypeName((TypeReference)instruction.Operand)})");
                    break;
                }

                // Reading through a by-ref (ref/out/in parameters, ref locals) yields the variable itself.
                case Code.Ldobj:
                case Code.Ldind_Ref: case Code.Ldind_I: case Code.Ldind_I1: case Code.Ldind_I2: case Code.Ldind_I4:
                case Code.Ldind_I8: case Code.Ldind_U1: case Code.Ldind_U2: case Code.Ldind_U4: case Code.Ldind_R4:
                case Code.Ldind_R8:
                case Code.Castclass:
                case Code.Box:
                case Code.Conv_I: case Code.Conv_I1: case Code.Conv_I2: case Code.Conv_I4: case Code.Conv_I8:
                case Code.Conv_U: case Code.Conv_U1: case Code.Conv_U2: case Code.Conv_U4: case Code.Conv_U8:
                case Code.Conv_R4: case Code.Conv_R8: case Code.Conv_R_Un:
                    Push(Pop());
                    break;

                case Code.Dup:
                {
                    var top = Pop();
                    Push(top);
                    Push(top);
                    break;
                }

                case Code.Ldnull:
                    Push("null");
                    break;

                case Code.Ldstr:
                {
                    var text = (string)instruction.Operand;
                    Push(text.Length <= 24 ? $"\"{text}\"" : "\"…\"");
                    break;
                }

                case Code.Ldc_I4:
                case Code.Ldc_I8:
                case Code.Ldc_R4:
                case Code.Ldc_R8:
                    Push(Convert.ToString(instruction.Operand, CultureInfo.InvariantCulture) ?? Unknown);
                    break;

                case Code.Calli:
                {
                    var site = (CallSite)instruction.Operand;
                    Pop(); // function pointer
                    PopMany(site.Parameters.Count);
                    if (site.HasThis) Pop();
                    if (site.ReturnType.MetadataType != MetadataType.Void) Push(Unknown);
                    break;
                }

                case Code.Ret:
                    if (method.ReturnType.MetadataType != MetadataType.Void) Pop();
                    break;

                default:
                    ApplyStackBehaviour(opcode);
                    break;
            }

            if (opcode.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw ||
                opcode.Code is Code.Leave or Code.Endfinally or Code.Endfilter)
            {
                stack.Clear();
            }
        }

        return (records, assignments);

        void SimulateCall(Instruction instruction, MethodReference callee)
        {
            var args = PopMany(callee.Parameters.Count);

            if (instruction.OpCode.Code == Code.Newobj)
            {
                Push($"new {CecilHelpers.TypeDisplayName(callee.DeclaringType)}(…)");
                return;
            }

            string? receiver = callee.HasThis ? Pop() : null;
            var name = callee.Name;
            var dot = name.LastIndexOf('.');
            if (dot > 0 && !name.StartsWith('.')) name = name[(dot + 1)..]; // explicit interface implementations

            string expression;
            var kind = AccessKind.Member;
            string? index = null;

            if (receiver is not null)
            {
                var target = receiver == Closure ? Unknown : receiver;
                if (name == "get_Item" && args.Count >= 1)
                {
                    kind = AccessKind.Element;
                    index = args[0];
                    expression = Element(target, index);
                }
                else if (name == "set_Item" && args.Count >= 2)
                {
                    kind = AccessKind.Element;
                    index = args[0];
                    expression = Element(target, index);
                }
                else if (name == "get_Current" && args.Count == 0 && target.EndsWith(".GetEnumerator(…)", StringComparison.Ordinal))
                {
                    // foreach (var x in items): Release builds often keep x only on the stack.
                    expression = $"{target[..^".GetEnumerator(…)".Length]}[{Reassigned}]";
                }
                else if (name.StartsWith("get_", StringComparison.Ordinal) && args.Count == 0)
                {
                    expression = Member(target, name[4..]);
                }
                else
                {
                    expression = Member(target, $"{name}(…)");
                }

                if (instruction.OpCode.Code == Code.Callvirt)
                    Record(instruction, kind, receiver, expression, index);
            }
            else if (args.Count >= 1 && IsExtensionMethod(callee))
            {
                expression = Member(args[0], $"{name}(…)");
            }
            else
            {
                expression = CecilHelpers.IsCompilerGeneratedName(callee.DeclaringType.Name) || CecilHelpers.IsCompilerGeneratedName(name)
                    ? Unknown
                    : $"{CecilHelpers.TypeDisplayName(callee.DeclaringType)}.{name}(…)";
            }

            if (callee.ReturnType.MetadataType != MetadataType.Void) Push(expression);
        }

        void ApplyStackBehaviour(OpCode opcode)
        {
            switch (opcode.StackBehaviourPop)
            {
                case StackBehaviour.PopAll: stack.Clear(); break;
                default: PopMany(PopCount(opcode.StackBehaviourPop)); break;
            }

            for (var i = PushCount(opcode.StackBehaviourPush); i > 0; i--) Push(Unknown);
        }
    }

    private static string ArgumentExpression(ParameterDefinition parameter, MethodBody body, MethodShape shape)
    {
        if (parameter == body.ThisParameter)
            return shape == MethodShape.Normal ? "this" : Closure;

        var type = parameter.ParameterType is ByReferenceType byRef ? byRef.ElementType : parameter.ParameterType;
        if (CecilHelpers.IsDisplayClass(type)) return Closure;
        return string.IsNullOrEmpty(parameter.Name) ? Unknown : parameter.Name;
    }

    private static string? LocalName(
        Dictionary<VariableDefinition, List<(string Name, InstructionOffset Start, InstructionOffset End)>> names,
        VariableDefinition variable,
        Instruction at)
    {
        if (!names.TryGetValue(variable, out var candidates)) return null;
        foreach (var (name, start, end) in candidates)
        {
            if (CecilHelpers.IsCompilerGeneratedName(name)) continue;
            var from = start.IsEndOfMethod ? int.MaxValue : start.Offset;
            var to = end.IsEndOfMethod ? int.MaxValue : end.Offset;
            if (at.Offset >= from && at.Offset < to) return name;
        }
        return candidates.Select(c => c.Name).FirstOrDefault(n => !CecilHelpers.IsCompilerGeneratedName(n));
    }

    private static string FieldMemberName(FieldReference field)
    {
        var name = field.Name;
        if (name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
            return name[1..name.IndexOf('>')];
        return name;
    }

    private static string Member(string receiver, string member) =>
        receiver is Unknown or Closure or Reassigned ? Unknown : $"{receiver}.{member}";

    private static string Element(string receiver, string index) =>
        receiver == Unknown || receiver == Closure ? Unknown : $"{receiver}[{(index == Closure || index == Unknown ? Reassigned : index)}]";

    private static string Clean(string expression) => expression.Replace(Closure, Unknown);

    /// <summary>Values that can be null: not a literal, an object creation, or something unnamed.</summary>
    private static bool CanBeNull(string value)
    {
        if (value is Unknown or Closure or Reassigned or "this") return false;
        if (value.StartsWith("new ", StringComparison.Ordinal)) return false;
        var first = value[0];
        return first != '"' && !char.IsDigit(first) && first != '-';
    }

    /// <summary>Receivers worth reporting: named, non-literal, and possibly null.</summary>
    private static bool IsNameable(string receiver)
    {
        if (receiver is Unknown or Closure or Reassigned or "this" or "null") return false;
        if (receiver.Contains(Closure, StringComparison.Ordinal)) return false;
        if (receiver.StartsWith("new ", StringComparison.Ordinal)) return false;
        var first = receiver[0];
        return first != '"' && first != '?' && !char.IsDigit(first) && first != '-';
    }

    private static bool IsValueType(TypeReference type)
    {
        if (type.IsValueType) return true;
        if (type is GenericParameter) return false;
        try { return type.Resolve()?.IsValueType ?? false; }
        catch { return false; }
    }

    private static bool IsExtensionMethod(MethodReference method)
    {
        try
        {
            var definition = method.Resolve();
            return definition is not null && CecilHelpers.HasAttribute(definition, "System.Runtime.CompilerServices.ExtensionAttribute");
        }
        catch
        {
            return false;
        }
    }

    private static int PopCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
            or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1
            or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
            or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
        _ => 0,
    };

    private static int PushCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4
            or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        _ => 0,
    };
}
