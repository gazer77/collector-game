using System.Text.Json;
using Collector.Engine.Commands;
using Collector.Engine.Serialization;
using Collector.Engine.Simulation;
using Collector.Engine.State;

namespace Collector.Engine.Tests;

public class GameEngineTests
{
    private const long Second = 1000;
    private readonly GameEngine _engine = TestPacks.FarmEngine();

    private PlayerState NewGame(ulong seed = 42) => _engine.NewGame(seed);

    private void Ok(PlayerState state, GameCommand command)
    {
        var result = _engine.Apply(state, command);
        Assert.True(result.Ok, $"{command} failed: {result.Error} {result.Message}");
    }

    private void Fails(PlayerState state, GameCommand command, ErrorCode expected)
    {
        var before = StateHasher.Serialize(state);
        var result = _engine.Apply(state, command);
        Assert.Equal(expected, result.Error);
        Assert.Equal(before, StateHasher.Serialize(state)); // failed commands never change state
    }

    [Fact]
    public void New_game_uses_the_pack_starting_state()
    {
        var state = NewGame();

        Assert.Equal(250, state.Balance("coins"));
        Assert.Equal(5, state.Balance("gems"));
        Assert.Equal(6, state.Count("wheat"));
        Assert.Equal(7, state.Placements.Count);
        Assert.Equal(["home"], state.OwnedParcels);
        Assert.Equal(3, state.Orders.Count);
        // At level 1 only wheat and eggs are orderable (feed has orderWeight 0, corn unlocks at 2).
        Assert.All(state.Orders, slot => Assert.All(slot.Order!.Items.Keys, item => Assert.Contains(item, new[] { "wheat", "egg" })));
    }

    [Fact]
    public void Each_placement_remembers_its_last_recipe_after_collection()
    {
        var state = NewGame();
        var field = state.First("field");
        Assert.Null(field.LastRecipe);

        Ok(state, new StartRecipe(field.Id, "grow_wheat"));
        _engine.AdvanceTime(state, 120 * Second);
        Ok(state, new Collect(field.Id));

        Assert.Empty(field.Jobs);
        Assert.Equal("grow_wheat", field.LastRecipe);
        Assert.All(state.Placements.Where(p => p.Id != field.Id), p => Assert.Null(p.LastRecipe));
        Assert.Equal("grow_wheat", StateHasher.Clone(state).FindPlacement(field.Id)!.LastRecipe);
    }

    [Fact]
    public void Planting_consumes_seed_and_harvest_waits_for_the_timer()
    {
        var state = NewGame();
        var field = state.First("field");

        Ok(state, new StartRecipe(field.Id, "grow_wheat"));
        Assert.Equal(5, state.Count("wheat"));
        Fails(state, new StartRecipe(field.Id, "grow_wheat"), ErrorCode.NoFreeSlot);
        Fails(state, new Collect(field.Id), ErrorCode.NotReady);

        _engine.AdvanceTime(state, 119 * Second);
        Fails(state, new Collect(field.Id), ErrorCode.NotReady);
        _engine.AdvanceTime(state, 1 * Second);

        var result = _engine.Apply(state, new Collect(field.Id));
        Assert.True(result.Ok);
        Assert.Equal(7, state.Count("wheat"));
        Assert.Equal(1, state.Xp);
        Assert.Empty(field.Jobs);
        Assert.Contains(result.Events, e => e is ItemsCollected { Items: var items } && items["wheat"] == 2);
    }

    [Fact]
    public void Recipes_need_inputs_the_right_producer_and_the_right_level()
    {
        var state = NewGame();
        var field = state.First("field");
        var mill = state.First("feed_mill");

        Fails(state, new StartRecipe(field.Id, "chicken_feed"), ErrorCode.InvalidCommand);
        Fails(state, new StartRecipe(field.Id, "grow_corn"), ErrorCode.Locked);
        Fails(state, new StartRecipe(mill.Id, "no_such_recipe"), ErrorCode.UnknownDefinition);

        state.Inventory.Remove("wheat");
        Fails(state, new StartRecipe(field.Id, "grow_wheat"), ErrorCode.MissingItems);
    }

