using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ExceptionLens.Weaver;

internal static class CecilHelpers
{
    private const string IgnoreAttributeName = "ExceptionLensIgnoreAttribute";

    private static readonly HashSet<string> SensitiveAttributeNames = new(StringComparer.Ordinal)
    {
        "SensitiveAttribute",
        "SensitiveDataAttribute",
        "PersonalDataAttribute",
        "ProtectedPersonalDataAttribute",
    };

    private static readonly Regex HoistedLocal = new(@"^<(?<name>[^>]+)>5__\d+$", RegexOptions.Compiled);
    private static readonly Regex GeneratedMethod = new(@"^<(?<outer>[^>]*)>(?<kind>[bg])__(?<inner>[^|]*)", RegexOptions.Compiled);
    private static readonly Regex StateMachineTypeName = new(@"^<(?<name>[^>]+)>d__\d+", RegexOptions.Compiled);

    // ── Attributes ───────────────────────────────────────────────────────────

    public static bool HasIgnoreAttribute(IEnumerable<CustomAttribute> attributes) =>
        attributes.Any(a => a.AttributeType.Name == IgnoreAttributeName);

    public static bool HasAttribute(ICustomAttributeProvider provider, string fullName) =>
        provider.HasCustomAttributes && provider.CustomAttributes.Any(a => a.AttributeType.FullName == fullName);

    /// <summary>
    /// Recognises sensitive-data attributes by name so no package dependency is needed:
    /// [Sensitive], [PersonalData], [ProtectedPersonalData] and any
    /// Microsoft.Extensions.Compliance DataClassificationAttribute subclass.
    /// </summary>
    public static bool HasSensitiveAttribute(ICustomAttributeProvider provider)
    {
        if (!provider.HasCustomAttributes) return false;
        foreach (var attribute in provider.CustomAttributes)
        {
            if (SensitiveAttributeNames.Contains(attribute.AttributeType.Name)) return true;
            if (DerivesFrom(attribute.AttributeType, "DataClassificationAttribute")) return true;
        }
        return false;
    }

    private static bool DerivesFrom(TypeReference type, string baseName)
    {
        try
        {
            var current = type.Resolve()?.BaseType;
            for (var depth = 0; current is not null && depth < 10; depth++)
            {
                if (current.Name == baseName) return true;
                current = current.Resolve()?.BaseType;
            }
        }
        catch
        {
            // Unresolvable base type: not sensitive by attribute.
        }
        return false;
    }

    // ── Compiler-generated shapes ────────────────────────────────────────────

    public static bool IsCompilerGeneratedName(string name) => name.StartsWith('<') || name.StartsWith("CS$", StringComparison.Ordinal);

