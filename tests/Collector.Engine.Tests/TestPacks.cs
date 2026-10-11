using Collector.Engine.Packs;
using Collector.Engine.Simulation;
using Collector.Engine.State;

namespace Collector.Engine.Tests;

internal static class TestPacks
{
    public static readonly string FarmDirectory = Path.Combine(AppContext.BaseDirectory, "packs", "farm");

    private static readonly Lazy<GameDefinitions> FarmDefinitions =
        new(() => PackLoader.LoadAsync(new DirectoryPackSource(FarmDirectory)).GetAwaiter().GetResult());

    public static GameDefinitions Farm => FarmDefinitions.Value;

    public static GameEngine FarmEngine() => new(Farm);

    /// <summary>The farm pack's files as editable text, for building broken or variant packs.</summary>
    public static Dictionary<string, string> FarmFiles()
    {
        var files = new Dictionary<string, string>();
        foreach (var file in PackLoader.Files)
        {
            var path = Path.Combine(FarmDirectory, file);
            if (File.Exists(path))
                files[file] = File.ReadAllText(path);
        }
        return files;
    }

    public static Task<GameDefinitions> LoadAsync(Dictionary<string, string> files) =>
        PackLoader.LoadAsync(new MemoryPackSource(files));

    public static Placement First(this PlayerState state, string producer) =>
        state.Placements.First(p => p.Producer == producer);
}