    [Fact]
    public void Sequential_producers_queue_jobs_back_to_back()
    {
        var state = NewGame();
        var mill = state.First("feed_mill");

        Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        Assert.Equal(0, mill.Jobs[0].StartsAt);
        Assert.Equal(180 * Second, mill.Jobs[1].StartsAt);
        Assert.Equal(360 * Second, mill.Jobs[1].EndsAt);

        _engine.AdvanceTime(state, 200 * Second);
        Ok(state, new Collect(mill.Id));
        Assert.Equal(3, state.Count("chicken_feed"));
        Assert.Single(mill.Jobs);

        // A job queued after the queue drained starts now, not at the old end time.
        _engine.AdvanceTime(state, 400 * Second);
        Ok(state, new Collect(mill.Id));
        Ok(state, new StartRecipe(state.First("field").Id, "grow_wheat"));
        state.Inventory["wheat"] = 10;
        Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        Assert.Equal(600 * Second, mill.Jobs[0].StartsAt);
    }

    [Fact]
    public void Parallel_producers_run_all_slots_at_once()
    {
        var state = NewGame();
        Ok(state, new PlaceProducer("chicken_coop", 6, 4));
        var coop = state.First("chicken_coop");
        state.Inventory["chicken_feed"] = 5;

        Ok(state, new StartRecipe(coop.Id, "lay_egg"));
        Ok(state, new StartRecipe(coop.Id, "lay_egg"));
        Ok(state, new StartRecipe(coop.Id, "lay_egg"));
        Fails(state, new StartRecipe(coop.Id, "lay_egg"), ErrorCode.NoFreeSlot);
        Assert.All(coop.Jobs, j => Assert.Equal(300 * Second, j.EndsAt));

        _engine.AdvanceTime(state, 300 * Second);
        Ok(state, new Collect(coop.Id));
        Assert.Equal(3, state.Count("egg"));
    }

    [Fact]
    public void Collecting_stops_when_storage_is_full()
    {
        var state = NewGame();
        var field = state.First("field");
        Ok(state, new StartRecipe(field.Id, "grow_wheat"));
        _engine.AdvanceTime(state, 120 * Second);

        state.Inventory["wheat"] = 49; // silo capacity 50; the harvest yields 2
        state.Level = 2;
        state.Wallet["coins"] = 300;
        Fails(state, new Collect(field.Id), ErrorCode.StorageFull);

        Ok(state, new UpgradeStorage("silo"));
        Assert.Equal(75, _engine.StorageCapacity(state, "silo"));
        Ok(state, new Collect(field.Id));
        Assert.Equal(51, state.Count("wheat"));
    }

    [Fact]
    public void Storage_capacity_is_shared_by_every_item_in_it()
    {
        var state = NewGame();
        state.Inventory["chicken_feed"] = 30;
        state.Inventory["egg"] = 18;

        Assert.Equal(48, _engine.StorageUsed(state, "barn"));
        Assert.Equal(6, _engine.StorageUsed(state, "silo"));
    }

    [Fact]
    public void Leveling_up_grants_rewards_and_unlocks_content()
    {
        var state = NewGame();
        state.Xp = 19;
        var field = state.First("field");
        Ok(state, new StartRecipe(field.Id, "grow_wheat"));
        _engine.AdvanceTime(state, 120 * Second);

        var result = _engine.Apply(state, new Collect(field.Id));

        Assert.Equal(2, state.Level);
        Assert.Equal(300, state.Balance("coins"));
        Assert.Equal(6, state.Balance("gems"));
        Assert.Equal(3, state.Count("corn"));
        Assert.Contains(result.Events, e => e is LevelReached { Level: 2 });
        Ok(state, new StartRecipe(field.Id, "grow_corn"));
    }

    [Fact]
    public void Large_xp_gains_can_skip_several_levels()
    {
        var state = NewGame();
        state.Inventory["egg"] = 4;
        state.Orders[0].Order = new Order { Id = 999, Items = { ["egg"] = 4 }, Rewards = { ["coins"] = 10 }, Xp = 70 };

        Ok(state, new FillOrder(0));

        Assert.Equal(3, state.Level);
        Assert.Equal(3, state.Count("carrot"));
    }

    [Fact]
    public void Placing_checks_land_overlap_cost_and_limits()
    {
        var state = NewGame();

        Fails(state, new PlaceProducer("chicken_coop", 9, 0), ErrorCode.InvalidPosition);  // spills onto unowned land
        Fails(state, new PlaceProducer("chicken_coop", 3, 2), ErrorCode.InvalidPosition);  // overlaps a field
        Fails(state, new PlaceProducer("chicken_coop", -1, 0), ErrorCode.InvalidPosition);
        Fails(state, new PlaceProducer("dairy", 6, 4), ErrorCode.Locked);
        Fails(state, new PlaceProducer("feed_mill", 6, 4), ErrorCode.LimitReached);

        Ok(state, new PlaceProducer("chicken_coop", 6, 4));
        Assert.Equal(130, state.Balance("coins"));
        Fails(state, new PlaceProducer("chicken_coop", 0, 5), ErrorCode.LimitReached);

        state.Wallet["coins"] = 5;
        Fails(state, new PlaceProducer("field", 0, 7), ErrorCode.CannotAfford);
    }

