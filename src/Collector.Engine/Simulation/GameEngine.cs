using Collector.Engine.Commands;
using Collector.Engine.Definitions;
using Collector.Engine.Packs;
using Collector.Engine.State;

namespace Collector.Engine.Simulation;

/// <summary>
/// The rules. Stateless apart from the pack definitions, so one instance can serve every player of a pack.
/// Every method is deterministic: the same state plus the same command gives the same result on every host.
/// Commands are validated completely before anything is changed, so a failed command leaves the state untouched.
/// </summary>
public sealed class GameEngine(GameDefinitions definitions)
{
    private const long MsPerSecond = 1000;

    /// <summary>The slowest and fastest production pace a player may choose.</summary>
    public const double MinPace = 0.25, MaxPace = 10;

    public GameDefinitions Definitions { get; } = definitions;

    public PlayerState NewGame(ulong seed)
    {
        var start = Definitions.Manifest.Start;
        var state = new PlayerState { PackId = Definitions.Manifest.Id, RngState = seed };
        foreach (var (currency, amount) in start.Currencies)
            state.Wallet[currency] = amount;
        foreach (var (item, qty) in start.Items)
            state.Inventory[item] = qty;
        state.OwnedParcels.AddRange(Definitions.Land.Parcels.Where(p => p.StartOwned).Select(p => p.Id));
        foreach (var placement in start.Placements)
            state.Placements.Add(new Placement { Id = state.NextId++, Producer = placement.Producer, X = placement.X, Y = placement.Y });

        RefillOrders(state, []);
        return state;
    }

    public CommandResult Apply(PlayerState state, GameCommand command)
    {
        var events = new List<GameEvent>();
        var failure = command switch
        {
            PlaceProducer c => Place(state, c),
            MoveProducer c => Move(state, c),
            StartRecipe c => Start(state, c),
            Collect c => CollectJobs(state, c, events),
            CancelJob c => Cancel(state, c),
            SpeedUp c => Speed(state, c),
            UpgradeProducer c => Upgrade(state, c),
            UpgradeStorage c => Upgrade(state, c),
            BuyParcel c => Buy(state, c),
            BuyUpgrade c => Buy(state, c),
            SellItem c => Sell(state, c),
            BuyItem c => Buy(state, c),
            FillOrder c => Fill(state, c, events),
            DiscardOrder c => Discard(state, c, events),
            EndTurn => Turn(state, events),
            SetPace c => ChangePace(state, c),
            _ => CommandResult.Fail(ErrorCode.InvalidCommand, $"Unsupported command {command.GetType().Name}."),
        };
        if (failure is not null)
            return failure;

        state.Version++;
        return CommandResult.Success(events);
    }

    /// <summary>Moves the game clock forward. Called by the host (server, or the client while predicting), never by the player.</summary>
    public IReadOnlyList<GameEvent> AdvanceTime(PlayerState state, long milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        if (milliseconds == 0)
            return [];
        var events = new List<GameEvent>();
        AdvanceClock(state, milliseconds, events);
        state.Version++;
        return events;
    }

    // ---- Queries (also used by clients) ----

    public int StorageCapacity(PlayerState state, string storage) =>
        Definitions.Storage[storage].Tiers[state.StorageTier(storage)].Capacity;

    public int StorageUsed(PlayerState state, string storage) =>
        state.Inventory.Where(kv => Definitions.Items[kv.Key].Storage == storage).Sum(kv => kv.Value);

    /// <summary>How many jobs <paramref name="placement"/> can hold, running, waiting or finished.</summary>
    public int SlotsOf(Placement placement) =>
        Definitions.Producers[placement.Producer].Slots[placement.SlotTier].Count;

    /// <summary>How many of <paramref name="placement"/>'s jobs run at once (never more than it has slots).</summary>
    public int LanesOf(Placement placement) =>
        Math.Min(SlotsOf(placement), Definitions.Producers[placement.Producer].Lanes[placement.LaneTier].Count);

