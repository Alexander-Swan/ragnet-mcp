using System.Globalization;
using System.Text.Json;

namespace RagNet.Mcp.Indexing;

public static class IndexSchemaVersions
{
    public const int Current = 2;

    public const string PayloadFieldName = "schema_version";

    public static string CurrentText => Current.ToString(CultureInfo.InvariantCulture);

    public static bool IsCompatible(string? storedVersion)
        => string.IsNullOrWhiteSpace(storedVersion) || (
            int.TryParse(storedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
            parsed == Current);

    public static string? ReadPayloadVersion(JsonElement payload)
    {
        if (!payload.TryGetProperty(PayloadFieldName, out var value))
        {
            return null;
        }

        return ReadVersion(value);
    }

    public static string? ReadVersion(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var version) => version.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => value.GetString(),
            _ => value.ToString()
        };

    public static void EnsureCompatible(string? storedVersion, string sourceDescription)
    {
        if (IsCompatible(storedVersion))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{sourceDescription} uses schema version '{storedVersion}', but this RagNet build supports schema version {Current}. Reindex with the current RagNet version.");
    }
}
