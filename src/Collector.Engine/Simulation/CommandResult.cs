using System.Text.Json.Serialization;

namespace Collector.Engine.Simulation;

public enum ErrorCode
{
    None,
    UnknownDefinition,
    NotFound,
    Locked,
    LimitReached,
    CannotAfford,
    MissingItems,
    StorageFull,
    InvalidPosition,
    NoFreeSlot,
    NotReady,
    MaxTier,
    InvalidCommand,
}

public sealed record CommandResult
{
    public bool Ok => Error == ErrorCode.None;
    public ErrorCode Error { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<GameEvent> Events { get; init; } = [];

    public static CommandResult Success(IReadOnlyList<GameEvent> events) => new() { Events = events };
    public static CommandResult Fail(ErrorCode error, string message) => new() { Error = error, Message = message };
}

/// <summary>Things that happened as a result of a command, for UI feedback (animations, toasts).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ItemsCollected), "itemsCollected")]
[JsonDerivedType(typeof(XpGained), "xpGained")]
[JsonDerivedType(typeof(LevelReached), "levelReached")]
[JsonDerivedType(typeof(OrderCompleted), "orderCompleted")]
[JsonDerivedType(typeof(OrderArrived), "orderArrived")]
public abstract record GameEvent;

public sealed record ItemsCollected(int PlacementId, IReadOnlyDictionary<string, int> Items) : GameEvent;

/// <summary>Extra goods from an upgrade's bonus chance, already included in the matching <see cref="ItemsCollected"/>.</summary>
public sealed record BonusYield(int PlacementId, IReadOnlyDictionary<string, int> Items) : GameEvent;
public sealed record XpGained(long Amount) : GameEvent;
public sealed record LevelReached(int Level, IReadOnlyDictionary<string, long> Rewards, IReadOnlyDictionary<string, int> ItemRewards) : GameEvent;
public sealed record OrderCompleted(int Slot, int OrderId) : GameEvent;
public sealed record OrderArrived(int Slot, int OrderId) : GameEvent;
