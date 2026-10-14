namespace Collector.Engine.State;

/// <summary>
/// Everything about one player's game in one pack. Plain data: serializable, cloneable, and only changed by <see cref="Simulation.GameEngine"/>.
/// Ordinal sorted dictionaries keep serialization (and so <see cref="StateHasher"/>) identical on every host.
/// </summary>
public sealed class PlayerState
{
    public int SchemaVersion { get; set; } = 1;
    public string PackId { get; set; } = "";

    /// <summary>Incremented on every successful command or time advance.</summary>
    public long Version { get; set; }

    /// <summary>Game clock in milliseconds since the game started. Not wall time.</summary>
    public long Time { get; set; }

    public ulong RngState { get; set; }

    /// <summary>
    /// The highest command sequence number the server has applied. The engine never reads it; the server uses it to
    /// skip commands a client resends after a lost response, and the client numbers new commands after it.
    /// </summary>
    public long LastCommandSeq { get; set; }
    public int NextId { get; set; } = 1;

    public int Level { get; set; } = 1;
    public long Xp { get; set; }

    public OrdinalDictionary<long> Wallet { get; set; } = new();
    public OrdinalDictionary<int> Inventory { get; set; } = new();
    public OrdinalDictionary<int> StorageTiers { get; set; } = new();
    public List<string> OwnedParcels { get; set; } = [];

    /// <summary>
    /// How fast production runs: every recipe takes its normal time divided by this. A player setting, 1 by default.
    /// </summary>
    public double Pace { get; set; } = 1;

    /// <summary>Upgrades bought, in purchase order.</summary>
    public List<string> Upgrades { get; set; } = [];

    /// <summary>Per item, how many the farm manager leaves when selling. Items not listed are never sold by the manager.</summary>
    public OrdinalDictionary<int> KeepAtLeast { get; set; } = new();

    public List<Placement> Placements { get; set; } = [];
    public List<OrderSlot> Orders { get; set; } = [];

    public long Balance(string currency) => Wallet.GetValueOrDefault(currency);
    public int Count(string item) => Inventory.GetValueOrDefault(item);
    /// <summary>How many of <paramref name="item"/> helpers must leave in storage.</summary>
    public int Keep(string item) => KeepAtLeast.GetValueOrDefault(item);
    public int StorageTier(string storage) => StorageTiers.GetValueOrDefault(storage);
    public Placement? FindPlacement(int id) => Placements.Find(p => p.Id == id);
}

/// <summary>A producer placed on the map.</summary>
public sealed class Placement
{
    public int Id { get; set; }
    public string Producer { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>Index into the producer's slot tiers. Stored as "tier" so saves from before lanes were separate still load.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("tier")]
    public int SlotTier { get; set; }

    /// <summary>Index into the producer's lane tiers.</summary>
    public int LaneTier { get; set; }

    /// <summary>Running and finished-but-uncollected jobs, in queue order.</summary>
    public List<Job> Jobs { get; set; } = [];

    /// <summary>The recipe most recently started here, kept after collection so it can be repeated.</summary>
    public string? LastRecipe { get; set; }

    /// <summary>Recipes a self-running placement makes in turn. Empty means it repeats <see cref="LastRecipe"/>.</summary>
    public List<string> Plan { get; set; } = [];

    /// <summary>Index into <see cref="Plan"/> of the step started most recently, or -1 before any. The next step follows it.</summary>
    public int PlanLast { get; set; } = -1;

    /// <summary>Stops a self-running placement collecting and starting by itself until the player resumes it.</summary>
    public bool Paused { get; set; }
}

public sealed class Job
{
    public string Recipe { get; set; } = "";
    public long StartsAt { get; set; }
    public long EndsAt { get; set; }

    public bool IsReady(long now) => EndsAt <= now;
}

public sealed class OrderSlot
{
    public Order? Order { get; set; }

    /// <summary>When an empty slot gets a new order.</summary>
    public long AvailableAt { get; set; }
}

public sealed class Order
{
    public int Id { get; set; }
    public OrdinalDictionary<int> Items { get; set; } = new();
    public OrdinalDictionary<long> Rewards { get; set; } = new();
    public long Xp { get; set; }
}

/// <summary>
/// A dictionary that always sorts keys ordinally, including after JSON deserialization
/// (a plain SortedDictionary would come back with the culture-sensitive default comparer).
/// </summary>
public sealed class OrdinalDictionary<TValue> : SortedDictionary<string, TValue>
{
    public OrdinalDictionary() : base(StringComparer.Ordinal) { }
}
