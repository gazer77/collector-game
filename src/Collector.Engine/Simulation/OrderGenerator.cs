using Collector.Engine.Definitions;
using Collector.Engine.Packs;
using Collector.Engine.State;

namespace Collector.Engine.Simulation;

internal static class OrderGenerator
{
    /// <summary>Creates an order from items the player can currently produce, or null if there are none.</summary>
    public static Order? Generate(GameDefinitions definitions, PlayerState state, OrdersDefinition orders)
    {
        var candidates = OrderableItems(definitions, state.Level);
        if (candidates.Count == 0)
            return null;

        var rngState = state.RngState;
        var rng = new DeterministicRandom(ref rngState);

        var count = rng.NextInclusive(Math.Min(orders.MinItems, candidates.Count), Math.Min(orders.MaxItems, candidates.Count));
        var order = new Order { Id = state.NextId++ };
        long value = 0;
        for (var n = 0; n < count; n++)
        {
            var item = PickWeighted(ref rng, candidates);
            candidates.Remove(item);
            var qty = rng.NextInclusive(orders.MinQuantity, orders.MaxQuantity);
            order.Items[item.Id] = qty;
            value += item.SellPrice * qty;
        }

        var reward = Math.Max(1, (long)Math.Ceiling(value * orders.RewardMultiplier));
        order.Rewards[orders.RewardCurrency] = reward;
        order.Xp = (long)Math.Ceiling(reward * orders.XpPerCurrency);
        if (orders.Bonus is { } bonus && rng.NextDouble() < bonus.Chance)
            order.Rewards[bonus.Currency] = order.Rewards.GetValueOrDefault(bonus.Currency) + bonus.Amount;

        state.RngState = rngState;
        return order;
    }

    /// <summary>Items that are unlocked and come out of a recipe the player can run at <paramref name="level"/>.</summary>
    private static List<ItemDefinition> OrderableItems(GameDefinitions definitions, int level)
    {
        var producible = definitions.Recipes.Values
            .Where(r => r.UnlockLevel <= level && definitions.Producers[r.Producer].UnlockLevel <= level)
            .SelectMany(r => r.Outputs.Keys)
            .ToHashSet();
        // Iterate in pack order (not dictionary order) so every host picks identically.
        return definitions.Content.Items
            .Where(i => i.OrderWeight > 0 && i.UnlockLevel <= level && producible.Contains(i.Id))
            .ToList();
    }

    private static ItemDefinition PickWeighted(ref DeterministicRandom rng, List<ItemDefinition> items)
    {
        var total = items.Sum(i => i.OrderWeight);
        var roll = rng.NextInclusive(1, total);
        foreach (var item in items)
        {
            roll -= item.OrderWeight;
            if (roll <= 0)
                return item;
        }
        return items[^1];
    }
}
