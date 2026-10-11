using System.Text.Json;
using System.Text.Json.Serialization;

namespace Collector.Engine.Serialization;

/// <summary>JSON settings shared by pack files, saved state, commands and the API, so every host reads them the same way.</summary>
public static class EngineJson
{
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>Applies the engine's settings to an options instance owned by a host (e.g. ASP.NET Core's or an HttpClient's).</summary>
    public static void ApplyTo(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.NumberHandling = JsonNumberHandling.AllowReadingFromString;
        options.ReadCommentHandling = JsonCommentHandling.Skip;
        options.AllowTrailingCommas = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ApplyTo(options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