    public static bool IsDisplayClass(TypeReference type)
    {
        var name = type.GetElementType().Name;
        return name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal);
    }

    public static bool IsLambdaCache(TypeDefinition type) => type.Name == "<>c";

    public static bool IsAsyncStateMachine(TypeDefinition type) =>
        type.Interfaces.Any(i => i.InterfaceType.FullName == "System.Runtime.CompilerServices.IAsyncStateMachine");

    public static bool IsIteratorStateMachine(TypeDefinition type) =>
        StateMachineTypeName.IsMatch(type.Name) &&
        type.Interfaces.Any(i => i.InterfaceType.FullName == "System.Collections.IEnumerator");

    public static bool IsKickoffMethod(MethodDefinition method) =>
        HasAttribute(method, "System.Runtime.CompilerServices.AsyncStateMachineAttribute") ||
        HasAttribute(method, "System.Runtime.CompilerServices.IteratorStateMachineAttribute") ||
        HasAttribute(method, "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute");

    public static bool IsLambdaOrLocalFunction(string methodName) => GeneratedMethod.IsMatch(methodName);

    /// <summary>
    /// Maps a state-machine or closure field back to the source variable it holds.
    /// Returns null for compiler bookkeeping fields (state, builder, awaiters, spills, ...).
    /// </summary>
    public static string? SourceNameOfField(string fieldName)
    {
        if (fieldName == "<>4__this") return "this";
        if (!fieldName.StartsWith('<')) return fieldName;
        var match = HoistedLocal.Match(fieldName);
        return match.Success ? match.Groups["name"].Value : null;
    }

    // ── Display names ────────────────────────────────────────────────────────

    public static string DisplayName(MethodDefinition method)
    {
        var type = method.DeclaringType;
        var userType = type;
        while (userType.DeclaringType is not null && IsCompilerGeneratedName(userType.Name))
            userType = userType.DeclaringType;
        var typeName = TypeDisplayName(userType);

        var stateMachine = StateMachineTypeName.Match(type.Name);
        if (method.Name == "MoveNext" && stateMachine.Success && (IsAsyncStateMachine(type) || IsIteratorStateMachine(type)))
            return $"{typeName}.{stateMachine.Groups["name"].Value}";

        var generated = GeneratedMethod.Match(method.Name);
        if (generated.Success)
        {
            var outer = generated.Groups["outer"].Value;
            return generated.Groups["kind"].Value == "g"
                ? $"{typeName}.{outer}.{generated.Groups["inner"].Value}"
                : $"{typeName}.{outer}.<lambda>";
        }

        return $"{typeName}.{method.Name}";
    }

    public static string TypeDisplayName(TypeReference type)
    {
        var parts = new List<string>();
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (!IsCompilerGeneratedName(current.Name)) parts.Add(StripArity(current.Name));
        }
        parts.Reverse();
        return string.Join('.', parts);
    }

    public static string StripArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    // ── Type capability checks ───────────────────────────────────────────────

    /// <summary>
    /// True when a value of this type can be boxed into object[] safely.
    /// Anything we cannot prove safe (pointers, ref structs, unresolvable value types) is excluded,
    /// because boxing a byref-like type would make the method fail JIT verification.
    /// </summary>
    public static bool IsCapturable(TypeReference type)
    {
        while (type is IModifierType modifier) type = modifier.ElementType;

        switch (type)
        {
            case ByReferenceType:
            case PointerType:
            case FunctionPointerType:
            case PinnedType:
            case SentinelType:
                return false;
            case ArrayType:
                return true;
            case GenericParameter gp:
                return ((int)gp.Attributes & 0x0020) == 0; // AllowByRefLike (C# 13 'allows ref struct')
        }

        switch (type.MetadataType)
        {
            case MetadataType.TypedByReference:
            case MetadataType.Pointer:
            case MetadataType.FunctionPointer:
                return false;
            case MetadataType.Void:
                return false;
        }

        var full = type.GetElementType().FullName;
        if (full is "System.ArgIterator" or "System.RuntimeArgumentHandle" or "System.TypedReference")
            return false;

        TypeDefinition? definition = null;
        try { definition = type.Resolve(); } catch { /* ignored */ }

        if (definition is null)
            return !type.IsValueType; // unresolvable reference types box as a no-op; unknown structs are skipped

        if (definition.IsValueType &&
            HasAttribute(definition, "System.Runtime.CompilerServices.IsByRefLikeAttribute"))
            return false;

        return true;
    }

    // ── Generic self references (for ldtoken / ldfld inside generic code) ────

    public static TypeReference SelfType(TypeDefinition type)
    {
        if (!type.HasGenericParameters) return type;
        var instance = new GenericInstanceType(type);
        foreach (var parameter in type.GenericParameters) instance.GenericArguments.Add(parameter);
        return instance;
    }

    public static MethodReference SelfMethod(MethodDefinition method)
    {
        MethodReference reference = method;
        if (method.DeclaringType.HasGenericParameters)
        {
            var onInstance = new MethodReference(method.Name, method.ReturnType, SelfType(method.DeclaringType))
            {
                HasThis = method.HasThis,
                ExplicitThis = method.ExplicitThis,
                CallingConvention = method.CallingConvention,
            };
            foreach (var parameter in method.Parameters)
                onInstance.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
            foreach (var genericParameter in method.GenericParameters)
                onInstance.GenericParameters.Add(new GenericParameter(genericParameter.Name, onInstance));
            reference = onInstance;
        }

        if (method.HasGenericParameters)
        {
            var instance = new GenericInstanceMethod(reference);
            foreach (var genericParameter in method.GenericParameters)
                instance.GenericArguments.Add(genericParameter);
            reference = instance;
        }

        return reference;
    }

    public static FieldReference SelfField(FieldDefinition field) =>
        field.DeclaringType.HasGenericParameters
            ? new FieldReference(field.Name, field.FieldType, SelfType(field.DeclaringType))
            : field;

    // ── IL helpers ───────────────────────────────────────────────────────────

    public static void ComputeOffsets(MethodBody body)
    {
        var offset = 0;
        foreach (var instruction in body.Instructions)
        {
            instruction.Offset = offset;
            offset += instruction.GetSize();
        }
    }

    public static IEnumerable<Instruction> Range(Instruction start, Instruction? end)
    {
        for (var current = start; current is not null && current != end; current = current.Next)
            yield return current;
    }
}
