namespace Collector.Engine.Definitions;

/// <summary>A price in any mix of currencies and items.</summary>
public sealed record Cost
{
    public static readonly Cost Free = new();

    public Dictionary<string, long> Currencies { get; init; } = [];
    public Dictionary<string, int> Items { get; init; } = [];

    public bool IsFree => Currencies.Values.All(v => v == 0) && Items.Values.All(v => v == 0);
}

public sealed record CurrencyDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Asset { get; init; } = "";
}

public sealed record ItemDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Asset { get; init; } = "";

    /// <summary>The storage bucket whose capacity this item counts against.</summary>
    public required string Storage { get; init; }

    /// <summary>Base market price. Zero means the item can't be sold directly.</summary>
    public long SellPrice { get; init; }
    public int UnlockLevel { get; init; } = 1;

    /// <summary>Relative chance of appearing in generated orders. Zero excludes it.</summary>
    public int OrderWeight { get; init; } = 1;
    public List<string> Tags { get; init; } = [];
}

public sealed record StorageDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Asset { get; init; } = "";

    /// <summary>Tier 0 is the starting tier; its cost is ignored.</summary>
    public required List<StorageTier> Tiers { get; init; }
}

public sealed record StorageTier
{
    public required int Capacity { get; init; }
    public Cost Cost { get; init; } = Cost.Free;
    public int UnlockLevel { get; init; } = 1;
}

public enum QueueMode
{
    /// <summary>All slots run at the same time (fields, animal pens).</summary>
    Parallel,
    /// <summary>Slots are a queue; each job starts when the previous one ends (processing buildings).</summary>
    Sequential,
}

/// <summary>Anything placed on the map that runs recipes: a field, an animal pen or a building.</summary>
public sealed record ProducerDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Asset { get; init; } = "";

    /// <summary>Free-form grouping for the UI (e.g. "field", "animal", "building").</summary>
    public string Category { get; init; } = "";
    public int Width { get; init; } = 1;
    public int Height { get; init; } = 1;
    public Cost Cost { get; init; } = Cost.Free;
    public int UnlockLevel { get; init; } = 1;
    public QueueMode QueueMode { get; init; } = QueueMode.Parallel;

    /// <summary>How many may be owned, by player level. The highest entry at or below the player's level applies. Empty means unlimited.</summary>
    public List<CountLimit> Limits { get; init; } = [];

    /// <summary>Tier 0 is the as-built tier; its cost is ignored.</summary>
    public required List<ProducerTier> Tiers { get; init; }
}

public sealed record CountLimit(int Level, int Count);

public sealed record ProducerTier
{
    public required int Slots { get; init; }

    /// <summary>For sequential producers, how many queued jobs run at the same time. Ignored by parallel producers, which run every slot.</summary>
    public int Lanes { get; init; } = 1;

    public double DurationMultiplier { get; init; } = 1.0;
    public Cost Cost { get; init; } = Cost.Free;
    public int UnlockLevel { get; init; } = 1;
}

public sealed record RecipeDefinition
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public required string Producer { get; init; }
    public Dictionary<string, int> Inputs { get; init; } = [];
    public required Dictionary<string, int> Outputs { get; init; }
    public required long DurationSeconds { get; init; }
    public long Xp { get; init; }
    public int UnlockLevel { get; init; } = 1;
}

public sealed record LevelDefinition
{
    public required int Level { get; init; }

    /// <summary>Total (cumulative) XP needed to reach this level.</summary>
    public required long XpRequired { get; init; }
    public Dictionary<string, long> Rewards { get; init; } = [];

    /// <summary>Items granted on reaching this level (e.g. seeds for a newly unlocked crop). May exceed storage capacity.</summary>
    public Dictionary<string, int> ItemRewards { get; init; } = [];
}

public sealed record LandDefinition
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required List<ParcelDefinition> Parcels { get; init; }
}

public sealed record ParcelDefinition
{
    public required string Id { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public Cost Cost { get; init; } = Cost.Free;
    public int UnlockLevel { get; init; } = 1;
    public bool StartOwned { get; init; }

    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

public sealed record OrdersDefinition
{
    public int Slots { get; init; } = 3;
    public int UnlockLevel { get; init; } = 1;
    public int MinItems { get; init; } = 1;
    public int MaxItems { get; init; } = 3;
    public int MinQuantity { get; init; } = 1;
    public int MaxQuantity { get; init; } = 3;
    public required string RewardCurrency { get; init; }

    /// <summary>Order payout relative to selling the same goods on the market.</summary>
    public double RewardMultiplier { get; init; } = 1.5;
    public double XpPerCurrency { get; init; } = 0.25;
    public long RefillDelaySeconds { get; init; }
    public long DiscardCooldownSeconds { get; init; } = 300;
    public OrderBonusDefinition? Bonus { get; init; }
}

/// <summary>A chance for an order to also pay out an extra currency (e.g. gems).</summary>
public sealed record OrderBonusDefinition
{
    public required string Currency { get; init; }
    public double Chance { get; init; }
    public long Amount { get; init; } = 1;
}

public sealed record MarketDefinition
{
    public required string SellCurrency { get; init; }
}
