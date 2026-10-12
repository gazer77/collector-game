using Collector.Engine.Packs;

namespace Collector.Engine.Tests;

public class PackLoaderTests
{
    [Fact]
    public void Farm_pack_loads_and_validates()
    {
        var farm = TestPacks.Farm;

        Assert.Equal("farm", farm.Manifest.Id);
        Assert.Equal(12, farm.Items.Count);
        Assert.Equal(7, farm.Producers.Count);
        Assert.NotEmpty(farm.Hash);
        Assert.All(farm.Items.Values, i => Assert.True(farm.Assets.ContainsKey(i.Asset)));
    }

    [Fact]
    public async Task Missing_required_file_is_reported()
    {
        var files = TestPacks.FarmFiles();
        files.Remove("recipes.json");

        var ex = await Assert.ThrowsAsync<PackValidationException>(() => TestPacks.LoadAsync(files));

        Assert.Contains(ex.Errors, e => e.StartsWith("recipes.json") && e.Contains("missing"));
    }

    [Fact]
    public async Task Optional_files_may_be_omitted()
    {
        var files = TestPacks.FarmFiles();
        files.Remove("orders.json");
        files.Remove(PackLoader.AssetManifestFile);

        var defs = await TestPacks.LoadAsync(files);

        Assert.Null(defs.Orders);
    }

    [Fact]
    public async Task Buy_prices_that_orders_out_pay_are_rejected()
    {
        var files = TestPacks.FarmFiles();
        files["market.json"] = """{ "sellCurrency": "coins", "buyMultiplier": 1.5 }"""; // orders pay 1.6x

        var ex = await Assert.ThrowsAsync<PackValidationException>(() => TestPacks.LoadAsync(files));

        Assert.Contains(ex.Errors, e => e.StartsWith("items.json") && e.Contains("orders out-pay"));
    }

    [Fact]
    public async Task Upgrades_must_target_real_producers_and_have_sane_effects()
    {
        var files = TestPacks.FarmFiles();
        files["upgrades.json"] = """
            [ { "id": "jetpack", "name": "Jetpack", "producers": ["rocket_silo"], "durationMultiplier": 1.5, "requires": "warp" } ]
            """;

        var ex = await Assert.ThrowsAsync<PackValidationException>(() => TestPacks.LoadAsync(files));

        Assert.Contains(ex.Errors, e => e.Contains("unknown producer 'rocket_silo'"));
        Assert.Contains(ex.Errors, e => e.Contains("durationMultiplier"));
        Assert.Contains(ex.Errors, e => e.Contains("unknown upgrade 'warp'"));
    }

    [Theory]
    [InlineData("\"hotkey\": \"c\"", "\"hotkey\": \"w\"", "already taken")]   // corn steals wheat's key
    [InlineData("\"hotkey\": \"c\"", "\"hotkey\": \"r\"", "already taken")]   // R is reserved for replant
    [InlineData("\"hotkey\": \"c\"", "\"hotkey\": \"Co\"", "one lowercase letter or digit")]
    public async Task Recipe_hotkeys_must_be_single_unique_keys(string find, string replace, string error)
    {
        var files = TestPacks.FarmFiles();
        files["recipes.json"] = files["recipes.json"].Replace(find, replace);

        var ex = await Assert.ThrowsAsync<PackValidationException>(() => TestPacks.LoadAsync(files));

        Assert.Contains(ex.Errors, e => e.Contains(error));
    }

    [Fact]
    public async Task Malformed_json_is_reported_with_file_name()
    {
        var files = TestPacks.FarmFiles();
        files["items.json"] = "[ { \"id\": ";

        var ex = await Assert.ThrowsAsync<PackValidationException>(() => TestPacks.LoadAsync(files));

        Assert.Contains(ex.Errors, e => e.StartsWith("items.json"));
    }

    [Fact]
    public async Task Unknown_fields_are_rejected_to_catch_typos()
    {
        var files = TestPacks.FarmFiles();
        files["market.json"] = """{ "sellCurrency": "coins", "sellCurency": "coins" }""";

        var ex = await Assert.ThrowsAsync<PackValidationException>(() => TestPacks.LoadAsync(files));

        Assert.Contains(ex.Errors, e => e.StartsWith("market.json"));
    }

    [Theory]
    [InlineData("recipes.json", "\"producer\": \"dairy\"", "\"producer\": \"creamery\"", "unknown producer 'creamery'")]
    [InlineData("items.json", "\"storage\": \"silo\"", "\"storage\": \"shed\"", "unknown storage 'shed'")]
    [InlineData("recipes.json", "\"inputs\": { \"milk\": 2 }", "\"inputs\": { \"mlik\": 2 }", "unknown item 'mlik'")]
    [InlineData("pack.json", "\"x\": 6, \"y\": 1", "\"x\": 9, \"y\": 1", "not entirely on starting land")]
    [InlineData("pack.json", "\"x\": 6, \"y\": 1", "\"x\": 3, \"y\": 2", "overlaps another placement")]
    [InlineData("levels.json", "\"xpRequired\": 60", "\"xpRequired\": 10", "must require more XP")]
    [InlineData("recipes.json", "\"durationSeconds\": 600,  \"xp\": 4, \"unlockLevel\": 4", "\"durationSeconds\": 600,  \"xp\": 4, \"unlockLevel\": 2", "before its producer")]
    public async Task Semantic_errors_are_reported(string file, string find, string replace, string expected)
    {
        var files = TestPacks.FarmFiles();
        Assert.Contains(find, files[file]);
        files[file] = files[file].Replace(find, replace);

        var ex = await Assert.ThrowsAsync<PackValidationException>(() => TestPacks.LoadAsync(files));

        Assert.Contains(ex.Errors, e => e.StartsWith(file) && e.Contains(expected));
    }

    [Fact]
    public async Task Hash_ignores_line_endings_but_not_content()
    {
        var unix = TestPacks.FarmFiles().ToDictionary(kv => kv.Key, kv => kv.Value.Replace("\r\n", "\n"));
        var windows = unix.ToDictionary(kv => kv.Key, kv => kv.Value.Replace("\n", "\r\n"));
        var changed = new Dictionary<string, string>(unix) { ["market.json"] = """{ "sellCurrency": "gems" }""" };

        var a = await TestPacks.LoadAsync(unix);
        var b = await TestPacks.LoadAsync(windows);
        var c = await TestPacks.LoadAsync(changed);

        Assert.Equal(a.Hash, b.Hash);
        Assert.NotEqual(a.Hash, c.Hash);
    }

    [Fact]
    public async Task Directory_source_refuses_paths_outside_the_pack()
    {
        var source = new DirectoryPackSource(TestPacks.FarmDirectory);

        await Assert.ThrowsAsync<ArgumentException>(() => source.ReadTextAsync("../farm2/pack.json"));
    }
}
