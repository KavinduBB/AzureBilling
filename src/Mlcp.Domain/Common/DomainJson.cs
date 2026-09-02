using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mlcp.Domain.Common;

/// <summary>
/// Serialisation settings for the handful of domain values persisted as JSON columns.
/// </summary>
/// <remarks>
/// Enums are written as names, not numbers. A stored number silently changes meaning the day
/// someone inserts a member into the middle of an enum; a stored name fails loudly instead,
/// which is what you want for data that outlives the code that wrote it.
/// </remarks>
public static class DomainJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json)
        => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}
