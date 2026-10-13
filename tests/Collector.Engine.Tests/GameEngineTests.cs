using System.Text.Json;
using Collector.Engine.Commands;
using Collector.Engine.Definitions;
using Collector.Engine.Packs;
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

        _engine.AdvanceTime(state, 59 * Second);
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
    public void Pace_scales_new_jobs_and_what_is_left_of_jobs_underway()
    {
        var state = NewGame();
        var mill = state.First("feed_mill");
        Ok(state, new StartRecipe(mill.Id, "chicken_feed"));   // 0 → 180
        Ok(state, new StartRecipe(mill.Id, "chicken_feed"));   // 180 → 360
        _engine.AdvanceTime(state, 60 * Second);

        Ok(state, new SetPace(2));

        Assert.Equal(2, state.Pace);
        Assert.Equal(0, mill.Jobs[0].StartsAt);
        Assert.Equal(120 * Second, mill.Jobs[0].EndsAt);       // 120 s left became 60
        Assert.Equal(120 * Second, mill.Jobs[1].StartsAt);     // the queued job moves up...
        Assert.Equal(210 * Second, mill.Jobs[1].EndsAt);       // ...and takes half as long
        Assert.Equal(30 * Second, _engine.DurationOf(state, state.First("field"), _engine.Definitions.Recipes["grow_wheat"]));

        Ok(state, new SetPace(1));
        Assert.Equal(180 * Second, mill.Jobs[0].EndsAt);
        Assert.Equal(360 * Second, mill.Jobs[1].EndsAt);

        Fails(state, new SetPace(0), ErrorCode.InvalidCommand);
        Fails(state, new SetPace(GameEngine.MaxPace * 2), ErrorCode.InvalidCommand);
        Fails(state, new SetPace(double.NaN), ErrorCode.InvalidCommand);
    }

    [Fact]
    public void Saves_from_before_pace_run_at_normal_pace()
    {
        var json = StateHasher.Serialize(NewGame()).Replace("\"pace\":1,", "");
        Assert.DoesNotContain("pace", json);

        var state = StateHasher.Deserialize(json);

        Assert.Equal(1, state.Pace);
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
    public void Field_allowance_grows_with_level_and_owned_land()
    {
        var state = NewGame();
        var field = TestPacks.Farm.Producers["field"];
        Assert.Equal(8 + 8, GameDefinitions.LimitFor(field, state)); // level 1 step, plus home

        state.Level = 3;
        state.Wallet["coins"] = 1000;
        Ok(state, new BuyParcel("east"));
        Assert.Equal(12 + 2 * 8, GameDefinitions.LimitFor(field, state));

        // Producers without a per-parcel bonus are unaffected.
        Assert.Equal(1, GameDefinitions.LimitFor(TestPacks.Farm.Producers["feed_mill"], state));
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
    public void Slots_and_lanes_upgrade_separately_up_to_their_max()
    {
        var state = NewGame();
        var mill = state.First("feed_mill");
        Fails(state, new UpgradeProducer(mill.Id, UpgradeTrack.Slots), ErrorCode.Locked); // needs level 2
        Fails(state, new UpgradeProducer(mill.Id, UpgradeTrack.Lanes), ErrorCode.Locked); // needs level 3

        state.Level = 6;
        state.Wallet["coins"] = 5000;
        Ok(state, new UpgradeProducer(mill.Id, UpgradeTrack.Slots));
        Assert.Equal((5, 1), (_engine.SlotsOf(mill), _engine.LanesOf(mill)));
        Ok(state, new UpgradeProducer(mill.Id, UpgradeTrack.Lanes));
        Assert.Equal((5, 2), (_engine.SlotsOf(mill), _engine.LanesOf(mill)));
        Ok(state, new UpgradeProducer(mill.Id, UpgradeTrack.Slots));
        Ok(state, new UpgradeProducer(mill.Id, UpgradeTrack.Lanes));
        Assert.Equal((7, 3), (_engine.SlotsOf(mill), _engine.LanesOf(mill)));
        Fails(state, new UpgradeProducer(mill.Id, UpgradeTrack.Slots), ErrorCode.MaxTier);
        Fails(state, new UpgradeProducer(mill.Id, UpgradeTrack.Lanes), ErrorCode.MaxTier);
        Assert.Equal(5000 - 250 - 400 - 700 - 1200, state.Balance("coins"));
    }

    [Fact]
    public void Saves_from_before_lanes_were_separate_still_load()
    {
        var state = NewGame();
        state.Placements[0].SlotTier = 1;
        var json = StateHasher.Serialize(state);
        Assert.Contains("\"tier\":1", json); // the old field name is kept
        var old = json.Replace("\"laneTier\":0,", "");
        Assert.DoesNotContain("laneTier", old);

        var loaded = StateHasher.Deserialize(old);

        Assert.Equal(1, loaded.Placements[0].SlotTier);
        Assert.Equal(0, loaded.Placements[0].LaneTier);
    }

    [Fact]
    public void Every_farm_producer_can_upgrade_both_slots_and_lanes()
    {
        Assert.All(TestPacks.Farm.Producers.Values, p =>
        {
            Assert.True(p.Slots.Count > 1, $"{p.Id} has no slot upgrade");
            Assert.True(p.Lanes.Count > 1, $"{p.Id} has no lane upgrade");
        });
    }

    [Fact]
    public void Lanes_never_exceed_slots()
    {
        var state = NewGame();
        state.Level = 4;
        state.Wallet["coins"] = 1000;
        var field = state.First("field");
        Ok(state, new UpgradeProducer(field.Id, UpgradeTrack.Lanes)); // 2 lanes, but only 1 slot
        Assert.Equal(1, _engine.LanesOf(field));
        Ok(state, new UpgradeProducer(field.Id, UpgradeTrack.Slots));
        Assert.Equal(2, _engine.LanesOf(field));

        // Two crops now grow side by side on one field.
        Ok(state, new StartRecipe(field.Id, "grow_wheat"));
        Ok(state, new StartRecipe(field.Id, "grow_wheat"));
        Assert.All(field.Jobs, j => Assert.Equal(0, j.StartsAt));
    }

    [Fact]
    public void Upgrading_to_more_lanes_runs_queued_jobs_side_by_side()
    {
        var state = NewGame();
        var mill = state.First("feed_mill");
        for (var i = 0; i < 3; i++)
            Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        Assert.Equal([0, 180 * Second, 360 * Second], mill.Jobs.Select(j => j.StartsAt));

        // A second lane: the second job starts at once and the third moves up behind the first.
        state.Level = 3;
        state.Wallet["coins"] = 400;
        Ok(state, new UpgradeProducer(mill.Id, UpgradeTrack.Lanes));
        Assert.Equal(2, _engine.LanesOf(mill));
        Assert.Equal([0, 0, 180 * Second], mill.Jobs.Select(j => j.StartsAt));
        Assert.Equal([180 * Second, 180 * Second, 360 * Second], mill.Jobs.Select(j => j.EndsAt));

        // With room for more, a new job takes whichever lane frees up first.
        state.Wallet["coins"] = 250;
        Ok(state, new UpgradeProducer(mill.Id, UpgradeTrack.Slots));
        state.Inventory["wheat"] = 10;
        Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        Assert.Equal(180 * Second, mill.Jobs[^1].StartsAt);

        // Animals start with as many lanes as slots, so everything runs at once.
        state.Wallet["coins"] = 200;
        Ok(state, new PlaceProducer("chicken_coop", 6, 4));
        Assert.Equal(3, _engine.LanesOf(state.First("chicken_coop")));
    }

    [Fact]
    public void Cancelling_a_queued_job_refunds_it_and_moves_the_queue_up()
    {
        var state = NewGame();
        var mill = state.First("feed_mill");
        for (var i = 0; i < 3; i++)
            Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        Assert.Equal(0, state.Count("wheat"));

        Fails(state, new CancelJob(mill.Id, 0), ErrorCode.InvalidCommand); // already running
        Fails(state, new CancelJob(mill.Id, 3), ErrorCode.NotFound);

        Ok(state, new CancelJob(mill.Id, 1));
        Assert.Equal(2, state.Count("wheat"));
        Assert.Equal(2, mill.Jobs.Count);
        Assert.Equal(180 * Second, mill.Jobs[1].StartsAt); // the third job took the second's place
        Assert.Equal(360 * Second, mill.Jobs[1].EndsAt);

        state.Inventory["wheat"] = 50; // silo full
        Fails(state, new CancelJob(mill.Id, 1), ErrorCode.StorageFull);
    }

    [Theory]
    [InlineData(typeof(GameCommand))]
    [InlineData(typeof(GameEvent))]
    public void Every_command_and_event_is_registered_for_json(Type baseType)
    {
        // An unregistered subtype can't be sent between client and server; the server would fail mid-response.
        var registered = baseType.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute), false)
            .Cast<System.Text.Json.Serialization.JsonDerivedTypeAttribute>()
            .Select(a => a.DerivedType)
            .ToHashSet();
        var subtypes = baseType.Assembly.GetTypes().Where(t => t.IsSubclassOf(baseType) && !t.IsAbstract);
        Assert.All(subtypes, t => Assert.Contains(t, registered));
    }

    [Fact]
    public void Upgrades_check_level_prerequisites_and_cost()
    {
        var state = NewGame();
        Fails(state, new BuyUpgrade("watering_can"), ErrorCode.Locked);   // level 2
        Fails(state, new BuyUpgrade("hoverboard"), ErrorCode.UnknownDefinition);

        state.Level = 5;
        state.Wallet["coins"] = 2000;
        Fails(state, new BuyUpgrade("sprinklers"), ErrorCode.Locked);     // needs the watering can
        Ok(state, new BuyUpgrade("watering_can"));
        Fails(state, new BuyUpgrade("watering_can"), ErrorCode.InvalidCommand);
        Ok(state, new BuyUpgrade("sprinklers"));
        Assert.Equal(2000 - 250 - 1500, state.Balance("coins"));
        Fails(state, new BuyUpgrade("plow"), ErrorCode.CannotAfford);
    }

    [Fact]
    public void Speed_upgrades_stack_on_new_jobs_for_the_producers_they_name()
    {
        var state = NewGame();
        state.Level = 5;
        state.Wallet["coins"] = 5000;
        var field = state.First("field");
        var mill = state.First("feed_mill");
        Ok(state, new StartRecipe(field.Id, "grow_wheat"));
        Assert.Equal(60 * Second, field.Jobs[0].EndsAt);

        Ok(state, new BuyUpgrade("watering_can"));
        Ok(state, new BuyUpgrade("sprinklers"));
        Assert.Equal(60 * Second, field.Jobs[0].EndsAt); // already planted: unchanged

        var second = state.Placements.Where(p => p.Producer == "field").ElementAt(1);
        Ok(state, new StartRecipe(second.Id, "grow_wheat"));
        Assert.Equal((long)Math.Round(60 * Second * 0.85 * 0.8), second.Jobs[0].EndsAt);

        // Field upgrades don't touch the mill.
        Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        Assert.Equal(180 * Second, mill.Jobs[0].EndsAt);
    }

    [Fact]
    public void Gather_all_collects_every_covered_producer_from_any_one_of_them()
    {
        var state = NewGame();
        state.Level = 2;
        var fields = state.Placements.Where(p => p.Producer == "field").ToList();
        var mill = state.First("feed_mill");
        Ok(state, new StartRecipe(fields[0].Id, "grow_wheat"));
        Ok(state, new StartRecipe(fields[1].Id, "grow_wheat"));
        Ok(state, new StartRecipe(fields[2].Id, "grow_wheat"));
        Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        _engine.AdvanceTime(state, 180 * Second);

        Ok(state, new Collect(fields[0].Id));
        Assert.Empty(fields[0].Jobs);
        Assert.Single(fields[1].Jobs); // no cart yet: one field at a time

        Ok(state, new BuyUpgrade("harvest_cart"));
        var wheat = state.Count("wheat");
        Ok(state, new Collect(fields[0].Id)); // an empty field still gathers the rest
        Assert.All(fields, f => Assert.Empty(f.Jobs));
        Assert.Equal(wheat + 4, state.Count("wheat"));
        Assert.Single(mill.Jobs); // the cart is for fields only

        Fails(state, new Collect(fields[0].Id), ErrorCode.NotReady);
    }

    [Fact]
    public void Self_running_producers_collect_and_restart_as_each_batch_finishes()
    {
        var state = NewGame();
        state.Level = 6;
        state.Wallet["coins"] = 5000;
        Ok(state, new BuyUpgrade("harvest_cart"));
        Ok(state, new BuyUpgrade("seed_drill"));
        var field = state.First("field");
        Ok(state, new StartRecipe(field.Id, "grow_wheat"));   // 6 wheat → 5
        var xp = state.Xp;

        _engine.AdvanceTime(state, 150 * Second);

        // Harvested at 60 s and 120 s (+2 each, replanting -1 each), now growing again from 120 s.
        Assert.Equal(7, state.Count("wheat"));
        Assert.Equal(xp + 2, state.Xp);
        var job = Assert.Single(field.Jobs);
        Assert.Equal((120 * Second, 180 * Second), (job.StartsAt, job.EndsAt));
    }

    [Fact]
    public void Self_running_producers_play_out_the_same_however_time_is_chopped_up()
    {
        PlayerState Setup()
        {
            var state = NewGame();
            state.Level = 8;
            state.Wallet["coins"] = 20000;
            foreach (var upgrade in new[] { "harvest_cart", "seed_drill", "farm_truck", "conveyor_belts", "nesting_boxes", "plow", "farm_manager", "order_assistant" })
                Ok(state, new BuyUpgrade(upgrade));
            state.Inventory["wheat"] = 30;
            Ok(state, new SetKeep("wheat", 20));
            Ok(state, new SetKeep("chicken_feed", 6));
            foreach (var field in state.Placements.Where(p => p.Producer == "field"))
                Ok(state, new StartRecipe(field.Id, "grow_wheat"));
            Ok(state, new StartRecipe(state.First("feed_mill").Id, "chicken_feed"));
            return state;
        }
        var once = Setup();
        _engine.AdvanceTime(once, 3600 * Second);
        var chopped = Setup();
        for (var t = 0; t < 3600; t += 7)
            _engine.AdvanceTime(chopped, Math.Min(7, 3600 - t) * Second);

        once.Version = chopped.Version = 0;
        Assert.Equal(StateHasher.Serialize(once), StateHasher.Serialize(chopped));
        Assert.True(once.Count("chicken_feed") > 3);
    }

    [Fact]
    public void A_full_silo_pauses_self_running_producers_until_there_is_room()
    {
        var state = NewGame();
        state.Level = 6;
        state.Wallet["coins"] = 5000;
        Ok(state, new BuyUpgrade("harvest_cart"));
        Ok(state, new BuyUpgrade("seed_drill"));
        var field = state.First("field");
        Ok(state, new StartRecipe(field.Id, "grow_wheat"));
        state.Inventory["wheat"] += _engine.StorageCapacity(state, "silo") - _engine.StorageUsed(state, "silo") - 1;

        _engine.AdvanceTime(state, 300 * Second);
        Assert.True(Assert.Single(field.Jobs).IsReady(state.Time)); // no room for 2 wheat: it waits

        Ok(state, new SellItem("wheat", 5));
        _engine.AdvanceTime(state, 1 * Second);
        Assert.Equal(300 * Second, Assert.Single(field.Jobs).StartsAt); // collected and replanted as soon as there was room
    }

    [Fact]
    public void The_farm_manager_sells_only_what_is_above_the_amounts_kept()
    {
        var state = NewGame();
        state.Level = 4;
        state.Wallet["coins"] = 1000;
        state.Inventory["wheat"] = 20;
        state.Inventory["corn"] = 9;

        Ok(state, new SetKeep("wheat", 10)); // nobody to sell it yet
        Assert.Equal(20, state.Count("wheat"));

        var result = _engine.Apply(state, new BuyUpgrade("farm_manager"));
        Assert.True(result.Ok);
        Assert.Equal(10, state.Count("wheat"));
        Assert.Equal(9, state.Count("corn")); // no amount set: never sold
        Assert.Equal(10 * _engine.Definitions.Items["wheat"].SellPrice, state.Balance("coins"));
        var sold = Assert.Single(result.Events.OfType<ItemsSold>());
        Assert.Equal(10, sold.Items["wheat"]);

        // Fresh harvests are sold too, down to the amount kept.
        var field = state.First("field");
        Ok(state, new StartRecipe(field.Id, "grow_wheat"));
        Assert.Equal(9, state.Count("wheat")); // below the amount kept: nothing to sell
        _engine.AdvanceTime(state, 60 * Second);
        Ok(state, new Collect(field.Id));
        Assert.Equal(10, state.Count("wheat"));

        Ok(state, new SetKeep("wheat", null));
        state.Inventory["wheat"] = 30;
        Ok(state, new SetKeep("corn", 9));
        Assert.Equal(30, state.Count("wheat"));
        Fails(state, new SetKeep("wheat", -1), ErrorCode.InvalidCommand);
    }

    [Fact]
    public void The_order_assistant_delivers_what_it_can_without_dipping_below_amounts_kept()
    {
        var state = NewGame();
        state.Level = 5;
        state.Wallet["coins"] = 1500;
        state.Inventory.Clear();
        state.Orders[0].Order = new Order { Id = 900, Items = { ["wheat"] = 3 }, Rewards = { ["coins"] = 40 } };
        state.Orders[1].Order = new Order { Id = 901, Items = { ["corn"] = 2 }, Rewards = { ["coins"] = 30 } };
        state.Orders[2] = new OrderSlot { AvailableAt = long.MaxValue };
        state.Inventory["wheat"] = 5;
        state.Inventory["corn"] = 3;
        Ok(state, new SetKeep("corn", 2));

        var result = _engine.Apply(state, new BuyUpgrade("order_assistant"));

        Assert.True(result.Ok);
        Assert.Null(state.Orders[0].Order);                 // delivered
        Assert.Equal(901, state.Orders[1].Order!.Id);       // would leave 1 corn, below the 2 kept
        Assert.Equal(2, state.Count("wheat"));
        Assert.Equal(40, state.Balance("coins"));
        Assert.Contains(result.Events, e => e is OrderCompleted { OrderId: 900, ByHelper: true });
    }

    [Fact]
    public void The_order_assistant_delivers_new_orders_the_moment_they_arrive()
    {
        var state = NewGame();
        state.Level = 5;
        state.Wallet["coins"] = 1500;
        foreach (var item in _engine.Definitions.Items.Keys)
            state.Inventory[item] = 500;
        Ok(state, new BuyUpgrade("order_assistant"));       // fills all three straight away
        Assert.All(state.Orders, slot => Assert.Null(slot.Order));
        var delay = _engine.Definitions.Orders!.RefillDelaySeconds * Second;
        var arrives = state.Orders[0].AvailableAt;
        Assert.Equal(state.Time + delay, arrives);

        _engine.AdvanceTime(state, delay * 5 / 2);

        // The new order came at `arrives`, was delivered then, and the slot's next order is due a delay later.
        Assert.Equal(arrives + 2 * delay, state.Orders[0].AvailableAt);
    }

    [Fact]
    public async Task Farm_wide_helpers_cannot_target_producers()
    {
        var files = TestPacks.FarmFiles();
        files["upgrades.json"] = files["upgrades.json"].Replace("\"sellsExcess\": true", "\"sellsExcess\": true, \"categories\": [\"field\"]");

        var ex = await Assert.ThrowsAsync<PackValidationException>(() => TestPacks.LoadAsync(files));

        Assert.Contains(ex.Errors, e => e.Contains("farm-wide helper"));
    }

    [Fact]
    public void Bonus_chance_sometimes_adds_one_of_each_output()
    {
        var state = NewGame();
        var field = state.First("field");
        var rngBefore = state.RngState;
        Ok(state, new StartRecipe(field.Id, "grow_wheat"));
        _engine.AdvanceTime(state, 60 * Second);
        Ok(state, new Collect(field.Id));
        Assert.Equal(rngBefore, state.RngState); // no bonus upgrades, no rolls

        state.Level = 3;
        state.Wallet["coins"] = 500;
        Ok(state, new BuyUpgrade("plow"));
        Assert.Equal(0.2, _engine.BonusChance(state, field));

        int bonuses = 0, harvests = 200;
        for (var i = 0; i < harvests; i++)
        {
            var before = state.Count("wheat");
            Ok(state, new StartRecipe(field.Id, "grow_wheat"));
            _engine.AdvanceTime(state, 60 * Second);
            var result = _engine.Apply(state, new Collect(field.Id));
            var gained = state.Count("wheat") - before;
            var bonus = result.Events.OfType<BonusYield>().SingleOrDefault();
            Assert.Equal(bonus is null ? 1 : 2, gained); // plant 1, harvest 2 (+1 on a bonus)
            if (bonus is not null)
                bonuses++;
            state.Inventory["wheat"] = 10; // keep the silo from filling
        }
        Assert.InRange(bonuses, 20, 60); // ~20% of 200
    }

    [Fact]
    public void Idle_slots_count_only_what_could_start_now()
    {
        var state = NewGame();
        var mill = state.First("feed_mill");
        Assert.Equal(1, _engine.IdleSlots(state, mill)); // one lane, three queue slots

        Ok(state, new StartRecipe(mill.Id, "chicken_feed"));
        Assert.Equal(0, _engine.IdleSlots(state, mill)); // queue has room, but the lane is busy

        _engine.AdvanceTime(state, 180 * Second);
        Assert.Equal(1, _engine.IdleSlots(state, mill)); // finished, uncollected goods don't hold the lane

        // Parallel producers: every empty slot can start, and uncollected goods still fill theirs.
        Ok(state, new PlaceProducer("chicken_coop", 6, 4));
        var coop = state.First("chicken_coop");
        Ok(state, new Collect(mill.Id));
        Ok(state, new StartRecipe(coop.Id, "lay_egg"));
        Assert.Equal(2, _engine.IdleSlots(state, coop));
        _engine.AdvanceTime(state, 300 * Second);
        Assert.Equal(2, _engine.IdleSlots(state, coop));
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
    public void Buying_from_the_market_checks_level_wallet_and_storage()
    {
        var state = NewGame();
        var coins = state.Balance("coins");
        var wheat = state.Count("wheat");
        Assert.Equal(8, _engine.BuyPrice(TestPacks.Farm.Items["wheat"])); // sells for 3, x2.5 rounded up

        Ok(state, new BuyItem("wheat", 2));
        Assert.Equal(coins - 16, state.Balance("coins"));
        Assert.Equal(wheat + 2, state.Count("wheat"));

        Fails(state, new BuyItem("carrot", 1), ErrorCode.Locked);        // carrots unlock at level 3
        Fails(state, new BuyItem("wheat", 0), ErrorCode.InvalidCommand);
        Fails(state, new BuyItem("wheat", 30), ErrorCode.CannotAfford);
        state.Wallet["coins"] = 10_000;
        Fails(state, new BuyItem("wheat", 50), ErrorCode.StorageFull);   // silo holds 50
        Assert.Equal(10_000, state.Balance("coins"));
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
