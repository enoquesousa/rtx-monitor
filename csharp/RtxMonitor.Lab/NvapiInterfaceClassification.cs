using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RtxMonitor.Lab;

public sealed record NvapiInterfaceClassificationEntry(
    string InterfaceId,
    int CallCount,
    string Classification,
    string? PublicFunction);

public sealed record NvapiInterfaceClassificationReport(
    int SchemaVersion,
    string SourceKind,
    string ObservationArtifactSha256,
    string InterfaceTableArtifactSha256,
    string GpuzSha256,
    string CapturedUtc,
    int ObservationCount,
    int ObservedUniqueInterfaceCount,
    int PublicCatalogMatchCount,
    int NotInPublicCatalogCount,
    IReadOnlyList<NvapiInterfaceClassificationEntry> Interfaces,
    IReadOnlyList<string> Warnings);

public sealed class NvapiInterfaceClassificationException : Exception
{
    public NvapiInterfaceClassificationException(string message)
        : base(message)
    {
    }
}

public static partial class NvapiInterfaceClassification
{
    public const int SchemaVersion = 1;
    public const string SourceKind = "nvapi_interface_classification";
    private const int MaximumInputBytes = 4 * 1024 * 1024;

    public static NvapiInterfaceClassificationReport AnalyzeFiles(
        string observationPath,
        string interfaceTablePath)
    {
        byte[] observationBytes = ReadBoundedFile(observationPath, "observation report");
        byte[] interfaceTableBytes = ReadBoundedFile(interfaceTablePath, "interface table");
        NvapiObservation observation = ParseObservation(observationBytes);
        IReadOnlyDictionary<string, string> publicInterfaces = ParseInterfaceTable(
            interfaceTableBytes);

        var entries = new List<NvapiInterfaceClassificationEntry>(
            observation.Interfaces.Count);
        int publicCount = 0;
        foreach (NvapiObservationEntry item in observation.Interfaces)
        {
            if (publicInterfaces.TryGetValue(item.InterfaceId, out string? function))
            {
                publicCount++;
                entries.Add(
                    new NvapiInterfaceClassificationEntry(
                        item.InterfaceId,
                        item.CallCount,
                        "public_catalog_match",
                        function));
            }
            else
            {
                entries.Add(
                    new NvapiInterfaceClassificationEntry(
                        item.InterfaceId,
                        item.CallCount,
                        "not_in_public_catalog",
                        null));
            }
        }

        return new NvapiInterfaceClassificationReport(
            SchemaVersion,
            SourceKind,
            Sha256Hex(observationBytes),
            Sha256Hex(interfaceTableBytes),
            observation.GpuzSha256,
            observation.CapturedUtc,
            observation.ObservationCount,
            entries.Count,
            publicCount,
            entries.Count - publicCount,
            entries,
            [
                "A public catalog match identifies the queried NVAPI function name, not the telemetry value returned by GPU-Z.",
                "An ID absent from the supplied public table may be private, obsolete, version-specific, or a failed capability probe; absence alone does not identify a sensor.",
                "This analysis is offline and never calls an observed interface ID.",
            ]);
    }

    private static byte[] ReadBoundedFile(string path, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new NvapiInterfaceClassificationException(
                $"The {label} does not exist: '{path}'.");
        }

