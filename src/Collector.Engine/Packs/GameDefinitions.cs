using Collector.Engine.Definitions;
using Collector.Engine.State;

namespace Collector.Engine.Packs;

/// <summary>A validated, indexed pack. Immutable and safe to share across players and threads.</summary>
public sealed class GameDefinitions
{
    private readonly Dictionary<string, List<RecipeDefinition>> _recipesByProducer;

    /// <summary>Validates <paramref name="content"/> and indexes it.</summary>
    /// <exception cref="PackValidationException">The pack has errors.</exception>
    public GameDefinitions(PackContent content, string hash = "")
    {
        var errors = PackValidator.Validate(content);
        if (errors.Count > 0)
            throw new PackValidationException(content.Manifest.Id, errors);

        Content = content;
        Hash = hash;
        Currencies = content.Currencies.ToDictionary(c => c.Id);
        Items = content.Items.ToDictionary(i => i.Id);
        Storage = content.Storage.ToDictionary(s => s.Id);
        Producers = content.Producers.ToDictionary(p => p.Id);
        Recipes = content.Recipes.ToDictionary(r => r.Id);
        Parcels = content.Land.Parcels.ToDictionary(p => p.Id);
        Levels = [.. content.Levels.OrderBy(l => l.Level)];
        _recipesByProducer = content.Recipes
            .GroupBy(r => r.Producer)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public PackContent Content { get; }

    /// <summary>Content hash of the pack files, so client and server can confirm they run the same rules.</summary>
    public string Hash { get; }

    public PackManifest Manifest => Content.Manifest;
    public LandDefinition Land => Content.Land;
    public OrdersDefinition? Orders => Content.Orders;
    public MarketDefinition? Market => Content.Market;
    public IReadOnlyDictionary<string, AssetDefinition> Assets => Content.Assets;

    public IReadOnlyDictionary<string, CurrencyDefinition> Currencies { get; }
    public IReadOnlyDictionary<string, ItemDefinition> Items { get; }
    public IReadOnlyDictionary<string, StorageDefinition> Storage { get; }
    public IReadOnlyDictionary<string, ProducerDefinition> Producers { get; }
    public IReadOnlyDictionary<string, RecipeDefinition> Recipes { get; }
    public IReadOnlyDictionary<string, ParcelDefinition> Parcels { get; }

    /// <summary>Levels in ascending order.</summary>
    public IReadOnlyList<LevelDefinition> Levels { get; }

    public IReadOnlyList<RecipeDefinition> RecipesFor(string producerId) =>
        _recipesByProducer.TryGetValue(producerId, out var list) ? list : [];

    /// <summary>The most of <paramref name="producer"/> a player at <paramref name="level"/> may own, or null for no limit.</summary>
    /// <summary>How many of <paramref name="producer"/> the player may own: the level step plus a bonus per owned parcel. Null means unlimited.</summary>
    public static int? LimitFor(ProducerDefinition producer, PlayerState state)
    {
        if (producer.Limits.Count == 0)
            return null;
        var applicable = producer.Limits.Where(l => l.Level <= state.Level).MaxBy(l => l.Level);
        return (applicable?.Count ?? 0) + producer.LimitPerParcel * state.OwnedParcels.Count;
    }
}

public sealed class PackValidationException(string packId, IReadOnlyList<string> errors)
    : Exception($"Pack '{packId}' is invalid:{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", errors)}")
{
    public string PackId { get; } = packId;
    public IReadOnlyList<string> Errors { get; } = errors;
}