    [Fact]
    public void Buying_land_opens_it_for_building()
    {
        var state = NewGame();
        Fails(state, new BuyParcel("east"), ErrorCode.Locked);

        state.Level = 2;
        state.Wallet["coins"] = 300;
        state.Wallet["coins"] = 650;
        Ok(state, new BuyParcel("east"));
        Fails(state, new BuyParcel("east"), ErrorCode.InvalidCommand);

        Assert.Equal(150, state.Balance("coins"));
        Ok(state, new PlaceProducer("chicken_coop", 9, 0)); // now straddles home and east
    }

    [Fact]
    public void Moving_ignores_the_producer_own_footprint()
    {
        var state = NewGame();
        var mill = state.First("feed_mill");

        Ok(state, new MoveProducer(mill.Id, 7, 1)); // overlaps where it was
        Assert.Equal((7, 1), (mill.X, mill.Y));
        Fails(state, new MoveProducer(mill.Id, 2, 1), ErrorCode.InvalidPosition);
    }

    [Fact]
    public void Upgrading_a_producer_adds_slots_up_to_max_tier()
    {
        var state = NewGame();
        var mill = state.First("feed_mill");
        Fails(state, new UpgradeProducer(mill.Id), ErrorCode.Locked); // tier 1 needs level 2

        state.Level = 5;
        state.Wallet["coins"] = 2000;
        Ok(state, new UpgradeProducer(mill.Id));
        Assert.Equal(5, _engine.TierOf(mill).Slots);
        Ok(state, new UpgradeProducer(mill.Id));
        Fails(state, new UpgradeProducer(mill.Id), ErrorCode.MaxTier);

        // Tier 2 makes jobs 20% faster.
        Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        Assert.Equal(144 * Second, mill.Jobs[0].EndsAt);
    }

    [Fact]
    public void Upgrading_to_more_lanes_runs_queued_jobs_side_by_side()
    {
        var state = NewGame();
        var mill = state.First("feed_mill");
        for (var i = 0; i < 3; i++)
            Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        Assert.Equal([0, 180 * Second, 360 * Second], mill.Jobs.Select(j => j.StartsAt));

        // Tier 1 has two lanes: the second job starts at once and the third moves up behind the first.
        state.Level = 2;
        state.Wallet["coins"] = 300;
        Ok(state, new UpgradeProducer(mill.Id));
        Assert.Equal(2, _engine.LanesOf(mill));
        Assert.Equal([0, 0, 180 * Second], mill.Jobs.Select(j => j.StartsAt));
        Assert.Equal([180 * Second, 180 * Second, 360 * Second], mill.Jobs.Select(j => j.EndsAt));

        // A new job takes whichever lane frees up first.
        state.Inventory["wheat"] = 10;
        Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        Assert.Equal(180 * Second, mill.Jobs[^1].StartsAt);

        // Parallel producers ignore lanes and run every slot.
        state.Wallet["coins"] = 200;
        Ok(state, new PlaceProducer("chicken_coop", 6, 4));
        Assert.Equal(3, _engine.LanesOf(state.First("chicken_coop")));
    }

    [Fact]
    public void A_second_feed_mill_unlocks_at_level_four()
    {
        var state = NewGame();
        state.Wallet["coins"] = 1000;
        state.Level = 3;
        Fails(state, new PlaceProducer("feed_mill", 6, 4), ErrorCode.LimitReached);
        state.Level = 4;
        Ok(state, new PlaceProducer("feed_mill", 6, 4));
        Fails(state, new PlaceProducer("feed_mill", 6, 7), ErrorCode.LimitReached);
    }

    [Fact]
    public void Speed_up_charges_by_time_remaining_and_shifts_the_queue()
    {
        var state = NewGame();
        var mill = state.First("feed_mill");
        Fails(state, new SpeedUp(mill.Id), ErrorCode.NotReady);

        Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        Assert.Equal(1, _engine.SpeedUpCost(state, mill)); // 180 s remaining, 600 s per gem

        Ok(state, new SpeedUp(mill.Id));
        Assert.Equal(4, state.Balance("gems"));
        Assert.Equal(0, mill.Jobs[0].EndsAt);
        Assert.Equal(180 * Second, mill.Jobs[1].EndsAt);

        state.Wallet["gems"] = 0;
        Fails(state, new SpeedUp(mill.Id), ErrorCode.CannotAfford);
    }

