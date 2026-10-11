using System.Security.Cryptography;
using System.Text.Json;
using Collector.Engine.Serialization;

namespace Collector.Engine.State;

public static class StateHasher
{
    /// <summary>Short hash of the serialized state. Client and server compare it to detect prediction drift.</summary>
    public static string Hash(PlayerState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, EngineJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
    }

    public static string Serialize(PlayerState state) => JsonSerializer.Serialize(state, EngineJson.Options);

    public static PlayerState Deserialize(string json) =>
        JsonSerializer.Deserialize<PlayerState>(json, EngineJson.Options)
        ?? throw new JsonException("State JSON was null.");

    public static PlayerState Clone(PlayerState state) => Deserialize(Serialize(state));
}