    /// <summary>The next upgrade on <paramref name="track"/> for <paramref name="placement"/>, or null when it's maxed.</summary>
    public CapacityTier? NextTier(Placement placement, UpgradeTrack track)
    {
        var producer = Definitions.Producers[placement.Producer];
        var (tiers, current) = track == UpgradeTrack.Slots ? (producer.Slots, placement.SlotTier) : (producer.Lanes, placement.LaneTier);
        return current + 1 < tiers.Count ? tiers[current + 1] : null;
    }

    /// <summary>The next level's definition, or null at max level.</summary>
    public LevelDefinition? NextLevel(PlayerState state) =>
        Definitions.Levels.FirstOrDefault(l => l.Level == state.Level + 1);

    /// <summary>What it costs right now to finish the placement's next unfinished job, or null if there's nothing to speed up.</summary>
    public long? SpeedUpCost(PlayerState state, Placement placement)
    {
        if (Definitions.Manifest.SpeedUp is not { } speedUp)
            return null;
        var job = placement.Jobs.Find(j => !j.IsReady(state.Time));
        if (job is null)
            return null;
        var remainingSeconds = (job.EndsAt - state.Time + MsPerSecond - 1) / MsPerSecond;
        return Math.Max(speedUp.MinCost, (remainingSeconds + speedUp.SecondsPerUnit - 1) / speedUp.SecondsPerUnit);
    }

    // ---- Commands ----

    private CommandResult? Place(PlayerState state, PlaceProducer c)
    {
        if (!Definitions.Producers.TryGetValue(c.Producer, out var producer))
            return Unknown("producer", c.Producer);
        if (state.Level < producer.UnlockLevel)
            return Locked(producer.Name, producer.UnlockLevel);
        if (GameDefinitions.LimitFor(producer, state) is { } limit
            && state.Placements.Count(p => p.Producer == producer.Id) >= limit)
            return CommandResult.Fail(ErrorCode.LimitReached, $"You can't build more than {limit} {producer.Name} at your level.");
        if (CheckAffordable(state, producer.Cost) is { } cantAfford)
            return cantAfford;
        if (CheckPosition(state, producer, c.X, c.Y, ignorePlacementId: null) is { } badPosition)
            return badPosition;

        Pay(state, producer.Cost);
        state.Placements.Add(new Placement { Id = state.NextId++, Producer = producer.Id, X = c.X, Y = c.Y });
        return null;
    }

    private CommandResult? Move(PlayerState state, MoveProducer c)
    {
        if (state.FindPlacement(c.PlacementId) is not { } placement)
            return NoPlacement(c.PlacementId);
        if (CheckPosition(state, Definitions.Producers[placement.Producer], c.X, c.Y, placement.Id) is { } badPosition)
            return badPosition;

        placement.X = c.X;
        placement.Y = c.Y;
        return null;
    }

    private CommandResult? Start(PlayerState state, StartRecipe c)
    {
        if (state.FindPlacement(c.PlacementId) is not { } placement)
            return NoPlacement(c.PlacementId);
        if (!Definitions.Recipes.TryGetValue(c.Recipe, out var recipe))
            return Unknown("recipe", c.Recipe);
        if (recipe.Producer != placement.Producer)
            return CommandResult.Fail(ErrorCode.InvalidCommand, $"{recipe.Id} can't be made in {placement.Producer}.");
        if (state.Level < recipe.UnlockLevel)
            return Locked(string.IsNullOrEmpty(recipe.Name) ? Definitions.Items[recipe.Outputs.Keys.First()].Name : recipe.Name, recipe.UnlockLevel);
        if (placement.Jobs.Count >= SlotsOf(placement))
            return CommandResult.Fail(ErrorCode.NoFreeSlot, "No free slot. Collect finished goods first.");
        if (CheckItems(state, recipe.Inputs) is { } missing)
            return missing;

        RemoveItems(state, recipe.Inputs);
        var duration = DurationOf(state, placement, recipe);
        var startsAt = Math.Max(state.Time, Schedule(placement, state.Time));
        placement.Jobs.Add(new Job { Recipe = recipe.Id, StartsAt = startsAt, EndsAt = startsAt + duration });
        placement.LastRecipe = recipe.Id;
        return null;
    }

