namespace Collector.Engine.Simulation;

/// <summary>
/// SplitMix64 over a state value stored in <see cref="State.PlayerState.RngState"/>, so the same state
/// produces the same random choices on the client and the server. <see cref="System.Random"/> isn't
/// guaranteed stable across .NET versions, so it can't be used here.
/// </summary>
internal ref struct DeterministicRandom
{
    private ref ulong _state;

    public DeterministicRandom(ref ulong state) => _state = ref state;

    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform integer in [min, max] inclusive.</summary>
    public int NextInclusive(int min, int max) => min + (int)(NextUInt64() % (ulong)(max - min + 1));
}
