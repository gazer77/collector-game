namespace Collector.Engine.Packs;

/// <summary>Reads files from a content pack. Hosts supply their own (disk on the server, HTTP in the browser, app bundle on mobile).</summary>
public interface IPackSource
{
    /// <summary>Returns the file's text, or null if it doesn't exist.</summary>
    /// <param name="relativePath">Forward-slash path relative to the pack root, e.g. <c>assets/manifest.json</c>.</param>
    Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default);
}

public sealed class DirectoryPackSource(string rootDirectory) : IPackSource
{
    public string RootDirectory { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory)) + Path.DirectorySeparatorChar;

    public async Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(Path.Combine(RootDirectory, relativePath));
        if (!path.StartsWith(RootDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path escapes the pack root: {relativePath}", nameof(relativePath));

        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
    }
}

/// <summary>In-memory pack, mainly for tests and for packs downloaded as a single bundle.</summary>
public sealed class MemoryPackSource(IReadOnlyDictionary<string, string> files) : IPackSource
{
    public Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(files.TryGetValue(relativePath, out var text) ? text : null);
}
