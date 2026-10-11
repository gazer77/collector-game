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
            SpeedUp c => Speed(state, c),
            UpgradeProducer c => Upgrade(state, c),
            UpgradeStorage c => Upgrade(state, c),
            BuyParcel c => Buy(state, c),
            SellItem c => Sell(state, c),
            FillOrder c => Fill(state, c, events),
            DiscardOrder c => Discard(state, c, events),
            EndTurn => Turn(state, events),
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

    public ProducerTier TierOf(Placement placement) =>
        Definitions.Producers[placement.Producer].Tiers[placement.Tier];

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
        if (GameDefinitions.LimitFor(producer, state.Level) is { } limit
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
            return Locked(recipe.Id, recipe.UnlockLevel);
        var tier = TierOf(placement);
        if (placement.Jobs.Count >= tier.Slots)
            return CommandResult.Fail(ErrorCode.NoFreeSlot, "No free slot. Collect finished goods first.");
        if (CheckItems(state, recipe.Inputs) is { } missing)
            return missing;

        RemoveItems(state, recipe.Inputs);
        var duration = (long)Math.Round(recipe.DurationSeconds * MsPerSecond * tier.DurationMultiplier);
        var queueMode = Definitions.Producers[placement.Producer].QueueMode;
        var startsAt = queueMode == QueueMode.Sequential && placement.Jobs.Count > 0
            ? Math.Max(state.Time, placement.Jobs[^1].EndsAt)
            : state.Time;
        placement.Jobs.Add(new Job { Recipe = recipe.Id, StartsAt = startsAt, EndsAt = startsAt + duration });
        return null;
    }

    private CommandResult? CollectJobs(PlayerState state, Collect c, List<GameEvent> events)
    {
        if (state.FindPlacement(c.PlacementId) is not { } placement)
            return NoPlacement(c.PlacementId);
        var ready = placement.Jobs.Where(j => j.IsReady(state.Time)).ToList();
        if (ready.Count == 0)
            return CommandResult.Fail(ErrorCode.NotReady, "Nothing is ready to collect yet.");

        var collected = new OrdinalDictionary<int>();
        long xp = 0;
        foreach (var job in ready)
        {
            var recipe = Definitions.Recipes[job.Recipe];
            if (!Fits(state, recipe.Outputs))
                break;
            AddItems(state, recipe.Outputs);
            foreach (var (item, qty) in recipe.Outputs)
                collected[item] = collected.GetValueOrDefault(item) + qty;
            xp += recipe.Xp;
            placement.Jobs.Remove(job);
        }
        if (collected.Count == 0)
        {
            var storage = Definitions.Items[Definitions.Recipes[ready[0].Recipe].Outputs.Keys.First()].Storage;
            return CommandResult.Fail(ErrorCode.StorageFull, $"Not enough room in your {Definitions.Storage[storage].Name}.");
        }

        events.Add(new ItemsCollected(placement.Id, collected));
        GainXp(state, xp, events);
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
        var index = placement.Jobs.FindIndex(j => !j.IsReady(state.Time));
        var job = placement.Jobs[index];
        var shift = job.EndsAt - state.Time;
        job.EndsAt = state.Time;
        if (Definitions.Producers[placement.Producer].QueueMode == QueueMode.Sequential)
        {
            // Everything queued behind it moves up by the same amount.
            foreach (var later in placement.Jobs.Skip(index + 1))
            {
                later.StartsAt -= shift;
                later.EndsAt -= shift;
            }
        }
        return null;
    }

    private CommandResult? Upgrade(PlayerState state, UpgradeProducer c)
    {
        if (state.FindPlacement(c.PlacementId) is not { } placement)
            return NoPlacement(c.PlacementId);
        var producer = Definitions.Producers[placement.Producer];
        if (placement.Tier + 1 >= producer.Tiers.Count)
            return CommandResult.Fail(ErrorCode.MaxTier, $"{producer.Name} is fully upgraded.");
        var next = producer.Tiers[placement.Tier + 1];
        if (state.Level < next.UnlockLevel)
            return Locked($"{producer.Name} upgrade", next.UnlockLevel);
        if (CheckAffordable(state, next.Cost) is { } cantAfford)
            return cantAfford;

        Pay(state, next.Cost);
        placement.Tier++;
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
        state.Time += milliseconds;
        RefillOrders(state, events);
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
