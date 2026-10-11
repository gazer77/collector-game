using Collector.Engine.Definitions;

namespace Collector.Engine.Packs;

/// <summary>A pack exactly as read from its files, before validation and indexing.</summary>
public sealed record PackContent
{
    public required PackManifest Manifest { get; init; }
    public List<CurrencyDefinition> Currencies { get; init; } = [];
    public List<ItemDefinition> Items { get; init; } = [];
    public List<StorageDefinition> Storage { get; init; } = [];
    public List<ProducerDefinition> Producers { get; init; } = [];
    public List<RecipeDefinition> Recipes { get; init; } = [];
    public List<LevelDefinition> Levels { get; init; } = [];
    public required LandDefinition Land { get; init; }
    public OrdersDefinition? Orders { get; init; }
    public MarketDefinition? Market { get; init; }
    public Dictionary<string, AssetDefinition> Assets { get; init; } = [];
}