        if (info.Length <= 0 || info.Length > MaximumInputBytes)
        {
            throw new NvapiInterfaceClassificationException(
                $"The {label} must contain 1 to {MaximumInputBytes} bytes.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != info.Length)
        {
            throw new NvapiInterfaceClassificationException(
                $"The {label} changed while it was being read.");
        }

        return bytes;
    }

    private static NvapiObservation ParseObservation(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            JsonElement root = document.RootElement;
            RequireObject(root, "observation report");
            RequireOnlyProperties(
                root,
                "schema_version",
                "source_kind",
                "captured_utc",
                "duration_seconds",
                "gpuz_sha256",
                "forced_target_process_ids",
                "observation_count",
                "unique_interface_count",
                "interfaces",
                "warning");

            int schemaVersion = RequireInt32(root, "schema_version", minimum: 1);
            if (schemaVersion != 1)
            {
                throw new NvapiInterfaceClassificationException(
                    $"Unsupported observation schema version {schemaVersion}.");
            }

            string sourceKind = RequireString(root, "source_kind");
            if (sourceKind != "nvapi_query_interface_observation")
            {
                throw new NvapiInterfaceClassificationException(
                    "The observation source_kind is not nvapi_query_interface_observation.");
            }

            string capturedUtc = RequireString(root, "captured_utc");
            if (!DateTimeOffset.TryParse(capturedUtc, out DateTimeOffset parsedTimestamp) ||
                parsedTimestamp.Offset != TimeSpan.Zero)
            {
                throw new NvapiInterfaceClassificationException(
                    "The observation captured_utc must be a valid UTC timestamp.");
            }

            string gpuzSha256 = RequireString(root, "gpuz_sha256");
            if (!LowerSha256Regex().IsMatch(gpuzSha256))
            {
                throw new NvapiInterfaceClassificationException(
                    "The observation gpuz_sha256 must be lowercase SHA-256.");
            }

            _ = RequireInt32(root, "duration_seconds", minimum: 1);
            _ = RequireInt32Array(root, "forced_target_process_ids");
            int observationCount = RequireInt32(root, "observation_count", minimum: 0);
            int uniqueCount = RequireInt32(root, "unique_interface_count", minimum: 0);
            JsonElement interfacesElement = RequireProperty(root, "interfaces");
            if (interfacesElement.ValueKind != JsonValueKind.Array)
            {
                throw new NvapiInterfaceClassificationException(
                    "The observation interfaces property must be an array.");
            }

            var interfaces = new List<NvapiObservationEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int summedCalls = 0;
            foreach (JsonElement element in interfacesElement.EnumerateArray())
            {
                RequireObject(element, "interface observation");
                RequireOnlyProperties(element, "interface_id", "call_count");
                string id = RequireString(element, "interface_id");
                if (!InterfaceIdRegex().IsMatch(id))
                {
                    throw new NvapiInterfaceClassificationException(
                        $"Invalid NVAPI interface ID '{id}'.");
                }

                if (!seen.Add(id))
                {
                    throw new NvapiInterfaceClassificationException(
                        $"Duplicate NVAPI interface ID '{id}'.");
                }

                int callCount = RequireInt32(element, "call_count", minimum: 1);
                summedCalls = checked(summedCalls + callCount);
                interfaces.Add(new NvapiObservationEntry(id, callCount));
            }

            _ = RequireString(root, "warning");
            if (interfaces.Count != uniqueCount || summedCalls != observationCount)
            {
                throw new NvapiInterfaceClassificationException(
                    "Observation counts do not match the interface entries.");
            }

            return new NvapiObservation(
                capturedUtc,
                gpuzSha256,
                observationCount,
                interfaces);
        }
        catch (JsonException error)
        {
            throw new NvapiInterfaceClassificationException(
                $"The observation report is not valid JSON: {error.Message}");
        }
        catch (OverflowException)
        {
            throw new NvapiInterfaceClassificationException(
                "Observation call counts exceed the supported range.");
        }
    }

    private static IReadOnlyDictionary<string, string> ParseInterfaceTable(byte[] bytes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new NvapiInterfaceClassificationException(
                $"The NVAPI interface table is not valid UTF-8: {error.Message}");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in InterfaceTableEntryRegex().Matches(text))
        {
            string function = match.Groups["function"].Value;
            string id = $"0x{match.Groups["id"].Value.ToLowerInvariant()}";
            if (!result.TryAdd(id, function))
            {
                throw new NvapiInterfaceClassificationException(
                    $"The NVAPI interface table contains duplicate ID '{id}'.");
            }
        }

        if (result.Count == 0)
        {
            throw new NvapiInterfaceClassificationException(
                "No public NVAPI entries were found in the supplied interface table.");
        }

        return result;
    }

    private static JsonElement RequireProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            throw new NvapiInterfaceClassificationException(
                $"Required property '{name}' is missing.");
        }

        return value;
    }

    private static string RequireString(JsonElement element, string name)
    {
        JsonElement value = RequireProperty(element, name);
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(value.GetString()))
        {
            throw new NvapiInterfaceClassificationException(
                $"Property '{name}' must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static int RequireInt32(JsonElement element, string name, int minimum)
    {
        JsonElement value = RequireProperty(element, name);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int number) ||
            number < minimum)
        {
            throw new NvapiInterfaceClassificationException(
                $"Property '{name}' must be an integer greater than or equal to {minimum}.");
        }

        return number;
    }

    private static IReadOnlyList<int> RequireInt32Array(JsonElement element, string name)
    {
        JsonElement value = RequireProperty(element, name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new NvapiInterfaceClassificationException(
                $"Property '{name}' must be an array.");
        }

        var values = new List<int>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number ||
                !item.TryGetInt32(out int number) ||
                number <= 0)
            {
                throw new NvapiInterfaceClassificationException(
                    $"Property '{name}' must contain only positive process IDs.");
            }

            values.Add(number);
        }

        return values;
    }

    private static void RequireObject(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new NvapiInterfaceClassificationException(
                $"The {label} must be a JSON object.");
        }
    }

    private static void RequireOnlyProperties(JsonElement element, params string[] names)
    {
        var accepted = new HashSet<string>(names, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!accepted.Contains(property.Name))
            {
                throw new NvapiInterfaceClassificationException(
                    $"Unsupported property '{property.Name}'.");
            }

            if (!seen.Add(property.Name))
            {
                throw new NvapiInterfaceClassificationException(
                    $"Duplicate property '{property.Name}'.");
            }
        }
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [GeneratedRegex("^0x[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceIdRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerSha256Regex();

    [GeneratedRegex(
        "\\{\\s*\"(?<function>NvAPI_[A-Za-z0-9_]+)\"\\s*,\\s*0x(?<id>[0-9A-Fa-f]{8})\\s*\\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceTableEntryRegex();

    private sealed record NvapiObservation(
        string CapturedUtc,
        string GpuzSha256,
        int ObservationCount,
        IReadOnlyList<NvapiObservationEntry> Interfaces);

    private sealed record NvapiObservationEntry(string InterfaceId, int CallCount);
}