    private CommandResult? CollectJobs(PlayerState state, Collect c, List<GameEvent> events)
    {
        if (state.FindPlacement(c.PlacementId) is not { } placement)
            return NoPlacement(c.PlacementId);
        var group = GatherGroup(state, placement);
        if (group.SelectMany(p => p.Jobs).FirstOrDefault(j => j.IsReady(state.Time)) is not { } firstReady)
            return CommandResult.Fail(ErrorCode.NotReady, "Nothing is ready to collect yet.");

        var gathered = 0;
        foreach (var target in group)
            gathered += Gather(state, target, events);
        if (gathered == 0)
        {
            var storage = Definitions.Items[Definitions.Recipes[firstReady.Recipe].Outputs.Keys.First()].Storage;
            return CommandResult.Fail(ErrorCode.StorageFull, $"Not enough room in your {Definitions.Storage[storage].Name}.");
        }
        return null;
    }

    /// <summary>
    /// What collecting from <paramref name="placement"/> collects from: itself first, then every other placement
    /// covered by one of its gather-all upgrades.
    /// </summary>
    public IReadOnlyList<Placement> GatherGroup(PlayerState state, Placement placement)
    {
        var sweeps = ActiveUpgrades(state, placement).Where(u => u.GatherAll).ToList();
        if (sweeps.Count == 0)
            return [placement];
        return [placement, .. state.Placements.Where(p => p != placement && sweeps.Any(u => u.AppliesTo(Definitions.Producers[p.Producer])))];
    }

    /// <summary>Whether <paramref name="placement"/> collects and restarts by itself.</summary>
    public bool AutoRestarts(PlayerState state, Placement placement) =>
        ActiveUpgrades(state, placement).Any(u => u.AutoRestart);

    /// <summary>
    /// Collects the placement's finished jobs, in order, until storage runs out. Returns how many came in;
    /// when none do, nothing has changed.
    /// </summary>
    private int Gather(PlayerState state, Placement placement, List<GameEvent> events)
    {
        var ready = placement.Jobs.Where(j => j.IsReady(state.Time)).ToList();
        if (ready.Count == 0)
            return 0;

        var collected = new OrdinalDictionary<int>();
        var bonus = new OrdinalDictionary<int>();
        var bonusChance = BonusChance(state, placement);
        // Roll on a copy so a collect that fails outright leaves the state untouched.
        var rngState = state.RngState;
        var rng = new DeterministicRandom(ref rngState);
        long xp = 0;
        var count = 0;
        foreach (var job in ready)
        {
            var recipe = Definitions.Recipes[job.Recipe];
            if (!Fits(state, recipe.Outputs))
                break;
            IReadOnlyDictionary<string, int> outputs = recipe.Outputs;
            if (bonusChance > 0 && rng.NextDouble() < bonusChance)
            {
                var withBonus = recipe.Outputs.ToDictionary(kv => kv.Key, kv => kv.Value + 1);
                // A bonus that wouldn't fit in storage is simply lost; the batch itself still comes in.
                if (Fits(state, withBonus))
                {
                    outputs = withBonus;
                    foreach (var item in recipe.Outputs.Keys)
                        bonus[item] = bonus.GetValueOrDefault(item) + 1;
                }
            }
            AddItems(state, outputs);
            foreach (var (item, qty) in outputs)
                collected[item] = collected.GetValueOrDefault(item) + qty;
            xp += recipe.Xp;
            placement.Jobs.Remove(job);
            count++;
        }
        if (count == 0)
            return 0;

        state.RngState = rngState;
        events.Add(new ItemsCollected(placement.Id, collected));
        if (bonus.Count > 0)
            events.Add(new BonusYield(placement.Id, bonus));
        GainXp(state, xp, events);
        return count;
    }