    [Fact]
    public void Selling_pays_the_market_price()
    {
        var state = NewGame();

        Ok(state, new SellItem("wheat", 4));
        Assert.Equal(262, state.Balance("coins"));
        Assert.Equal(2, state.Count("wheat"));

        Fails(state, new SellItem("wheat", 3), ErrorCode.MissingItems);
        Fails(state, new SellItem("wheat", 0), ErrorCode.InvalidCommand);
        Fails(state, new SellItem("truffle", 1), ErrorCode.UnknownDefinition);
    }

    [Fact]
    public void Filling_an_order_pays_out_and_a_new_order_arrives_after_the_delay()
    {
        var state = NewGame();
        state.Level = 10; // max level, so no level-up rewards muddy the payout
        state.Xp = 1700;
        var order = state.Orders[0].Order!;
        foreach (var (item, qty) in order.Items)
            state.Inventory[item] = qty;
        var coins = state.Balance("coins");

        Ok(state, new FillOrder(0));

        Assert.Equal(coins + order.Rewards["coins"], state.Balance("coins"));
        Assert.Equal(1700 + order.Xp, state.Xp);
        Assert.Null(state.Orders[0].Order);
        Fails(state, new FillOrder(0), ErrorCode.NotFound);

        var events = _engine.AdvanceTime(state, 30 * Second);
        Assert.NotNull(state.Orders[0].Order);
        Assert.Contains(events, e => e is OrderArrived { Slot: 0 });
    }

    [Fact]
    public void Order_rewards_beat_the_market_price()
    {
        var state = NewGame();
        foreach (var slot in state.Orders)
        {
            var marketValue = slot.Order!.Items.Sum(kv => _engine.Definitions.Items[kv.Key].SellPrice * kv.Value);
            Assert.True(slot.Order.Rewards["coins"] > marketValue);
        }
    }

    [Fact]
    public void Discarded_orders_come_back_after_the_cooldown()
    {
        var state = NewGame();
        Ok(state, new DiscardOrder(1));
        Assert.Null(state.Orders[1].Order);

        _engine.AdvanceTime(state, 299 * Second);
        Assert.Null(state.Orders[1].Order);
        _engine.AdvanceTime(state, 1 * Second);
        Assert.NotNull(state.Orders[1].Order);
    }

    [Fact]
    public void End_turn_is_rejected_in_a_real_time_pack()
    {
        Fails(NewGame(), new EndTurn(), ErrorCode.InvalidCommand);
    }

    [Fact]
    public void Same_seed_and_commands_give_identical_state()
    {
        static string Play(GameEngine engine, ulong seed)
        {
            var state = engine.NewGame(seed);
            engine.Apply(state, new StartRecipe(state.First("field").Id, "grow_wheat"));
            engine.Apply(state, new DiscardOrder(0));
            engine.AdvanceTime(state, 400 * Second);
            engine.Apply(state, new Collect(state.First("field").Id));
            return StateHasher.Hash(state);
        }

        Assert.Equal(Play(_engine, 7), Play(TestPacks.FarmEngine(), 7));
        Assert.NotEqual(Play(_engine, 7), Play(_engine, 8));
    }

    [Fact]
    public void State_survives_a_json_round_trip_with_the_same_hash()
    {
        var state = NewGame();
        Ok(state, new StartRecipe(state.First("feed_mill").Id, "chicken_feed"));
        Ok(state, new PlaceProducer("chicken_coop", 6, 4));
        state.Inventory["zzz_last"] = 0;
        state.Inventory.Remove("zzz_last");

        var copy = StateHasher.Clone(state);

        Assert.Equal(StateHasher.Hash(state), StateHasher.Hash(copy));
        Assert.Equal(StateHasher.Serialize(state), StateHasher.Serialize(copy));
    }

    [Fact]
    public void Commands_serialize_with_a_type_discriminator()
    {
        GameCommand command = new StartRecipe(3, "grow_wheat");

        var json = JsonSerializer.Serialize(command, EngineJson.Options);
        var back = JsonSerializer.Deserialize<GameCommand>(json, EngineJson.Options);

        Assert.Equal("""{"type":"startRecipe","placementId":3,"recipe":"grow_wheat"}""", json);
        Assert.Equal(command, back);
    }

    [Fact]
    public void Version_counts_successful_changes_only()
    {
        var state = NewGame();
        _engine.Apply(state, new Collect(state.First("field").Id)); // fails
        _engine.Apply(state, new StartRecipe(state.First("field").Id, "grow_wheat"));
        _engine.AdvanceTime(state, Second);

        Assert.Equal(2, state.Version);
    }
}
