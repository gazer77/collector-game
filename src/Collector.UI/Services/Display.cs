using Collector.Engine.Definitions;
using Collector.Engine.Packs;

namespace Collector.UI.Services;

/// <summary>Formatting helpers shared by the components.</summary>
public static class Display
{
    /// <summary>"0:42", "4:05", "1h 12m".</summary>
    public static string Duration(long milliseconds)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Emoji(this GameDefinitions defs, string assetKey) =>
        defs.Assets.TryGetValue(assetKey, out var asset) && asset.Emoji is { } emoji ? emoji : "❔";

    public static string? Tint(this GameDefinitions defs, string assetKey) =>
        defs.Assets.TryGetValue(assetKey, out var asset) ? asset.Tint : null;

    public static IEnumerable<(string Asset, string Name, long Amount)> Lines(this GameDefinitions defs, Cost cost) =>
        cost.Currencies.Where(c => c.Value > 0).Select(c => (defs.Currencies[c.Key].Asset, defs.Currencies[c.Key].Name, c.Value))
            .Concat(cost.Items.Where(i => i.Value > 0).Select(i => (defs.Items[i.Key].Asset, defs.Items[i.Key].Name, (long)i.Value)));
}
