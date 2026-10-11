using Collector.Contracts;
using Collector.Engine.Packs;
using Collector.Engine.Simulation;
using Microsoft.Extensions.Options;

namespace Collector.Server.Packs;

public sealed record LoadedPack(GameDefinitions Definitions, GameEngine Engine, PackBundle Bundle, string Directory);

/// <summary>Loads every pack folder at startup. A broken pack stops the server rather than serving bad rules.</summary>
public sealed class PackRegistry
{
    private readonly Dictionary<string, LoadedPack> _packs = new(StringComparer.Ordinal);

    public PackRegistry(IOptions<CollectorOptions> options, ILogger<PackRegistry> logger)
    {
        Root = options.Value.PacksDirectory ?? Path.Combine(AppContext.BaseDirectory, "packs");
        if (!System.IO.Directory.Exists(Root))
            throw new DirectoryNotFoundException($"Packs directory not found: {Root}");

        foreach (var dir in System.IO.Directory.EnumerateDirectories(Root))
        {
            if (!File.Exists(Path.Combine(dir, PackLoader.ManifestFile)))
                continue;
            var pack = Load(dir).GetAwaiter().GetResult();
            _packs.Add(pack.Definitions.Manifest.Id, pack);
            logger.LogInformation("Loaded pack {PackId} {Version} ({Hash})", pack.Definitions.Manifest.Id, pack.Definitions.Manifest.Version, pack.Definitions.Hash[..12]);
        }
        if (_packs.Count == 0)
            throw new InvalidOperationException($"No packs found in {Root}");
    }

    public string Root { get; }

    public IEnumerable<string> Ids => _packs.Keys;

    public LoadedPack? Find(string packId) => _packs.GetValueOrDefault(packId);

    private static async Task<LoadedPack> Load(string directory)
    {
        var source = new DirectoryPackSource(directory);
        var definitions = await PackLoader.LoadAsync(source);
        var files = new Dictionary<string, string>();
        foreach (var file in PackLoader.Files)
        {
            if (await source.ReadTextAsync(file) is { } text)
                files[file] = text;
        }
        var bundle = new PackBundle(definitions.Manifest.Id, definitions.Hash, files);
        return new LoadedPack(definitions, new GameEngine(definitions), bundle, directory);
    }
}
