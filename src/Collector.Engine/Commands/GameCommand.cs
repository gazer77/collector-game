using System.Text.Json.Serialization;
using Collector.Engine.Definitions;

namespace Collector.Engine.Commands;

/// <summary>A player action. Commands never carry a timestamp: the engine uses the state's own clock.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlaceProducer), "placeProducer")]
[JsonDerivedType(typeof(MoveProducer), "moveProducer")]
[JsonDerivedType(typeof(StartRecipe), "startRecipe")]
[JsonDerivedType(typeof(Collect), "collect")]
[JsonDerivedType(typeof(CancelJob), "cancelJob")]
[JsonDerivedType(typeof(SpeedUp), "speedUp")]
[JsonDerivedType(typeof(UpgradeProducer), "upgradeProducer")]
[JsonDerivedType(typeof(UpgradeStorage), "upgradeStorage")]
[JsonDerivedType(typeof(BuyParcel), "buyParcel")]
[JsonDerivedType(typeof(BuyUpgrade), "buyUpgrade")]
[JsonDerivedType(typeof(SellItem), "sellItem")]
[JsonDerivedType(typeof(BuyItem), "buyItem")]
[JsonDerivedType(typeof(FillOrder), "fillOrder")]
[JsonDerivedType(typeof(DiscardOrder), "discardOrder")]
[JsonDerivedType(typeof(EndTurn), "endTurn")]
[JsonDerivedType(typeof(SetPace), "setPace")]
[JsonDerivedType(typeof(SetKeep), "setKeep")]
[JsonDerivedType(typeof(PauseProducer), "pauseProducer")]
[JsonDerivedType(typeof(SetPlan), "setPlan")]
public abstract record GameCommand;

public sealed record PlaceProducer(string Producer, int X, int Y) : GameCommand;
public sealed record MoveProducer(int PlacementId, int X, int Y) : GameCommand;
public sealed record StartRecipe(int PlacementId, string Recipe) : GameCommand;

/// <summary>Collects every finished job on a placement that fits in storage.</summary>
public sealed record Collect(int PlacementId) : GameCommand;

/// <summary>Removes a queued job that hasn't started (by its index in the placement's queue) and refunds its inputs.</summary>
public sealed record CancelJob(int PlacementId, int Index) : GameCommand;

/// <summary>Pays to finish the placement's next unfinished job now.</summary>
public sealed record SpeedUp(int PlacementId) : GameCommand;
/// <summary>Buys the next tier of a placement's slots (more can be queued) or lanes (more run at once).</summary>
public sealed record UpgradeProducer(int PlacementId, UpgradeTrack Track = UpgradeTrack.Slots) : GameCommand;
public sealed record UpgradeStorage(string Storage) : GameCommand;
public sealed record BuyParcel(string Parcel) : GameCommand;
public sealed record BuyUpgrade(string Upgrade) : GameCommand;
public sealed record SellItem(string Item, int Quantity) : GameCommand;

/// <summary>Buys goods from the market at its buy price, for when you're short of seed or an order's items.</summary>
public sealed record BuyItem(string Item, int Quantity) : GameCommand;
public sealed record FillOrder(int Slot) : GameCommand;
public sealed record DiscardOrder(int Slot) : GameCommand;

/// <summary>Changes how fast production runs (see <see cref="State.PlayerState.Pace"/>). Jobs already underway speed up or slow down too.</summary>
public sealed record SetPace(double Pace) : GameCommand;

/// <summary>Sets how many of an item the farm manager leaves in storage; null stops the manager selling it at all.</summary>
public sealed record SetKeep(string Item, int? Minimum) : GameCommand;

/// <summary>Stops (or resumes) a self-running placement collecting and restarting by itself.</summary>
public sealed record PauseProducer(int PlacementId, bool Paused) : GameCommand;

/// <summary>Sets the recipes a self-running placement makes in turn. An empty list goes back to repeating the last recipe.</summary>
public sealed record SetPlan(int PlacementId, List<string> Recipes) : GameCommand;

/// <summary>Turn-mode packs only: advances the clock by one turn.</summary>
public sealed record EndTurn : GameCommand;
