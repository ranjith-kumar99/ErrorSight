using ExceptionLens.Runtime.Metadata;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace ExceptionLens.Weaver;

/// <summary>
/// Injects an exception filter into one method:
///
/// <code>
///   .try { original body }
///   filter {
///       // stack: exception
///       ldtoken  &lt;this method&gt;; ldtoken &lt;declaring type&gt;; ldc.i4 &lt;id&gt;
///       newarr object { this, args..., locals... }
///       call bool WeavingHooks::OnFilter(object, RuntimeMethodHandle, RuntimeTypeHandle, int32, object[])
///       endfilter                       // always false → exception keeps propagating untouched
///   } { pop; rethrow }                   // unreachable
/// </code>
///
/// The filter runs during the CLR's first (search) pass, before any stack unwinding or finally
/// blocks, so it observes the exact values present when the exception was thrown.
/// </summary>
internal sealed class MethodWeaver
{
    private readonly MethodDefinition _method;
    private readonly MethodShape _shape;
    private readonly MethodReference _onFilter;
    private readonly int _id;

    public MethodWeaver(MethodDefinition method, MethodShape shape, MethodReference onFilter, int id)
    {
        _method = method;
        _shape = shape;
        _onFilter = onFilter;
        _id = id;
    }

    public WovenMethod? Weave()
    {
        var body = _method.Body;
        body.SimplifyMacros();

        // Analysis runs on the original IL (original offsets are still valid here).
        var accesses = DereferenceAnalyzer.Analyze(_method, _shape);
        var slots = SlotCollector.Collect(_method, _shape);

        bool woven;
        if (_shape == MethodShape.StateMachineMoveNext && FindAsyncCatch(body) is { } compilerCatch)
        {
            woven = WeaveBeforeCompilerCatch(body, compilerCatch, slots);
        }
        else if (_shape == MethodShape.StateMachineMoveNext && CecilHelpers.IsAsyncStateMachine(_method.DeclaringType))
        {
            woven = false; // unexpected async shape: leave untouched
        }
        else
        {
            woven = WrapWholeBody(body, slots);
        }

        body.OptimizeMacros();
        if (!woven) return null;

        CecilHelpers.ComputeOffsets(body);
        return BuildMetadata(body, slots, accesses);
    }

    // ── Sync methods, iterators, lambdas ─────────────────────────────────────

