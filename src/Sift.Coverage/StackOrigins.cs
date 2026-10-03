using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Sift.Coverage;

/// <summary>
/// For every instruction of a method body, which instruction loaded each value on the stack before
/// it, as far as a field load (<c>ldfld</c>, <c>ldsfld</c>, <c>ldflda</c>, <c>ldsflda</c>) goes: a forward
/// data-flow pass over all branches. Walking back from a call only finds its receiver when nothing
/// between them branches, and the compiler's cached lambdas do branch
/// (<c>_cache.GetOrAdd(key, static k => ...)</c> loads the delegate with <c>ldsfld; dup; brtrue</c>).
/// Where paths meet, a slot keeps its loader only when every path agrees, so an answer is never
/// a guess; IL whose stack heights disagree, or that holds an instruction the pass cannot size, gives
/// no answer for what follows.
/// </summary>
internal sealed class StackOrigins
{
    /// <summary>Stack on entry to each reached instruction, bottom first; a slot holds its field load or null.</summary>
    private readonly Dictionary<Instruction, Instruction?[]?> _entry = [];

    private StackOrigins()
    {
    }

    /// <summary>Runs the pass over <paramref name="body"/>; later changes to the body are not seen.</summary>
    /// <param name="body">A method body.</param>
    public static StackOrigins Of(MethodBody body)
    {
        var origins = new StackOrigins();
        var work = new Stack<Instruction>();
        if (body.Instructions.Count > 0)
        {
            origins.Merge(body.Instructions[0], [], work);
        }

        foreach (var handler in body.ExceptionHandlers)
        {
            // A catch or filter starts with the exception on the stack; a finally or fault with nothing.
            Instruction?[] start = handler.HandlerType is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter ? [null] : [];
            origins.Merge(handler.HandlerStart, start, work);
            if (handler.FilterStart is { } filter)
            {
                origins.Merge(filter, [null], work);
            }
        }

        while (work.TryPop(out var instruction))
        {
            if (origins._entry[instruction] is not { } stack || After(instruction, stack) is not { } after)
            {
                continue;
            }

            foreach (var next in Successors(instruction))
            {
                origins.Merge(next, after, work);
            }
        }

        return origins;
    }

    /// <summary>
    /// The field load that pushed argument <paramref name="index"/> of <paramref name="call"/>, the
    /// receiver of an instance call being argument 0; null when it was something else or is unknown.
    /// </summary>
    /// <param name="call">A call in the analysed body.</param>
    /// <param name="index">Argument position, receiver included.</param>
    public Instruction? Argument(Instruction call, int index)
    {
        if (_entry.GetValueOrDefault(call) is not { } stack || Pops(call) is not { } pops || pops > stack.Length || index >= pops)
        {
            return null;
        }

        return stack[stack.Length - pops + index];
    }

    /// <summary>Joins <paramref name="incoming"/> into the entry stack of <paramref name="target"/>, queueing it when that changes.</summary>
    /// <param name="target">Instruction control reaches.</param>
    /// <param name="incoming">Stack it arrives with.</param>
    /// <param name="work">Instructions to revisit.</param>
    private void Merge(Instruction target, Instruction?[] incoming, Stack<Instruction> work)
    {
        if (!_entry.TryGetValue(target, out var current))
        {
            _entry[target] = incoming;
            work.Push(target);
            return;
        }

        if (current is null)
        {
            return;
        }

        if (current.Length != incoming.Length)
        {
            // Not valid IL; nothing after this point is answered.
            _entry[target] = null;
            work.Push(target);
            return;
        }

        Instruction?[]? merged = null;
        for (var slot = 0; slot < current.Length; slot++)
        {
            if (current[slot] is not null && current[slot] != incoming[slot])
            {
                merged ??= [.. current];
                merged[slot] = null;
            }
        }

        if (merged is not null)
        {
            _entry[target] = merged;
            work.Push(target);
        }
    }

    /// <summary>The stack after <paramref name="instruction"/> runs; null when it ends the flow or cannot be sized.</summary>
    /// <param name="instruction">Instruction.</param>
    /// <param name="stack">Stack before it.</param>
    private static Instruction?[]? After(Instruction instruction, Instruction?[] stack)
    {
        var code = instruction.OpCode.Code;
        if (code is Code.Leave or Code.Leave_S or Code.Endfinally)
        {
            // Leaving a protected region empties the stack.
            return [];
        }

        if (instruction.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw)
        {
            return null;
        }

        if (code is Code.Dup)
        {
            return stack.Length == 0 ? null : [.. stack, stack[^1]];
        }

        if (Pops(instruction) is not { } pops || Pushes(instruction) is not { } pushes || pops > stack.Length)
        {
            return null;
        }

        var after = new Instruction?[stack.Length - pops + pushes];
        Array.Copy(stack, after, stack.Length - pops);
        if (pushes == 1 && code is Code.Ldfld or Code.Ldsfld or Code.Ldflda or Code.Ldsflda)
        {
            after[^1] = instruction;
        }

        return after;
    }

    /// <summary>Instructions control can go to after <paramref name="instruction"/>.</summary>
    /// <param name="instruction">Instruction.</param>
    private static IEnumerable<Instruction> Successors(Instruction instruction)
    {
        switch (instruction.Operand)
        {
            case Instruction target when instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch:
                yield return target;
                break;
            case Instruction[] targets:
                foreach (var target in targets)
                {
                    yield return target;
                }

                break;
        }

        if (instruction.OpCode.FlowControl is not (FlowControl.Branch or FlowControl.Return or FlowControl.Throw) && instruction.Next is { } next)
        {
            yield return next;
        }
    }

    /// <summary>Values <paramref name="instruction"/> takes off the stack; null when unknown (<c>ret</c>, <c>leave</c>).</summary>
    /// <param name="instruction">Instruction.</param>
    public static int? Pops(Instruction instruction) => instruction.OpCode.StackBehaviourPop switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
            or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
            or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
        StackBehaviour.Varpop when instruction.Operand is MethodReference method =>
            method.Parameters.Count + (method.HasThis && instruction.OpCode != OpCodes.Newobj ? 1 : 0),
        StackBehaviour.Varpop when instruction.Operand is CallSite site => site.Parameters.Count + (site.HasThis ? 1 : 0) + 1,
        _ => null,
    };

    /// <summary>Values <paramref name="instruction"/> leaves on the stack; null when unknown.</summary>
    /// <param name="instruction">Instruction.</param>
    public static int? Pushes(Instruction instruction) => instruction.OpCode.StackBehaviourPush switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Varpush when instruction.Operand is MethodReference method =>
            instruction.OpCode == OpCodes.Newobj || method.ReturnType.MetadataType != MetadataType.Void ? 1 : 0,
        StackBehaviour.Varpush when instruction.Operand is CallSite site => site.ReturnType.MetadataType != MetadataType.Void ? 1 : 0,
        _ => null,
    };
}
