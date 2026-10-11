using Collector.Engine.Commands;
using Collector.Engine.Simulation;
using Collector.Engine.State;

namespace Collector.Contracts;

/// <summary>Routes shared by the server and its clients.</summary>
public static class ApiRoutes
{
    public const string GuestLogin = "api/auth/guest";
    public static string Pack(string packId) => $"api/packs/{packId}";
    public static string Game(string packId) => $"api/game/{packId}";
    public static string Commands(string packId) => $"api/game/{packId}/commands";
    public static string Heartbeat(string packId) => $"api/game/{packId}/heartbeat";
}

/// <param name="DeviceId">A random id the client generates once and keeps. It acts as the guest's credential.</param>
public sealed record GuestLoginRequest(string DeviceId);

public sealed record AuthResponse(Guid PlayerId, string Token, DateTimeOffset ExpiresAt);

/// <summary>Every file of a content pack, so a client can run the same rules as the server.</summary>
public sealed record PackBundle(string Id, string Hash, Dictionary<string, string> Files);

/// <summary>The server's authoritative view of a player's game.</summary>
public sealed record GameSync(string PackId, string PackHash, PlayerState State, string StateHash);

public sealed record CommandEnvelope(long Seq, GameCommand Command);

/// <param name="ClientTime">The game time the client believed it was when sending. Lets the server forgive small clock drift.</param>
public sealed record CommandBatch(long ClientTime, List<CommandEnvelope> Commands);

public sealed record CommandOutcome(long Seq, bool Ok, ErrorCode Error, string Message, IReadOnlyList<GameEvent> Events);

public sealed record CommandBatchResult(List<CommandOutcome> Results, GameSync Sync);
