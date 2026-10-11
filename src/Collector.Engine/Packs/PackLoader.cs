using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Collector.Engine.Definitions;
using Collector.Engine.Serialization;

namespace Collector.Engine.Packs;

/// <summary>Reads a pack's JSON files from an <see cref="IPackSource"/> and produces validated <see cref="GameDefinitions"/>.</summary>
public static class PackLoader
{
    public const string ManifestFile = "pack.json";
    public const string AssetManifestFile = "assets/manifest.json";

    /// <summary>Every file the loader reads, in hashing order.</summary>
    public static readonly IReadOnlyList<string> Files =
    [
        ManifestFile, "currencies.json", "items.json", "storage.json", "producers.json",
        "recipes.json", "levels.json", "land.json", "orders.json", "market.json", AssetManifestFile,
    ];

    private static readonly HashSet<string> OptionalFiles = ["orders.json", "market.json", AssetManifestFile];

    /// <exception cref="PackValidationException">Files are missing, malformed or inconsistent.</exception>
    public static async Task<GameDefinitions> LoadAsync(IPackSource source, CancellationToken cancellationToken = default)
    {
        var texts = new Dictionary<string, string?>();
        foreach (var file in Files)
            texts[file] = await source.ReadTextAsync(file, cancellationToken);

        var errors = new List<string>();
        T? Read<T>(string file) where T : class
        {
            var text = texts[file];
            if (text is null)
            {
                if (!OptionalFiles.Contains(file))
                    errors.Add($"{file}: file is missing.");
                return null;
            }
            try
            {
                var value = JsonSerializer.Deserialize<T>(text, EngineJson.Options);
                if (value is null)
                    errors.Add($"{file}: file is empty.");
                return value;
            }
            catch (JsonException ex)
            {
                errors.Add($"{file}: {ex.Message}");
                return null;
            }
        }

        var manifest = Read<PackManifest>(ManifestFile);
        var currencies = Read<List<CurrencyDefinition>>("currencies.json");
        var items = Read<List<ItemDefinition>>("items.json");
        var storage = Read<List<StorageDefinition>>("storage.json");
        var producers = Read<List<ProducerDefinition>>("producers.json");
        var recipes = Read<List<RecipeDefinition>>("recipes.json");
        var levels = Read<List<LevelDefinition>>("levels.json");
        var land = Read<LandDefinition>("land.json");
        var orders = Read<OrdersDefinition>("orders.json");
        var market = Read<MarketDefinition>("market.json");
        var assets = Read<Dictionary<string, AssetDefinition>>(AssetManifestFile);

        if (errors.Count > 0 || manifest is null || land is null)
            throw new PackValidationException(manifest?.Id ?? "?", errors);

        var content = new PackContent
        {
            Manifest = manifest,
            Currencies = currencies ?? [],
            Items = items ?? [],
            Storage = storage ?? [],
            Producers = producers ?? [],
            Recipes = recipes ?? [],
            Levels = levels ?? [],
            Land = land,
            Orders = orders,
            Market = market,
            Assets = assets ?? [],
        };
        return new GameDefinitions(content, ComputeHash(texts));
    }

    private static string ComputeHash(Dictionary<string, string?> texts)
    {
        var builder = new StringBuilder();
        foreach (var file in Files)
        {
            // Normalize line endings so a Windows checkout and a Linux server hash the same pack identically.
            var text = texts[file]?.Replace("\r\n", "\n");
            builder.Append(file).Append('\0').Append(text ?? "<missing>").Append('\0');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
