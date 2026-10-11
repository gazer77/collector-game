namespace Collector.Server;

/// <summary>Bound from the "Collector" configuration section.</summary>
public sealed class CollectorOptions
{
    public const string Section = "Collector";

    /// <summary>Holds the SQLite database and the token signing key. Relative paths are under the content root.</summary>
    public string DataDirectory { get; set; } = "App_Data";

    /// <summary>Folder containing one subfolder per content pack. Defaults to the packs copied next to the server binaries.</summary>
    public string? PacksDirectory { get; set; }

    /// <summary>How long after the last request a player still counts as playing. Longer gaps count as "away".</summary>
    public int SessionTimeoutSeconds { get; set; } = 45;

    /// <summary>How far ahead of the server's clock a client's clock may be before its commands are judged on server time alone.</summary>
    public int ClockToleranceMilliseconds { get; set; } = 2000;

    public int MaxCommandsPerBatch { get; set; } = 100;
    public int TokenLifetimeDays { get; set; } = 30;
}
