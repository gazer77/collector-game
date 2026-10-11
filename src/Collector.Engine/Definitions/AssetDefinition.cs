namespace Collector.Engine.Definitions;

/// <summary>
/// How a client should draw an asset key. The engine only checks that keys exist;
/// interpretation is up to the client. Clients prefer <see cref="Image"/> and fall back to <see cref="Emoji"/>.
/// </summary>
public sealed record AssetDefinition
{
    public string? Emoji { get; init; }

    /// <summary>Path relative to the pack's <c>assets/</c> folder.</summary>
    public string? Image { get; init; }
    public string? Tint { get; init; }
}