    private CommandResult? Cancel(PlayerState state, CancelJob c)
    {
        if (state.FindPlacement(c.PlacementId) is not { } placement)
            return NoPlacement(c.PlacementId);
        if (c.Index < 0 || c.Index >= placement.Jobs.Count)
            return CommandResult.Fail(ErrorCode.NotFound, "There's no job in that queue position.");
        var job = placement.Jobs[c.Index];
        if (job.StartsAt <= state.Time)
            return CommandResult.Fail(ErrorCode.InvalidCommand, "Only jobs that haven't started can be cancelled.");
        var inputs = Definitions.Recipes[job.Recipe].Inputs;
        if (!Fits(state, inputs))
            return CommandResult.Fail(ErrorCode.StorageFull, "There's no room in storage to take the ingredients back.");

        placement.Jobs.RemoveAt(c.Index);
        AddItems(state, inputs);
        // Anything queued behind it moves up.
        Schedule(placement, state.Time);
        return null;
    }

    private CommandResult? Speed(PlayerState state, SpeedUp c)
    {
        if (Definitions.Manifest.SpeedUp is not { } speedUp)
            return CommandResult.Fail(ErrorCode.InvalidCommand, "This game has no speed-ups.");
        if (state.FindPlacement(c.PlacementId) is not { } placement)
            return NoPlacement(c.PlacementId);
        if (SpeedUpCost(state, placement) is not { } cost)
            return CommandResult.Fail(ErrorCode.NotReady, "Nothing is running.");
        if (state.Balance(speedUp.Currency) < cost)
            return CannotAfford(speedUp.Currency);

        state.Wallet[speedUp.Currency] -= cost;
        placement.Jobs.Find(j => !j.IsReady(state.Time))!.EndsAt = state.Time;
        // A lane just freed up, so whatever is queued moves up.
        Schedule(placement, state.Time);
        return null;
    }

    /// <summary>The owned upgrades that affect <paramref name="placement"/>.</summary>
    public IEnumerable<UpgradeDefinition> ActiveUpgrades(PlayerState state, Placement placement) =>
        Definitions.UpgradesFor(placement.Producer).Where(u => state.Upgrades.Contains(u.Id));

    /// <summary>How long <paramref name="recipe"/> takes in <paramref name="placement"/>, after every owned upgrade and the player's pace.</summary>
    public long DurationOf(PlayerState state, Placement placement, RecipeDefinition recipe)
    {
        var multiplier = 1.0;
        foreach (var upgrade in ActiveUpgrades(state, placement))
            multiplier *= upgrade.DurationMultiplier;
        return (long)Math.Round(recipe.DurationSeconds * MsPerSecond * multiplier / state.Pace);
    }

    /// <summary>Chance that a batch collected from <paramref name="placement"/> brings one extra of each output.</summary>
    public double BonusChance(PlayerState state, Placement placement) =>
        Math.Min(1, ActiveUpgrades(state, placement).Sum(u => u.BonusChance));

    /// <summary>
    /// How many new jobs would start working straight away: lanes not running anything, capped by free slots.
    /// </summary>
    public int IdleSlots(PlayerState state, Placement placement)
    {
        var running = placement.Jobs.Count(j => j.StartsAt <= state.Time && !j.IsReady(state.Time));
        return Math.Max(0, Math.Min(LanesOf(placement) - running, SlotsOf(placement) - placement.Jobs.Count));
    }

    /// <summary>
    /// Assigns jobs to lanes in queue order, each to the lane that frees up first. Jobs already running keep their
    /// times; jobs still waiting are moved to the earliest start their lane allows, keeping their duration.
    /// Returns when the next new job could start.
    /// </summary>
    private long Schedule(Placement placement, long now)
    {
        var free = new long[LanesOf(placement)];
        foreach (var job in placement.Jobs)
        {
            var lane = Array.IndexOf(free, free.Min());
            if (job.StartsAt > now)
            {
                var duration = job.EndsAt - job.StartsAt;
                job.StartsAt = Math.Max(now, free[lane]);
                job.EndsAt = job.StartsAt + duration;
            }
            free[lane] = job.EndsAt;
        }
        return free.Min();
    }