    private bool WrapWholeBody(MethodBody body, List<CapturedSlot> slots)
    {
        var instructions = body.Instructions;
        var tryStart = FindTryStart(body);
        if (tryStart is null || tryStart.OpCode.Code == Code.Ret) return false;

        var isVoid = _method.ReturnType.MetadataType == MetadataType.Void;
        var rets = instructions.Where(i => i.OpCode.Code == Code.Ret).ToList();

        VariableDefinition? returnValue = null;
        Instruction? epilogue = null;
        if (rets.Count > 0)
        {
            if (!isVoid)
            {
                returnValue = new VariableDefinition(_method.ReturnType);
                body.Variables.Add(returnValue);
                epilogue = Instruction.Create(OpCodes.Ldloc, returnValue);
            }
            else
            {
                epilogue = Instruction.Create(OpCodes.Ret);
            }
        }

        // Rewrite each `ret` in place so branches that targeted it stay valid.
        var il = body.GetILProcessor();
        foreach (var ret in rets)
        {
            if (isVoid)
            {
                ret.OpCode = OpCodes.Leave;
                ret.Operand = epilogue;
            }
            else
            {
                ret.OpCode = OpCodes.Stloc;
                ret.Operand = returnValue;
                il.InsertAfter(ret, Instruction.Create(OpCodes.Leave, epilogue));
            }
        }

        // `tail.` calls are not allowed inside a protected region.
        foreach (var instruction in instructions.Where(i => i.OpCode.Code == Code.Tail).ToList())
        {
            instruction.OpCode = OpCodes.Nop;
            instruction.Operand = null;
        }

        var (filter, handler) = BuildFilterBlocks(slots);
        var filterStart = filter[0];

        // Handlers that currently end at "end of method" must now end where our blocks begin.
        foreach (var existing in body.ExceptionHandlers)
        {
            if (existing.HandlerEnd is null) existing.HandlerEnd = filterStart;
            if (existing.TryEnd is null) existing.TryEnd = filterStart;
        }

        foreach (var instruction in filter) instructions.Add(instruction);
        foreach (var instruction in handler) instructions.Add(instruction);
        if (epilogue is not null)
        {
            instructions.Add(epilogue);
            if (!isVoid) instructions.Add(Instruction.Create(OpCodes.Ret));
        }

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Filter)
        {
            TryStart = tryStart,
            TryEnd = filterStart,
            FilterStart = filterStart,
            HandlerStart = handler[0],
            HandlerEnd = epilogue,
        });

        AddHiddenSequencePoint(filterStart);
        if (epilogue is not null) AddHiddenSequencePoint(epilogue);
        return true;
    }

    /// <summary>
    /// For constructors of reference types the protected region starts after the base/this .ctor call
    /// (field initialisers run before it and `this` is not yet initialised there).
    /// </summary>
    private Instruction? FindTryStart(MethodBody body)
    {
        var first = body.Instructions.FirstOrDefault();
        if (!_method.IsConstructor || _method.IsStatic || _method.DeclaringType.IsValueType) return first;

        var self = _method.DeclaringType;
        var baseType = self.BaseType?.GetElementType().FullName;
        foreach (var instruction in body.Instructions)
        {
            if (instruction.OpCode.Code != Code.Call || instruction.Operand is not MethodReference { Name: ".ctor" } ctor)
                continue;
            var owner = ctor.DeclaringType.GetElementType().FullName;
            if (owner == baseType || owner == self.FullName) return instruction.Next;
        }

        return null;
    }

    // ── Async state machines ─────────────────────────────────────────────────

    /// <summary>The compiler's outer catch (Exception) → builder.SetException(ex) in an async MoveNext.</summary>
    private static ExceptionHandler? FindAsyncCatch(MethodBody body) =>
        body.ExceptionHandlers
            .Where(h => h.HandlerType == ExceptionHandlerType.Catch &&
                        h.CatchType?.FullName == "System.Exception" &&
                        CecilHelpers.Range(h.HandlerStart, h.HandlerEnd).Any(i =>
                            i.OpCode.FlowControl == FlowControl.Call &&
                            i.Operand is MethodReference { Name: "SetException" }))
            .OrderByDescending(h => h.TryEnd?.Offset ?? int.MaxValue)
            .FirstOrDefault();

    /// <summary>
    /// The compiler's catch-all would handle the exception before an outer filter ran, so our filter is added
    /// as a second handler for the *same* try block, ahead of it — the shape C# emits for
    /// <c>try {…} catch (E) when (f) {…} catch (Exception) {…}</c>.
    /// </summary>
    private bool WeaveBeforeCompilerCatch(MethodBody body, ExceptionHandler compilerCatch, List<CapturedSlot> slots)
    {
        var oldTryEnd = compilerCatch.TryEnd;
        if (oldTryEnd is null) return false;

        var (filter, handler) = BuildFilterBlocks(slots);
        var filterStart = filter[0];

        var il = body.GetILProcessor();
        foreach (var instruction in filter.Concat(handler))
            il.InsertBefore(oldTryEnd, instruction);

        foreach (var existing in body.ExceptionHandlers)
        {
            if (existing.TryEnd == oldTryEnd) existing.TryEnd = filterStart;
            if (existing != compilerCatch && existing.HandlerEnd == oldTryEnd) existing.HandlerEnd = filterStart;
        }

        body.ExceptionHandlers.Insert(body.ExceptionHandlers.IndexOf(compilerCatch), new ExceptionHandler(ExceptionHandlerType.Filter)
        {
            TryStart = compilerCatch.TryStart,
            TryEnd = filterStart,
            FilterStart = filterStart,
            HandlerStart = handler[0],
            HandlerEnd = oldTryEnd,
        });

        AddHiddenSequencePoint(filterStart);
        return true;
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    private (List<Instruction> Filter, List<Instruction> Handler) BuildFilterBlocks(List<CapturedSlot> slots)
    {
        var module = _method.Module;
        var filter = new List<Instruction>
        {
            // stack: [exception]
            Instruction.Create(OpCodes.Ldtoken, CecilHelpers.SelfMethod(_method)),
            Instruction.Create(OpCodes.Ldtoken, CecilHelpers.SelfType(_method.DeclaringType)),
            Instruction.Create(OpCodes.Ldc_I4, _id),
            Instruction.Create(OpCodes.Ldc_I4, slots.Count),
            Instruction.Create(OpCodes.Newarr, module.TypeSystem.Object),
        };

        for (var i = 0; i < slots.Count; i++)
        {
            filter.Add(Instruction.Create(OpCodes.Dup));
            filter.Add(Instruction.Create(OpCodes.Ldc_I4, i));
            filter.AddRange(slots[i].Loader);
            filter.Add(Instruction.Create(OpCodes.Stelem_Ref));
        }

        filter.Add(Instruction.Create(OpCodes.Call, _onFilter));
        filter.Add(Instruction.Create(OpCodes.Endfilter));

        var handler = new List<Instruction>
        {
            Instruction.Create(OpCodes.Pop),
            Instruction.Create(OpCodes.Rethrow),
        };

        return (filter, handler);
    }

    /// <summary>Marks injected code as hidden for debuggers. Only called for freshly created instructions.</summary>
    private void AddHiddenSequencePoint(Instruction instruction)
    {
        var debug = _method.DebugInformation;
        if (debug is null || !debug.HasSequencePoints) return;
        var document = debug.SequencePoints[0].Document;
        debug.SequencePoints.Add(new SequencePoint(instruction, document)
        {
            StartLine = 0xFEEFEE,
            EndLine = 0xFEEFEE,
            StartColumn = 0,
            EndColumn = 0,
        });
    }

    private WovenMethod BuildMetadata(MethodBody body, List<CapturedSlot> slots, List<AccessRecord> accesses)
    {
        var codeSize = body.Instructions.Count == 0 ? 0 : body.Instructions[^1].Offset + body.Instructions[^1].GetSize();
        int Resolve(InstructionOffset offset) => offset.IsEndOfMethod ? codeSize : offset.Offset;

        var metadata = new WovenMethod { Id = _id, DisplayName = CecilHelpers.DisplayName(_method) };

        foreach (var slot in slots)
        {
            if (slot.Scopes is { Count: > 0 } scopes)
            {
                slot.Meta.Scopes = scopes
                    .Select(s => new WovenLocalScope { Name = s.Name, Start = Resolve(s.Start), End = Resolve(s.End) })
                    .ToList();
            }
            metadata.Slots.Add(slot.Meta);
        }

        var debug = _method.DebugInformation;
        if (debug is not null && debug.HasSequencePoints)
        {
            // Portable PDBs require sequence points ordered by IL offset.
            var ordered = debug.SequencePoints.OrderBy(sp => sp.Offset).ToList();
            debug.SequencePoints.Clear();
            foreach (var sp in ordered) debug.SequencePoints.Add(sp);

            foreach (var sp in ordered)
                metadata.SequencePoints.Add(new WovenSequencePoint { Offset = sp.Offset, Line = sp.IsHidden ? -1 : sp.StartLine });
        }

        metadata.StackEmptyOffsets = StackDepthAnalyzer.StackEmptyOffsets(_method);

        foreach (var access in accesses)
        {
            metadata.Accesses.Add(new WovenAccess
            {
                Offset = access.Instruction.Offset,
                Kind = access.Kind,
                Receiver = access.Receiver,
                Result = access.Result,
                Index = access.Index,
            });
        }

        return metadata;
    }
}
