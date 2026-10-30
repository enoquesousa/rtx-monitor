using System.Text.Json;
using System.Globalization;

namespace RtxMonitor.Service;

/// <summary>A cached, immutable publication. HTTP never starts driver work.</summary>
public sealed class ResearchTelemetryState
{
    private readonly object gate = new();
    private JsonElement snapshot = Empty("disabled", null);
    private JsonElement compactSnapshot;
    private DateTimeOffset receivedAt = DateTimeOffset.MinValue;

    public ResearchTelemetryState() => compactSnapshot = snapshot;

    public JsonElement GetSnapshot(bool summary = false)
    {
        var publication = GetPublication();
        return summary ? publication.Summary : publication.Full;
    }

    internal (JsonElement Full, JsonElement Summary) GetPublication()
    {
        lock (gate)
        {
            // A stopped or blocked child must never leave its numbers looking live.
            if (receivedAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - receivedAt > TimeSpan.FromSeconds(75))
            {
                JsonElement stale = Empty("stale", "O coletor não publicou uma atualização em 75 segundos.");
                return (stale, stale);
            }
            return (snapshot, compactSnapshot);
        }
    }

    public void Record(JsonElement report)
    {
        if (report.ValueKind != JsonValueKind.Object ||
            !report.TryGetProperty("schema_version", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int schemaVersion) || schemaVersion != 1 ||
            !report.TryGetProperty("settings_writes_requested", out JsonElement writes) || writes.ValueKind != JsonValueKind.False ||
            !report.TryGetProperty("updated_at", out JsonElement stamp) || stamp.ValueKind != JsonValueKind.String || !stamp.TryGetDateTimeOffset(out DateTimeOffset updatedAt) ||
            updatedAt > DateTimeOffset.UtcNow.AddSeconds(5) || DateTimeOffset.UtcNow - updatedAt > TimeSpan.FromSeconds(75))
            throw new InvalidDataException("Relatório de pesquisa inválido, antigo ou sem contrato de somente leitura.");
        // Build both immutable views once per publication. HTTP handlers select
        // an already cached element and never traverse the full raw inventory.
        JsonElement full = report.Clone();
        JsonElement compact = BuildSummary(full);
        lock (gate)
        {
            snapshot = full;
            compactSnapshot = compact;
            receivedAt = updatedAt;
        }
    }

    public void Unavailable(string state, string? message)
    {
        lock (gate)
        {
            snapshot = Empty(state, message);
            compactSnapshot = snapshot;
            receivedAt = DateTimeOffset.MinValue;
        }
    }

    private static JsonElement BuildSummary(JsonElement report)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in report.EnumerateObject())
            {
                if (property.Name != "collection" || property.Value.ValueKind != JsonValueKind.Object)
                {
                    // WriteTo preserves original integer/decimal JSON tokens;
                    // no telemetry value is converted through double here.
                    property.WriteTo(writer);
                    continue;
                }

                writer.WritePropertyName("collection");
                writer.WriteStartObject();
                long queryCount = 0;
                long availableCount = 0;
                long successWithoutPayload = 0;
                var invokedIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty member in property.Value.EnumerateObject())
                {
                    if (member.Name == "counts") continue;
                    if (member.Name != "suites" || member.Value.ValueKind != JsonValueKind.Array)
                    {
                        member.WriteTo(writer);
                        continue;
                    }

                    writer.WritePropertyName("suites");
                    writer.WriteStartArray();
                    foreach (JsonElement suite in member.Value.EnumerateArray())
                    {
                        if (suite.ValueKind != JsonValueKind.Object) continue;
                        writer.WriteStartObject();
                        foreach (string name in new[] { "suite", "state", "captured_at", "max_age_ms", "age_ms" })
                            if (suite.TryGetProperty(name, out JsonElement header))
                            {
                                writer.WritePropertyName(name);
                                header.WriteTo(writer);
                            }
                        int suiteQueries = 0;
                        int suiteAvailable = 0;
                        if (suite.TryGetProperty("queries", out JsonElement queries) && queries.ValueKind == JsonValueKind.Array)
                        {
                            suiteQueries = queries.GetArrayLength();
                            queryCount += suiteQueries;
                            foreach (JsonElement query in queries.EnumerateArray())
                            {
                                if (query.ValueKind != JsonValueKind.Object) continue;
                                bool available = query.TryGetProperty("available", out JsonElement value) && value.ValueKind == JsonValueKind.True;
                                if (available) { suiteAvailable++; availableCount++; }
                                if (query.TryGetProperty("status", out JsonElement status) &&
                                    status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int code))
                                {
                                    if (code == 0 && !available) successWithoutPayload++;
                                    if (query.TryGetProperty("id", out JsonElement id) && CanonicalInterfaceId(id) is string canonical)
                                        invokedIds.Add(canonical);
                                }
                            }
                        }
                        writer.WriteNumber("query_count", suiteQueries);
                        writer.WriteNumber("available_count", suiteAvailable);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WritePropertyName("counts");
                writer.WriteStartObject();
                writer.WriteNumber("query_records", queryCount);
                writer.WriteNumber("unique_ids_invoked", invokedIds.Count);
                writer.WriteNumber("available_output", availableCount);
                writer.WriteNumber("success_without_available_payload", successWithoutPayload);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        using JsonDocument compact = JsonDocument.Parse(buffer.ToArray());
        return compact.RootElement.Clone();
    }

    private static string? CanonicalInterfaceId(JsonElement id)
    {
        uint value;
        if (id.ValueKind == JsonValueKind.Number && id.TryGetUInt32(out value))
            return "0x" + value.ToString("x8", CultureInfo.InvariantCulture);
        if (id.ValueKind != JsonValueKind.String) return null;
        string? text = id.GetString();
        if (text is null || text.Length is < 3 or > 10 || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;
        return uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
            ? "0x" + value.ToString("x8", CultureInfo.InvariantCulture) : null;
    }

    private static JsonElement Empty(string state, string? message) => JsonSerializer.SerializeToElement(new
    {
        schema_version = 1,
        state,
        observed_at = (DateTimeOffset?)null,
        updated_at = DateTimeOffset.UtcNow,
        identity = (object?)null,
        collection = (object?)null,
        settings_writes_requested = false,
        error = message,
    });
}
