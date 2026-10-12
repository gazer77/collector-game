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
    public int NextId { get; set; } = 1;

    public int Level { get; set; } = 1;
    public long Xp { get; set; }

    public OrdinalDictionary<long> Wallet { get; set; } = new();
    public OrdinalDictionary<int> Inventory { get; set; } = new();
    public OrdinalDictionary<int> StorageTiers { get; set; } = new();
    public List<string> OwnedParcels { get; set; } = [];

    /// <summary>Upgrades bought, in purchase order.</summary>
    public List<string> Upgrades { get; set; } = [];

    public List<Placement> Placements { get; set; } = [];
    public List<OrderSlot> Orders { get; set; } = [];

    public long Balance(string currency) => Wallet.GetValueOrDefault(currency);
    public int Count(string item) => Inventory.GetValueOrDefault(item);
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
    public int Tier { get; set; }

    /// <summary>Running and finished-but-uncollected jobs, in queue order.</summary>
    public List<Job> Jobs { get; set; } = [];

    /// <summary>The recipe most recently started here, kept after collection so it can be repeated.</summary>
    public string? LastRecipe { get; set; }
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