    private CommandResult? Upgrade(PlayerState state, UpgradeProducer c)
    {
        if (state.FindPlacement(c.PlacementId) is not { } placement)
            return NoPlacement(c.PlacementId);
        var producer = Definitions.Producers[placement.Producer];
        var what = c.Track == UpgradeTrack.Slots ? "slots" : "lanes";
        if (NextTier(placement, c.Track) is not { } next)
            return CommandResult.Fail(ErrorCode.MaxTier, $"{producer.Name} {what} are fully upgraded.");
        if (state.Level < next.UnlockLevel)
            return Locked($"More {producer.Name} {what}", next.UnlockLevel);
        if (CheckAffordable(state, next.Cost) is { } cantAfford)
            return cantAfford;

        Pay(state, next.Cost);
        if (c.Track == UpgradeTrack.Slots)
            placement.SlotTier++;
        else
            placement.LaneTier++;
        // New lanes (or slots letting existing lanes count) let queued jobs start straight away.
        Schedule(placement, state.Time);
        return null;
    }

    private CommandResult? Upgrade(PlayerState state, UpgradeStorage c)
    {
        if (!Definitions.Storage.TryGetValue(c.Storage, out var storage))
            return Unknown("storage", c.Storage);
        var tier = state.StorageTier(storage.Id);
        if (tier + 1 >= storage.Tiers.Count)
            return CommandResult.Fail(ErrorCode.MaxTier, $"{storage.Name} is fully upgraded.");
        var next = storage.Tiers[tier + 1];
        if (state.Level < next.UnlockLevel)
            return Locked($"{storage.Name} upgrade", next.UnlockLevel);
        if (CheckAffordable(state, next.Cost) is { } cantAfford)
            return cantAfford;

        Pay(state, next.Cost);
        state.StorageTiers[storage.Id] = tier + 1;
        return null;
    }

    private CommandResult? Buy(PlayerState state, BuyParcel c)
    {
        if (!Definitions.Parcels.TryGetValue(c.Parcel, out var parcel))
            return Unknown("parcel", c.Parcel);
        if (state.OwnedParcels.Contains(parcel.Id))
            return CommandResult.Fail(ErrorCode.InvalidCommand, "You already own that land.");
        if (state.Level < parcel.UnlockLevel)
            return Locked("That land", parcel.UnlockLevel);
        if (CheckAffordable(state, parcel.Cost) is { } cantAfford)
            return cantAfford;

        Pay(state, parcel.Cost);
        state.OwnedParcels.Add(parcel.Id);
        return null;
    }

    private CommandResult? Sell(PlayerState state, SellItem c)
    {
        if (!Definitions.Items.TryGetValue(c.Item, out var item))
            return Unknown("item", c.Item);
        if (Definitions.Market is not { } market || item.SellPrice <= 0)
            return CommandResult.Fail(ErrorCode.InvalidCommand, $"{item.Name} can't be sold.");
        if (c.Quantity <= 0)
            return CommandResult.Fail(ErrorCode.InvalidCommand, "Quantity must be positive.");
        var toSell = new Dictionary<string, int> { [item.Id] = c.Quantity };
        if (CheckItems(state, toSell) is { } missing)
            return missing;

        RemoveItems(state, toSell);
        Credit(state, market.SellCurrency, item.SellPrice * c.Quantity);
        return null;
    }

