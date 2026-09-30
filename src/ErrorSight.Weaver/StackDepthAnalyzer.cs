using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ErrorSight.Weaver;

/// <summary>
/// Computes the IL offsets at which the evaluation stack is empty. For optimized code the JIT records
/// native→IL mappings only at such boundaries, so a reported exception IL offset means "somewhere between
/// this boundary and the next one".
/// </summary>
internal static class StackDepthAnalyzer
{
    public static List<int> StackEmptyOffsets(MethodDefinition method)
    {
        var body = method.Body;
        if (body.Instructions.Count == 0) return new List<int>();

        var depths = new Dictionary<Instruction, int>();
        var work = new Stack<(Instruction Start, int Depth)>();
        work.Push((body.Instructions[0], 0));

        foreach (var handler in body.ExceptionHandlers)
        {
            work.Push((handler.TryStart, 0));
            var entersWithException = handler.HandlerType is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter;
            work.Push((handler.HandlerStart, entersWithException ? 1 : 0));
            if (handler.FilterStart is not null) work.Push((handler.FilterStart, 1));
        }

        while (work.Count > 0)
        {
            var (instruction, depth) = work.Pop();
            for (var current = instruction; current is not null; current = current.Next)
            {
                if (!depths.TryAdd(current, depth)) break; // already visited (IL guarantees consistent depth)

                depth = Math.Max(0, depth - Pops(current, depth, method) + Pushes(current));

                var code = current.OpCode.Code;
                switch (current.Operand)
                {
                    case Instruction target when current.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch:
                        work.Push((target, code is Code.Leave or Code.Leave_S ? 0 : depth));
                        break;
                    case Instruction[] targets:
                        foreach (var target in targets) work.Push((target, depth));
                        break;
                }

                if (current.OpCode.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw ||
                    code is Code.Leave or Code.Leave_S or Code.Endfinally or Code.Endfilter)
                    break; // no fall-through
            }
        }

        return depths.Where(kv => kv.Value == 0).Select(kv => kv.Key.Offset).OrderBy(o => o).ToList();
    }

    private static int Pops(Instruction instruction, int depth, MethodDefinition method)
    {
        var opcode = instruction.OpCode;
        switch (opcode.StackBehaviourPop)
        {
            case StackBehaviour.PopAll:
                return depth;
            case StackBehaviour.Varpop:
                switch (instruction.Operand)
                {
                    case MethodReference callee:
                        return callee.Parameters.Count + (callee.HasThis && opcode.Code != Code.Newobj ? 1 : 0);
                    case CallSite site:
                        return site.Parameters.Count + (site.HasThis ? 1 : 0) + 1;
                }
                return opcode.Code == Code.Ret && method.ReturnType.MetadataType != MetadataType.Void ? 1 : 0;
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref: return 1;
            case StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
                or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref:
                return 3;
            default:
                return 2;
        }
    }

    private static int Pushes(Instruction instruction)
    {
        var opcode = instruction.OpCode;
        switch (opcode.StackBehaviourPush)
        {
            case StackBehaviour.Push0: return 0;
            case StackBehaviour.Push1_push1: return 2;
            case StackBehaviour.Varpush:
                return instruction.Operand switch
                {
                    MethodReference callee when opcode.Code == Code.Newobj => 1,
                    MethodReference callee => callee.ReturnType.MetadataType == MetadataType.Void ? 0 : 1,
                    CallSite site => site.ReturnType.MetadataType == MetadataType.Void ? 0 : 1,
                    _ => 0,
                };
            default: return 1;
        }
    }
}
