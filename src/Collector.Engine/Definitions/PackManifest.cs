namespace Collector.Engine.Definitions;

/// <summary>Contents of a pack's <c>pack.json</c>: identity, clock rules and starting state.</summary>
public sealed record PackManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "0.1.0";
    public ClockDefinition Clock { get; init; } = new();
    public StartingStateDefinition Start { get; init; } = new();
    public SpeedUpDefinition? SpeedUp { get; init; }
}

public enum ClockMode
{
    /// <summary>Game time follows wall time (subject to the offline policy).</summary>
    RealTime,
    /// <summary>Like RealTime; reserved for packs that lean on automation and offline accrual.</summary>
    Idle,
    /// <summary>Game time advances only via the EndTurn command.</summary>
    Turn,
}

public enum OfflinePolicy
{
    /// <summary>The clock stops when the player has no active session.</summary>
    Paused,
    /// <summary>The clock keeps running while the player is away.</summary>
    Running,
    /// <summary>The clock keeps running while away, up to <see cref="ClockDefinition.OfflineCapSeconds"/>.</summary>
    Capped,
}

public sealed record ClockDefinition
{
    public ClockMode Mode { get; init; } = ClockMode.RealTime;
    public OfflinePolicy Offline { get; init; } = OfflinePolicy.Paused;
    public long OfflineCapSeconds { get; init; }

    /// <summary>Turn mode only: how many seconds of game time one turn represents.</summary>
    public long TurnLengthSeconds { get; init; } = 1;
}

public sealed record StartingStateDefinition
{
    public Dictionary<string, long> Currencies { get; init; } = [];
    public Dictionary<string, int> Items { get; init; } = [];
    public List<StartingPlacement> Placements { get; init; } = [];
}

public sealed record StartingPlacement(string Producer, int X, int Y);

/// <summary>Lets players pay a currency to finish a running job immediately.</summary>
public sealed record SpeedUpDefinition
{
    public required string Currency { get; init; }

    /// <summary>One unit of currency per this many seconds remaining (rounded up).</summary>
    public long SecondsPerUnit { get; init; } = 300;
    public long MinCost { get; init; } = 1;
}
