using System.Text.Json.Serialization;

namespace Collector.Engine.Commands;

/// <summary>A player action. Commands never carry a timestamp: the engine uses the state's own clock.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlaceProducer), "placeProducer")]
[JsonDerivedType(typeof(MoveProducer), "moveProducer")]
[JsonDerivedType(typeof(StartRecipe), "startRecipe")]
[JsonDerivedType(typeof(Collect), "collect")]
[JsonDerivedType(typeof(SpeedUp), "speedUp")]
[JsonDerivedType(typeof(UpgradeProducer), "upgradeProducer")]
[JsonDerivedType(typeof(UpgradeStorage), "upgradeStorage")]
[JsonDerivedType(typeof(BuyParcel), "buyParcel")]
[JsonDerivedType(typeof(SellItem), "sellItem")]
[JsonDerivedType(typeof(FillOrder), "fillOrder")]
[JsonDerivedType(typeof(DiscardOrder), "discardOrder")]
[JsonDerivedType(typeof(EndTurn), "endTurn")]
public abstract record GameCommand;

public sealed record PlaceProducer(string Producer, int X, int Y) : GameCommand;
public sealed record MoveProducer(int PlacementId, int X, int Y) : GameCommand;
public sealed record StartRecipe(int PlacementId, string Recipe) : GameCommand;

/// <summary>Collects every finished job on a placement that fits in storage.</summary>
public sealed record Collect(int PlacementId) : GameCommand;

/// <summary>Pays to finish the placement's next unfinished job now.</summary>
public sealed record SpeedUp(int PlacementId) : GameCommand;
public sealed record UpgradeProducer(int PlacementId) : GameCommand;
public sealed record UpgradeStorage(string Storage) : GameCommand;
public sealed record BuyParcel(string Parcel) : GameCommand;
public sealed record SellItem(string Item, int Quantity) : GameCommand;
public sealed record FillOrder(int Slot) : GameCommand;
public sealed record DiscardOrder(int Slot) : GameCommand;

/// <summary>Turn-mode packs only: advances the clock by one turn.</summary>
public sealed record EndTurn : GameCommand;
