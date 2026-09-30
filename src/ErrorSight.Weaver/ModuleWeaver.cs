using ErrorSight.Runtime.Metadata;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ErrorSight.Weaver;

/// <summary>Chooses which methods of a module to instrument and weaves them.</summary>
internal sealed class ModuleWeaver
{
    private readonly ModuleDefinition _module;
    private readonly WeaverOptions _options;
    private readonly MethodReference _onFilter;

    public ModuleWeaver(ModuleDefinition module, WeaverOptions options, MethodReference onFilter)
    {
        _module = module;
        _options = options;
        _onFilter = onFilter;
    }

    public List<WovenMethod> Execute()
    {
        var metadata = new List<WovenMethod>();
        var nextId = 1;

        foreach (var type in _module.GetTypes().ToList())
        {
            if (!TryClassify(type, out var typeShape)) continue;

            foreach (var method in type.Methods.ToList())
            {
                if (!TryClassify(method, typeShape, out var shape)) continue;

                // Any failure here aborts the whole assembly (the caller keeps the original bytes),
                // so a half-rewritten method can never reach disk.
                var woven = new MethodWeaver(method, shape, _onFilter, nextId).Weave();
                if (woven is null) continue;

                metadata.Add(woven);
                nextId++;
                if (_options.Verbose) Console.WriteLine($"  woven: {woven.DisplayName} ({woven.Slots.Count} values, {woven.Accesses.Count} accesses)");
            }
        }

        return metadata;
    }

    private enum TypeShape { Normal, StateMachine, DisplayClass, LambdaCache }

    private static bool TryClassify(TypeDefinition type, out TypeShape shape)
    {
        shape = TypeShape.Normal;
        if (type.Name == "<Module>" || type.IsInterface) return false;

        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (CecilHelpers.HasIgnoreAttribute(current.CustomAttributes)) return false;
            if (CecilHelpers.HasAttribute(current, "System.CodeDom.Compiler.GeneratedCodeAttribute")) return false;
        }

        if (!CecilHelpers.IsCompilerGeneratedName(type.Name)) return true;

        if (CecilHelpers.IsAsyncStateMachine(type) || CecilHelpers.IsIteratorStateMachine(type))
        {
            shape = TypeShape.StateMachine;
            return true;
        }

        if (CecilHelpers.IsDisplayClass(type))
        {
            shape = TypeShape.DisplayClass;
            return true;
        }

        if (CecilHelpers.IsLambdaCache(type))
        {
            shape = TypeShape.LambdaCache;
            return true;
        }

        return false; // anonymous types, <PrivateImplementationDetails>, fixed buffers, ...
    }

    private bool TryClassify(MethodDefinition method, TypeShape typeShape, out MethodShape shape)
    {
        shape = MethodShape.Normal;
        if (!method.HasBody || method.Body.Instructions.Count == 0) return false;
        if (method.IsConstructor && method.IsStatic) return false;
        if (method.CallingConvention == MethodCallingConvention.VarArg) return false;
        if (method.ReturnType is ByReferenceType) return false;
        if (method.AggressiveInlining) return false;
        if (CecilHelpers.HasIgnoreAttribute(method.CustomAttributes)) return false;
        if (CecilHelpers.HasAttribute(method, "System.CodeDom.Compiler.GeneratedCodeAttribute")) return false;
        if (CecilHelpers.IsKickoffMethod(method)) return false; // the body lives in the state machine
        if (method.Body.Instructions.Any(i => i.OpCode.Code == Code.Jmp)) return false;

        var isGeneratedMember = CecilHelpers.HasAttribute(method, "System.Runtime.CompilerServices.CompilerGeneratedAttribute");

        switch (typeShape)
        {
            case TypeShape.StateMachine:
                if (method.Name != "MoveNext") return false;
                shape = MethodShape.StateMachineMoveNext;
                return true; // size limit does not apply: MoveNext carries the whole user method

            case TypeShape.DisplayClass:
                if (!CecilHelpers.IsLambdaOrLocalFunction(method.Name)) return false;
                shape = method.IsStatic ? MethodShape.StaticLambda : MethodShape.DisplayClassLambda;
                break;

            case TypeShape.LambdaCache:
                if (!CecilHelpers.IsLambdaOrLocalFunction(method.Name)) return false;
                shape = MethodShape.StaticLambda;
                break;

            default:
                if (CecilHelpers.IsLambdaOrLocalFunction(method.Name))
                    shape = MethodShape.Normal; // local functions / lambdas hosted on the user type
                else if (isGeneratedMember || CecilHelpers.IsCompilerGeneratedName(method.Name))
                    return false; // auto-property accessors, record members, ...
                break;
        }

        return method.Body.CodeSize > _options.MinILSize;
    }
}