    private CommandResult? Buy(PlayerState state, BuyUpgrade c)
    {
        if (!Definitions.Upgrades.TryGetValue(c.Upgrade, out var upgrade))
            return Unknown("upgrade", c.Upgrade);
        if (state.Upgrades.Contains(upgrade.Id))
            return CommandResult.Fail(ErrorCode.InvalidCommand, $"You already have the {upgrade.Name}.");
        if (state.Level < upgrade.UnlockLevel)
            return Locked(upgrade.Name, upgrade.UnlockLevel);
        if (upgrade.Requires is { } required && !state.Upgrades.Contains(required))
            return CommandResult.Fail(ErrorCode.Locked, $"Get the {Definitions.Upgrades[required].Name} first.");
        if (CheckAffordable(state, upgrade.Cost) is { } cantAfford)
            return cantAfford;

        Pay(state, upgrade.Cost);
        state.Upgrades.Add(upgrade.Id);
        return null;
    }

    private CommandResult? Buy(PlayerState state, BuyItem c)
    {
        if (!Definitions.Items.TryGetValue(c.Item, out var item))
            return Unknown("item", c.Item);
        if (BuyPrice(item) is not { } price)
            return CommandResult.Fail(ErrorCode.InvalidCommand, $"{item.Name} can't be bought.");
        if (state.Level < item.UnlockLevel)
            return Locked(item.Name, item.UnlockLevel);
        if (c.Quantity <= 0)
            return CommandResult.Fail(ErrorCode.InvalidCommand, "Quantity must be positive.");
        var currency = Definitions.Market!.BuyCurrency ?? Definitions.Market.SellCurrency;
        if (state.Balance(currency) < price * c.Quantity)
            return CannotAfford(currency);
        var toBuy = new Dictionary<string, int> { [item.Id] = c.Quantity };
        if (!Fits(state, toBuy))
            return CommandResult.Fail(ErrorCode.StorageFull, $"Not enough room in your {Definitions.Storage[item.Storage].Name}.");

        state.Wallet[currency] -= price * c.Quantity;
        AddItems(state, toBuy);
        return null;
    }

    /// <summary>What one of <paramref name="item"/> costs to buy from the market, or null if it can't be bought.</summary>
    public long? BuyPrice(ItemDefinition item)
    {
        if (Definitions.Market is not { } market || item.BuyPrice < 0)
            return null;
        if (item.BuyPrice > 0)
            return item.BuyPrice;
        if (market.BuyMultiplier <= 0 || item.SellPrice <= 0)
            return null;
        return (long)Math.Ceiling(item.SellPrice * market.BuyMultiplier);
    }

    private CommandResult? Fill(PlayerState state, FillOrder c, List<GameEvent> events)
    {
        if (Definitions.Orders is not { } orders)
            return CommandResult.Fail(ErrorCode.InvalidCommand, "This game has no orders.");
        if (c.Slot < 0 || c.Slot >= state.Orders.Count || state.Orders[c.Slot].Order is not { } order)
            return CommandResult.Fail(ErrorCode.NotFound, "There's no order in that slot.");
        if (CheckItems(state, order.Items) is { } missing)
            return missing;

        RemoveItems(state, order.Items);
        foreach (var (currency, amount) in order.Rewards)
            Credit(state, currency, amount);
        state.Orders[c.Slot] = new OrderSlot { AvailableAt = state.Time + orders.RefillDelaySeconds * MsPerSecond };
        events.Add(new OrderCompleted(c.Slot, order.Id));
        GainXp(state, order.Xp, events);
        RefillOrders(state, events);
        return null;
    }

    private CommandResult? Discard(PlayerState state, DiscardOrder c, List<GameEvent> events)
    {
        if (Definitions.Orders is not { } orders)
            return CommandResult.Fail(ErrorCode.InvalidCommand, "This game has no orders.");
        if (c.Slot < 0 || c.Slot >= state.Orders.Count || state.Orders[c.Slot].Order is null)
            return CommandResult.Fail(ErrorCode.NotFound, "There's no order in that slot.");

        state.Orders[c.Slot] = new OrderSlot { AvailableAt = state.Time + orders.DiscardCooldownSeconds * MsPerSecond };
        RefillOrders(state, events);
        return null;
    }

