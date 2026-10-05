namespace TestDetta.Coverage;

/// <summary>
/// When a process tree is killed for its memory. A test host that allocates without end (a mutant
/// that breaks a loop's exit) does not crash by itself on macOS: the system compresses and swaps
/// until it resets the whole machine (corpus round 2e, 2026-10-05: three test hosts held 69 GB of
/// a 48 GB machine). A tree is stopped when it alone holds more than <paramref name="TreeBytes"/>,
/// or when the machine is nearly out of memory and the tree holds a large share of it; small trees
/// are left alone then, since they are not what fills the memory.
/// </summary>
/// <param name="TreeBytes">Most memory the tree may hold at any time.</param>
/// <param name="PressureFreePercent">Free memory, in percent of the machine's, below which the machine counts as nearly out.</param>
/// <param name="PressureTreeBytes">Smallest tree that is killed while the machine is nearly out.</param>
internal sealed record MemoryLimit(long TreeBytes, int PressureFreePercent, long PressureTreeBytes)
{
    /// <summary>
    /// Limits for the machine this runs on: a tree may hold half of its memory, and while less than a
    /// tenth is free, a tree holding more than a tenth is killed.
    /// </summary>
    public static MemoryLimit ForThisMachine()
    {
        var physical = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new MemoryLimit(physical / 2, 10, physical / 10);
    }

    /// <summary>True when a tree holding <paramref name="treeBytes"/> crosses the hard limit.</summary>
    /// <param name="treeBytes">Memory the tree holds.</param>
    public bool Exceeds(long treeBytes) => treeBytes > TreeBytes;

    /// <summary>True when a tree holding <paramref name="treeBytes"/> is large enough to be killed while the machine is nearly out.</summary>
    /// <param name="treeBytes">Memory the tree holds.</param>
    public bool CountsUnderPressure(long treeBytes) => treeBytes >= PressureTreeBytes;

    /// <summary>True when <paramref name="freePercent"/> of the machine's memory being free means it is nearly out.</summary>
    /// <param name="freePercent">Free memory in percent; null when unknown, which never counts as pressure.</param>
    public bool UnderPressure(int? freePercent) => freePercent < PressureFreePercent;
}
