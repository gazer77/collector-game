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

    /// <summary>Market buy price, overriding the market's buyMultiplier. Zero means use the multiplier; -1 means it can't be bought.</summary>
    public long BuyPrice { get; init; }
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

/// <summary>
/// Anything placed on the map that runs recipes: a field, an animal pen or a building. Each holds a queue of jobs
/// (<see cref="Slots"/>), of which up to <see cref="Lanes"/> run at once; the rest wait their turn. The two are
/// upgraded separately, per placement.
/// </summary>
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

    /// <summary>How many may be owned, by player level. The highest entry at or below the player's level applies. Empty means unlimited.</summary>
    public List<CountLimit> Limits { get; init; } = [];

    /// <summary>Extra allowance on top of <see cref="Limits"/> for each land parcel the player owns.</summary>
    public int LimitPerParcel { get; init; }

    /// <summary>How many jobs can be queued (running or waiting, plus finished goods not yet collected), by upgrade tier. Tier 0 is as built; its cost is ignored.</summary>
    public required List<CapacityTier> Slots { get; init; }

    /// <summary>How many queued jobs run at the same time, by upgrade tier. Tier 0 is as built; its cost is ignored.</summary>
    public required List<CapacityTier> Lanes { get; init; }
}

public sealed record CountLimit(int Level, int Count);

/// <summary>One step of a producer's slot or lane upgrades.</summary>
public sealed record CapacityTier
{
    public required int Count { get; init; }
    public Cost Cost { get; init; } = Cost.Free;
    public int UnlockLevel { get; init; } = 1;
}

/// <summary>Which of a producer's two upgrade paths an <see cref="Commands.UpgradeProducer"/> buys.</summary>
public enum UpgradeTrack
{
    /// <summary>More jobs can be queued.</summary>
    Slots,
    /// <summary>More jobs run at once.</summary>
    Lanes,
}

/// <summary>
/// Equipment bought once that speeds up, or adds yield to, the recipes of some producers
/// (a watering can for fields, a milking machine for cows).
/// </summary>
public sealed record UpgradeDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Asset { get; init; } = "";
    public Cost Cost { get; init; } = Cost.Free;
    public int UnlockLevel { get; init; } = 1;

    /// <summary>Another upgrade that must be owned first (sprinklers after the watering can).</summary>
    public string? Requires { get; init; }

    /// <summary>Producer ids it applies to.</summary>
    public List<string> Producers { get; init; } = [];

    /// <summary>Producer categories it applies to, e.g. "field" or "animal".</summary>
    public List<string> Categories { get; init; } = [];

    /// <summary>Multiplies recipe durations for jobs started after buying it. 0.8 is 20% faster. Stacks multiplicatively.</summary>
    public double DurationMultiplier { get; init; } = 1.0;

    /// <summary>Chance, per collected batch, of one extra of each output. Chances from several upgrades add up (capped at 1).</summary>
    public double BonusChance { get; init; }

    /// <summary>Collecting from one producer it covers collects from every producer it covers.</summary>
    public bool GatherAll { get; init; }

    /// <summary>
    /// Producers it covers collect finished batches by themselves and start their last recipe again,
    /// for as long as inputs, slots and storage allow.
    /// </summary>
    public bool AutoRestart { get; init; }

    /// <summary>Farm-wide: sells anything above the amount the player chose to keep (see PlayerState.KeepAtLeast).</summary>
    public bool SellsExcess { get; init; }

    /// <summary>Farm-wide: delivers any order that can be filled without dipping below what the player keeps.</summary>
    public bool FillsOrders { get; init; }

    /// <summary>Applies to the whole farm rather than to particular producers.</summary>
    public bool IsFarmWide => SellsExcess || FillsOrders;

    public bool AppliesTo(ProducerDefinition producer) =>
        Producers.Contains(producer.Id) || Categories.Contains(producer.Category);
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

    /// <summary>Optional single-key shortcut (a letter or digit) that starts this recipe on the next free producer.</summary>
    public string? Hotkey { get; init; }
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

    /// <summary>Currency for buying from the market. Defaults to <see cref="SellCurrency"/>.</summary>
    public string? BuyCurrency { get; init; }

    /// <summary>
    /// Buy price relative to an item's sell price, rounded up. Zero turns buying off. Keep it above the orders
    /// reward multiplier, or buying goods to fill orders makes money (the validator enforces this).
    /// </summary>
    public double BuyMultiplier { get; init; }
}
