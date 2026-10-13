using Collector.Engine.Definitions;

namespace Collector.Engine.Packs;

/// <summary>Semantic checks across a pack's files: references resolve, numbers make sense, the starting state is legal.</summary>
public static class PackValidator
{
    public static IReadOnlyList<string> Validate(PackContent pack)
    {
        var errors = new List<string>();
        void Error(string file, string message) => errors.Add($"{file}: {message}");

        if (string.IsNullOrWhiteSpace(pack.Manifest.Id))
            Error("pack.json", "id is required.");

        var currencyIds = UniqueIds(pack.Currencies, c => c.Id, "currencies.json", errors);
        var itemIds = UniqueIds(pack.Items, i => i.Id, "items.json", errors);
        var storageIds = UniqueIds(pack.Storage, s => s.Id, "storage.json", errors);
        var producerIds = UniqueIds(pack.Producers, p => p.Id, "producers.json", errors);
        UniqueIds(pack.Recipes, r => r.Id, "recipes.json", errors);
        UniqueIds(pack.Land.Parcels, p => p.Id, "land.json", errors);

        void CheckAsset(string file, string owner, string asset)
        {
            if (asset.Length > 0 && pack.Assets.Count > 0 && !pack.Assets.ContainsKey(asset))
                Error(file, $"{owner} uses asset '{asset}', which is not in {PackLoader.AssetManifestFile}.");
        }

        void CheckCurrencies(string file, string owner, IReadOnlyDictionary<string, long> amounts)
        {
            foreach (var (id, amount) in amounts)
            {
                if (!currencyIds.Contains(id))
                    Error(file, $"{owner} references unknown currency '{id}'.");
                if (amount < 0)
                    Error(file, $"{owner} has a negative amount of '{id}'.");
            }
        }

        void CheckItems(string file, string owner, IReadOnlyDictionary<string, int> quantities, bool positive = true)
        {
            foreach (var (id, qty) in quantities)
            {
                if (!itemIds.Contains(id))
                    Error(file, $"{owner} references unknown item '{id}'.");
                if (positive ? qty <= 0 : qty < 0)
                    Error(file, $"{owner} has an invalid quantity {qty} of '{id}'.");
            }
        }

        void CheckCost(string file, string owner, Cost cost)
        {
            CheckCurrencies(file, owner, cost.Currencies);
            CheckItems(file, owner, cost.Items, positive: false);
        }

        foreach (var c in pack.Currencies)
            CheckAsset("currencies.json", $"Currency '{c.Id}'", c.Asset);

        foreach (var item in pack.Items)
        {
            var owner = $"Item '{item.Id}'";
            if (!storageIds.Contains(item.Storage))
                Error("items.json", $"{owner} uses unknown storage '{item.Storage}'.");
            if (item.SellPrice < 0 || item.OrderWeight < 0)
                Error("items.json", $"{owner} has a negative sellPrice or orderWeight.");
            CheckAsset("items.json", owner, item.Asset);
        }

        foreach (var storage in pack.Storage)
        {
            var owner = $"Storage '{storage.Id}'";
            if (storage.Tiers.Count == 0)
                Error("storage.json", $"{owner} needs at least one tier.");
            for (var i = 0; i < storage.Tiers.Count; i++)
            {
                var tier = storage.Tiers[i];
                if (tier.Capacity <= 0)
                    Error("storage.json", $"{owner} tier {i} must have a positive capacity.");
                if (i > 0 && tier.Capacity < storage.Tiers[i - 1].Capacity)
                    Error("storage.json", $"{owner} tier {i} has less capacity than tier {i - 1}.");
                CheckCost("storage.json", $"{owner} tier {i}", tier.Cost);
            }
            CheckAsset("storage.json", owner, storage.Asset);
        }

        foreach (var producer in pack.Producers)
        {
            var owner = $"Producer '{producer.Id}'";
            if (producer.Width < 1 || producer.Height < 1)
                Error("producers.json", $"{owner} must be at least 1x1.");
            if (producer.LimitPerParcel < 0)
                Error("producers.json", $"{owner} limitPerParcel can't be negative.");
            foreach (var (track, tiers) in new[] { ("slots", producer.Slots), ("lanes", producer.Lanes) })
            {
                if (tiers.Count == 0)
                    Error("producers.json", $"{owner} needs at least one {track} tier.");
                for (var i = 0; i < tiers.Count; i++)
                {
                    if (tiers[i].Count < 1)
                        Error("producers.json", $"{owner} {track} tier {i} must be at least 1.");
                    if (i > 0 && tiers[i].Count <= tiers[i - 1].Count)
                        Error("producers.json", $"{owner} {track} tier {i} must be more than tier {i - 1}.");
                    CheckCost("producers.json", $"{owner} {track} tier {i}", tiers[i].Cost);
                }
            }
            // Lanes beyond the slots can never be used.
            if (producer.Slots.Count > 0 && producer.Lanes.Count > 0 && producer.Lanes[^1].Count > producer.Slots[^1].Count)
                Error("producers.json", $"{owner} has more lanes ({producer.Lanes[^1].Count}) than it can ever have slots ({producer.Slots[^1].Count}).");
            if (producer.Limits.Any(l => l.Count < 0))
                Error("producers.json", $"{owner} has a negative limit.");
            CheckCost("producers.json", owner, producer.Cost);
            CheckAsset("producers.json", owner, producer.Asset);
            if (!pack.Recipes.Any(r => r.Producer == producer.Id))
                Error("producers.json", $"{owner} has no recipes.");
        }

        // Hotkeys act on the selected producer, so they only need to be unique within one producer.
        // R is the client's global "replant" key.
        foreach (var group in pack.Recipes.Where(r => r.Hotkey is not null).GroupBy(r => r.Producer))
        {
            var hotkeys = new HashSet<string> { "r" };
            foreach (var recipe in group)
            {
                if (recipe.Hotkey is not { Length: 1 } key || !char.IsAsciiLetterOrDigit(key[0]) || key != key.ToLowerInvariant())
                    Error("recipes.json", $"Recipe '{recipe.Id}' hotkey must be one lowercase letter or digit.");
                else if (!hotkeys.Add(key))
                    Error("recipes.json", $"Recipe '{recipe.Id}' hotkey '{key}' is already taken in {recipe.Producer}.");
            }
        }

        foreach (var recipe in pack.Recipes)
        {
            var owner = $"Recipe '{recipe.Id}'";
            var producer = pack.Producers.FirstOrDefault(p => p.Id == recipe.Producer);
            if (producer is null)
                Error("recipes.json", $"{owner} uses unknown producer '{recipe.Producer}'.");
            else if (recipe.UnlockLevel < producer.UnlockLevel)
                Error("recipes.json", $"{owner} unlocks at level {recipe.UnlockLevel}, before its producer '{producer.Id}' (level {producer.UnlockLevel}).");
            if (recipe.Outputs.Count == 0)
                Error("recipes.json", $"{owner} must have at least one output.");
            if (recipe.DurationSeconds < 0 || recipe.Xp < 0)
                Error("recipes.json", $"{owner} has a negative duration or xp.");
            CheckItems("recipes.json", $"{owner} inputs", recipe.Inputs);
            CheckItems("recipes.json", $"{owner} outputs", recipe.Outputs);
        }

        var levels = pack.Levels.OrderBy(l => l.Level).ToList();
        if (levels.Count == 0 || levels[0].Level != 1 || levels[0].XpRequired != 0)
            Error("levels.json", "Levels must start at level 1 with xpRequired 0.");
        for (var i = 1; i < levels.Count; i++)
        {
            if (levels[i].Level != levels[i - 1].Level + 1)
                Error("levels.json", $"Levels must be consecutive; found {levels[i - 1].Level} then {levels[i].Level}.");
            if (levels[i].XpRequired <= levels[i - 1].XpRequired)
                Error("levels.json", $"Level {levels[i].Level} must require more XP than level {levels[i - 1].Level}.");
        }
        foreach (var level in levels)
{
            CheckCurrencies("levels.json", $"Level {level.Level} rewards", level.Rewards);
            CheckItems("levels.json", $"Level {level.Level} itemRewards", level.ItemRewards);
        }

        var land = pack.Land;
        if (land.Width < 1 || land.Height < 1)
            Error("land.json", "Map width and height must be positive.");
        for (var i = 0; i < land.Parcels.Count; i++)
        {
            var parcel = land.Parcels[i];
            var owner = $"Parcel '{parcel.Id}'";
            if (parcel.Width < 1 || parcel.Height < 1 || parcel.X < 0 || parcel.Y < 0
                || parcel.X + parcel.Width > land.Width || parcel.Y + parcel.Height > land.Height)
                Error("land.json", $"{owner} must lie inside the {land.Width}x{land.Height} map.");
            for (var j = 0; j < i; j++)
            {
                if (Overlaps(parcel, land.Parcels[j]))
                    Error("land.json", $"{owner} overlaps parcel '{land.Parcels[j].Id}'.");
            }
            CheckCost("land.json", owner, parcel.Cost);
        }
        var startParcels = land.Parcels.Where(p => p.StartOwned).ToList();
        if (startParcels.Count == 0)
            Error("land.json", "At least one parcel must have startOwned: true.");

        var start = pack.Manifest.Start;
        CheckCurrencies("pack.json", "start", start.Currencies);
        CheckItems("pack.json", "start", start.Items);
        foreach (var group in start.Items.Where(kv => itemIds.Contains(kv.Key))
                     .GroupBy(kv => pack.Items.First(i => i.Id == kv.Key).Storage))
        {
            var storage = pack.Storage.FirstOrDefault(s => s.Id == group.Key);
            var total = group.Sum(kv => kv.Value);
            if (storage is { Tiers.Count: > 0 } && total > storage.Tiers[0].Capacity)
                Error("pack.json", $"Starting items in storage '{group.Key}' ({total}) exceed its capacity ({storage.Tiers[0].Capacity}).");
        }
        var placed = new List<(int X, int Y, int W, int H)>();
        foreach (var placement in start.Placements)
        {
            var producer = pack.Producers.FirstOrDefault(p => p.Id == placement.Producer);
            if (producer is null)
            {
                Error("pack.json", $"Starting placement uses unknown producer '{placement.Producer}'.");
                continue;
            }
            var rect = (placement.X, placement.Y, producer.Width, producer.Height);
            if (!Cells(rect).All(c => startParcels.Any(p => p.Contains(c.X, c.Y))))
                Error("pack.json", $"Starting '{producer.Id}' at ({placement.X},{placement.Y}) is not entirely on starting land.");
            if (placed.Any(r => Overlaps(r, rect)))
                Error("pack.json", $"Starting '{producer.Id}' at ({placement.X},{placement.Y}) overlaps another placement.");
            placed.Add(rect);
        }

        if (pack.Manifest.SpeedUp is { } speedUp)
        {
            if (!currencyIds.Contains(speedUp.Currency))
                Error("pack.json", $"speedUp uses unknown currency '{speedUp.Currency}'.");
            if (speedUp.SecondsPerUnit < 1)
                Error("pack.json", "speedUp.secondsPerUnit must be at least 1.");
        }
        if (pack.Manifest.Clock.TurnLengthSeconds < 1)
            Error("pack.json", "clock.turnLengthSeconds must be at least 1.");

        if (pack.Orders is { } orders)
        {
            if (orders.Slots < 1)
                Error("orders.json", "slots must be at least 1.");
            if (orders.MinItems < 1 || orders.MaxItems < orders.MinItems)
                Error("orders.json", "Need 1 <= minItems <= maxItems.");
            if (orders.MinQuantity < 1 || orders.MaxQuantity < orders.MinQuantity)
                Error("orders.json", "Need 1 <= minQuantity <= maxQuantity.");
            if (!currencyIds.Contains(orders.RewardCurrency))
                Error("orders.json", $"rewardCurrency '{orders.RewardCurrency}' is not a currency.");
            if (orders.Bonus is { } bonus && !currencyIds.Contains(bonus.Currency))
                Error("orders.json", $"bonus currency '{bonus.Currency}' is not a currency.");
        }

        if (pack.Market is { } market && !currencyIds.Contains(market.SellCurrency))
            Error("market.json", $"sellCurrency '{market.SellCurrency}' is not a currency.");
        if (pack.Market is null && pack.Items.Any(i => i.SellPrice > 0))
            Error("market.json", "Items have sell prices but the pack has no market.json.");
        var upgradeIds = UniqueIds(pack.Upgrades, u => u.Id, "upgrades.json", errors);
        var categories = pack.Producers.Select(p => p.Category).ToHashSet();
        foreach (var upgrade in pack.Upgrades)
        {
            var owner = $"Upgrade '{upgrade.Id}'";
            CheckAsset("upgrades.json", owner, upgrade.Asset);
            CheckCost("upgrades.json", owner, upgrade.Cost);
            var targeted = upgrade.Producers.Count > 0 || upgrade.Categories.Count > 0;
            var producerEffects = upgrade.DurationMultiplier < 1 || upgrade.BonusChance > 0 || upgrade.GatherAll || upgrade.AutoRestart;
            if (upgrade.IsFarmWide && (targeted || producerEffects))
                Error("upgrades.json", $"{owner} is a farm-wide helper, so it can't name producers, categories or producer effects.");
            else if (!upgrade.IsFarmWide && !targeted)
                Error("upgrades.json", $"{owner} must name producers or categories to apply to.");
            if (upgrade.SellsExcess && pack.Market is null)
                Error("upgrades.json", $"{owner} sells goods, but the pack has no market.json.");
            if (upgrade.FillsOrders && pack.Orders is null)
                Error("upgrades.json", $"{owner} fills orders, but the pack has no orders.json.");
            foreach (var producer in upgrade.Producers.Where(p => !producerIds.Contains(p)))
                Error("upgrades.json", $"{owner} names unknown producer '{producer}'.");
            foreach (var category in upgrade.Categories.Where(c => !categories.Contains(c)))
                Error("upgrades.json", $"{owner} names category '{category}', which no producer has.");
            if (upgrade.Requires is { } required && (required == upgrade.Id || !upgradeIds.Contains(required)))
                Error("upgrades.json", $"{owner} requires unknown upgrade '{required}'.");
            if (upgrade.DurationMultiplier is <= 0 or > 1)
                Error("upgrades.json", $"{owner} durationMultiplier must be above 0 and at most 1.");
            if (upgrade.BonusChance is < 0 or > 1)
                Error("upgrades.json", $"{owner} bonusChance must be between 0 and 1.");
        }

        if (pack.Market is { } m)
        {
            var buyCurrency = m.BuyCurrency ?? m.SellCurrency;
            if (!currencyIds.Contains(buyCurrency))
                Error("market.json", $"buyCurrency '{buyCurrency}' is not a currency.");
            if (m.BuyMultiplier < 0)
                Error("market.json", "buyMultiplier can't be negative.");
            foreach (var item in pack.Items)
            {
                if (item.BuyPrice < -1)
                    Error("items.json", $"Item '{item.Id}' buyPrice must be -1 (not for sale), 0 (use buyMultiplier) or positive.");
                var buy = item.BuyPrice > 0 ? item.BuyPrice
                    : item.BuyPrice == 0 && m.BuyMultiplier > 0 ? (long)Math.Ceiling(item.SellPrice * m.BuyMultiplier) : 0;
                if (buy <= 0)
                    continue;
                // Buying must never pay: not by selling straight back, and not by filling orders with bought goods.
                if (buyCurrency == m.SellCurrency && buy < item.SellPrice)
                    Error("items.json", $"Item '{item.Id}' buys for {buy} but sells for {item.SellPrice}.");
                if (pack.Orders is { } o && o.RewardCurrency == buyCurrency && buy <= item.SellPrice * o.RewardMultiplier)
                    Error("items.json", $"Item '{item.Id}' buys for {buy}, which orders out-pay ({item.SellPrice} × {o.RewardMultiplier}).");
            }
        }

        return errors;
    }

    private static HashSet<string> UniqueIds<T>(IEnumerable<T> items, Func<T, string> id, string file, List<string> errors)
    {
        var seen = new HashSet<string>();
        foreach (var item in items)
        {
            var key = id(item);
            if (string.IsNullOrWhiteSpace(key))
                errors.Add($"{file}: an entry is missing its id.");
            else if (!seen.Add(key))
                errors.Add($"{file}: duplicate id '{key}'.");
        }
        return seen;
    }

    private static bool Overlaps(ParcelDefinition a, ParcelDefinition b) =>
        Overlaps((a.X, a.Y, a.Width, a.Height), (b.X, b.Y, b.Width, b.Height));

    internal static bool Overlaps((int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b) =>
        a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    internal static IEnumerable<(int X, int Y)> Cells((int X, int Y, int W, int H) r)
    {
        for (var x = r.X; x < r.X + r.W; x++)
            for (var y = r.Y; y < r.Y + r.H; y++)
                yield return (x, y);
    }
}
