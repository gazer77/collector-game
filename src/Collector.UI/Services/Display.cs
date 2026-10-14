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

    /// <summary>A production pace as a speed, e.g. "2×" or "½×".</summary>
    public static string Pace(double pace) => pace switch
    {
        0.25 => "¼×",
        0.5 => "½×",
        _ => pace.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×",
    };

    public static string Emoji(this GameDefinitions defs, string assetKey) =>
        defs.Assets.TryGetValue(assetKey, out var asset) && asset.Emoji is { } emoji ? emoji : "❔";

    public static string? Tint(this GameDefinitions defs, string assetKey) =>
        defs.Assets.TryGetValue(assetKey, out var asset) ? asset.Tint : null;

    /// <summary>"15% faster · 20% chance of +1".</summary>
    public static string Effects(UpgradeDefinition upgrade)
    {
        var parts = new List<string>();
        if (upgrade.DurationMultiplier < 1)
            parts.Add($"{Math.Round((1 - upgrade.DurationMultiplier) * 100)}% faster");
        if (upgrade.BonusChance > 0)
            parts.Add($"{Math.Round(upgrade.BonusChance * 100)}% chance of +1");
        if (upgrade.GatherAll)
            parts.Add("collect one to collect them all");
        if (upgrade.AutoRestart)
            parts.Add("collects and restarts by itself");
        if (upgrade.SellsExcess)
            parts.Add("Sells anything above the amounts you keep (set them in Storage)");
        if (upgrade.FillsOrders)
            parts.Add("Delivers orders as soon as you have the goods");
        return string.Join(" · ", parts);
    }

    /// <summary>What an upgrade applies to: "all fields", "Chicken Coop".</summary>
    public static string Targets(GameDefinitions defs, UpgradeDefinition upgrade) =>
        string.Join(", ", upgrade.Categories.Select(c => c switch
        {
            "field" => "all fields",
            "animal" => "all animals",
            _ => $"all {c}s",
        }).Concat(upgrade.Producers.Select(p => defs.Producers[p].Name)));

    public static IEnumerable<(string Asset, string Name, long Amount)> Lines(this GameDefinitions defs, Cost cost) =>
        cost.Currencies.Where(c => c.Value > 0).Select(c => (defs.Currencies[c.Key].Asset, defs.Currencies[c.Key].Name, c.Value))
            .Concat(cost.Items.Where(i => i.Value > 0).Select(i => (defs.Items[i.Key].Asset, defs.Items[i.Key].Name, (long)i.Value)));
}