    private CommandResult? ChangePace(PlayerState state, SetPace c)
    {
        if (!double.IsFinite(c.Pace) || c.Pace < MinPace || c.Pace > MaxPace)
            return CommandResult.Fail(ErrorCode.InvalidCommand, $"Pace must be between {MinPace} and {MaxPace}.");
        if (c.Pace == state.Pace)
            return null;

        // Work already underway keeps the progress it has made: what's left of each job is stretched or squeezed.
        var scale = state.Pace / c.Pace;
        foreach (var placement in state.Placements)
        {
            foreach (var job in placement.Jobs)
            {
                if (job.IsReady(state.Time))
                    continue;
                var from = Math.Max(job.StartsAt, state.Time);
                job.EndsAt = from + (long)Math.Round((job.EndsAt - from) * scale);
            }
            Schedule(placement, state.Time);
        }
        state.Pace = c.Pace;
        return null;
    }

    private CommandResult? Turn(PlayerState state, List<GameEvent> events)
    {
        var clock = Definitions.Manifest.Clock;
        if (clock.Mode != ClockMode.Turn)
            return CommandResult.Fail(ErrorCode.InvalidCommand, "This game doesn't use turns.");
        AdvanceClock(state, clock.TurnLengthSeconds * MsPerSecond, events);
        return null;
    }

    // ---- Shared rules ----

    private void AdvanceClock(PlayerState state, long milliseconds, List<GameEvent> events)
    {
        var end = state.Time + milliseconds;
        // Self-running producers act at the moment each batch finishes, not at the end of the advance,
        // so a long absence plays out the same as watching it happen.
        var auto = state.Placements.Where(p => AutoRestarts(state, p)).ToList();
        var stalled = new HashSet<Placement>();
        while (NextAutoCollect(auto, stalled, end) is { } next)
        {
            state.Time = Math.Max(state.Time, next.At);
            RefillOrders(state, events);
            var gathered = Gather(state, next.Placement, events);
            if (gathered == 0)
            {
                stalled.Add(next.Placement); // storage is full for now
                continue;
            }
            // Something changed in storage, so whatever was stuck may fit now.
            stalled.Clear();
            for (var i = 0; i < gathered; i++)
            {
                if (next.Placement.LastRecipe is not { } recipe || Start(state, new StartRecipe(next.Placement.Id, recipe)) is not null)
                    break;
            }
        }
        state.Time = end;
        RefillOrders(state, events);
    }

    /// <summary>The earliest finished job, up to <paramref name="end"/>, on a self-running placement that isn't stuck.</summary>
    private static (Placement Placement, long At)? NextAutoCollect(List<Placement> auto, HashSet<Placement> stalled, long end)
    {
        (Placement, long)? next = null;
        foreach (var placement in auto)
        {
            if (stalled.Contains(placement))
                continue;
            foreach (var job in placement.Jobs)
            {
                if (job.EndsAt <= end && (next is null || job.EndsAt < next.Value.Item2))
                    next = (placement, job.EndsAt);
            }
        }
        return next;
    }

    private void GainXp(PlayerState state, long xp, List<GameEvent> events)
    {
        if (xp <= 0)
            return;
        state.Xp += xp;
        events.Add(new XpGained(xp));
        var leveled = false;
        while (NextLevel(state) is { } next && state.Xp >= next.XpRequired)
        {
            state.Level = next.Level;
            foreach (var (currency, amount) in next.Rewards)
                Credit(state, currency, amount);
            AddItems(state, next.ItemRewards);
            events.Add(new LevelReached(next.Level, next.Rewards, next.ItemRewards));
            leveled = true;
        }
        if (leveled)
            RefillOrders(state, events);
    }

    private void RefillOrders(PlayerState state, List<GameEvent> events)
    {
        if (Definitions.Orders is not { } orders || state.Level < orders.UnlockLevel)
            return;
        while (state.Orders.Count < orders.Slots)
            state.Orders.Add(new OrderSlot { AvailableAt = state.Time });

        for (var i = 0; i < state.Orders.Count; i++)
        {
            var slot = state.Orders[i];
            if (slot.Order is not null || slot.AvailableAt > state.Time)
                continue;
            if (OrderGenerator.Generate(Definitions, state, orders) is not { } order)
                return; // Nothing orderable yet; try again on the next refill.
            slot.Order = order;
            events.Add(new OrderArrived(i, order.Id));
        }
    }

    private CommandResult? CheckPosition(PlayerState state, ProducerDefinition producer, int x, int y, int? ignorePlacementId)
    {
        var rect = (x, y, producer.Width, producer.Height);
        var land = Definitions.Land;
        if (x < 0 || y < 0 || x + producer.Width > land.Width || y + producer.Height > land.Height)
            return CommandResult.Fail(ErrorCode.InvalidPosition, "That's off the map.");

        var owned = state.OwnedParcels.Select(id => Definitions.Parcels[id]).ToList();
        if (!PackValidator.Cells(rect).All(cell => owned.Any(p => p.Contains(cell.X, cell.Y))))
            return CommandResult.Fail(ErrorCode.InvalidPosition, "You don't own all of that land.");

        foreach (var other in state.Placements)
        {
            if (other.Id == ignorePlacementId)
                continue;
            var otherDef = Definitions.Producers[other.Producer];
            if (PackValidator.Overlaps(rect, (other.X, other.Y, otherDef.Width, otherDef.Height)))
                return CommandResult.Fail(ErrorCode.InvalidPosition, $"That spot is taken by {otherDef.Name}.");
        }
        return null;
    }

    private bool Fits(PlayerState state, IReadOnlyDictionary<string, int> items)
    {
        foreach (var group in items.GroupBy(kv => Definitions.Items[kv.Key].Storage))
        {
            if (StorageUsed(state, group.Key) + group.Sum(kv => kv.Value) > StorageCapacity(state, group.Key))
                return false;
        }
        return true;
    }

    private CommandResult? CheckItems(PlayerState state, IReadOnlyDictionary<string, int> items)
    {
        foreach (var (item, qty) in items)
        {
            if (state.Count(item) < qty)
                return CommandResult.Fail(ErrorCode.MissingItems, $"You need {qty} {Definitions.Items[item].Name} (you have {state.Count(item)}).");
        }
        return null;
    }

    private CommandResult? CheckAffordable(PlayerState state, Cost cost)
    {
        foreach (var (currency, amount) in cost.Currencies)
        {
            if (state.Balance(currency) < amount)
                return CannotAfford(currency);
        }
        return CheckItems(state, cost.Items);
    }

    private static void Pay(PlayerState state, Cost cost)
    {
        foreach (var (currency, amount) in cost.Currencies)
            state.Wallet[currency] = state.Balance(currency) - amount;
        RemoveItems(state, cost.Items);
    }

    private static void Credit(PlayerState state, string currency, long amount) =>
        state.Wallet[currency] = state.Balance(currency) + amount;

    private static void AddItems(PlayerState state, IReadOnlyDictionary<string, int> items)
    {
        foreach (var (item, qty) in items)
            state.Inventory[item] = state.Count(item) + qty;
    }

    private static void RemoveItems(PlayerState state, IReadOnlyDictionary<string, int> items)
    {
        foreach (var (item, qty) in items)
        {
            if (qty == 0)
                continue;
            var left = state.Count(item) - qty;
            if (left == 0)
                state.Inventory.Remove(item);
            else
                state.Inventory[item] = left;
        }
    }

    private static CommandResult Unknown(string kind, string id) =>
        CommandResult.Fail(ErrorCode.UnknownDefinition, $"Unknown {kind} '{id}'.");

    private static CommandResult NoPlacement(int id) =>
        CommandResult.Fail(ErrorCode.NotFound, $"Nothing is placed with id {id}.");

    private static CommandResult Locked(string what, int level) =>
        CommandResult.Fail(ErrorCode.Locked, $"{what} unlocks at level {level}.");

    private CommandResult CannotAfford(string currency) =>
        CommandResult.Fail(ErrorCode.CannotAfford, $"Not enough {Definitions.Currencies[currency].Name}.");
}
