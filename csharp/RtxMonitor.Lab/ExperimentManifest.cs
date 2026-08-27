using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RtxMonitor.Lab;

public sealed class ExperimentManifestException : Exception
{
    public ExperimentManifestException(string message)
        : base(message)
    {
    }

    public ExperimentManifestException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

internal sealed record ExperimentArtifactPackage(
    string Role,
    string? ScenarioId,
    string RelativePath,
    string ManifestSha256,
    string ResolvedPath);

internal sealed record ExperimentScenarioWindow(
    long? BeginMonotonicNs,
    long? EndMonotonicNs);

internal sealed record ValidatedExperimentManifest(
    string ExperimentId,
    string Status,
    string Sha256,
    string PackageRoot,
    IReadOnlyList<ExperimentArtifactPackage> ArtifactPackages,
    IReadOnlyDictionary<string, ExperimentScenarioWindow> ScenarioWindows);

public static class ExperimentManifestProducer
{
    public const int SchemaVersion = 1;
    private const long MaximumManifestSizeBytes = 16L * 1024 * 1024;
    private static readonly string[] StatusValues = ["planned", "collecting", "completed", "aborted"];
    private static readonly string[] ScenarioKinds = ["idle", "graphics_load", "memory_load", "cooling", "custom"];
    private static readonly string[] MarkerPhases = ["begin", "end", "note"];
    private static readonly string[] PackageRoles =
    [
        "vbios_offline",
        "public_telemetry",
        "privileged_capture",
        "external_reference",
        "command_output",
        "other",
    ];

    public static string FinalizeFile(string inputPath, string packageRoot)
    {
        byte[] bytes = ReadRegularFile(inputPath, "experiment manifest draft");
        using JsonDocument document = Parse(bytes);
        _ = Validate(document.RootElement, bytes, packageRoot);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            document.RootElement.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    internal static ValidatedExperimentManifest ReadAndValidateFile(
        string path,
        string packageRoot,
        string? expectedSha256 = null)
    {
        byte[] bytes = ReadRegularFile(path, "experiment manifest");
        string sha256 = Sha256Hex(bytes);
        if (expectedSha256 is not null &&
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(ValidateSha256(expectedSha256, "expected manifest SHA-256")),
                Convert.FromHexString(sha256)))
        {
            throw new ExperimentManifestException(
                "Experiment manifest SHA-256 does not match the expected trust anchor.");
        }

        using JsonDocument document = Parse(bytes);
        return Validate(document.RootElement, bytes, packageRoot);
    }

    private static ValidatedExperimentManifest Validate(
        JsonElement root,
        ReadOnlySpan<byte> bytes,
        string packageRoot)
    {
        RejectDuplicateProperties(root, "$", depth: 0);
        RequireObject(
            root,
            "$",
            [
                "schema_version",
                "experiment_id",
                "status",
                "created_at_utc",
                "started_at_utc",
                "completed_at_utc",
                "software",
                "host",
                "gpu_profile",
                "timebase",
                "allowlist",
                "scenarios",
                "markers",
                "artifact_packages",
                "external_reference",
                "notes",
            ]);

        RequireInteger(root, "schema_version", 1, 1, "$");
        string experimentId = RequireUuid(root, "experiment_id", "$");
        string status = RequireEnum(root, "status", StatusValues, "$");
        DateTimeOffset createdAt = RequireUtcDateTime(root, "created_at_utc", "$");
        DateTimeOffset? startedAt = RequireNullableUtcDateTime(root, "started_at_utc", "$");
        DateTimeOffset? completedAt = RequireNullableUtcDateTime(root, "completed_at_utc", "$");
        ValidateTimeline(status, createdAt, startedAt, completedAt);
        ValidateSoftware(root.GetProperty("software"));
        ValidateHost(root.GetProperty("host"));
        ValidateGpuProfile(root.GetProperty("gpu_profile"));
        long monotonicFrequency = ValidateTimebase(root.GetProperty("timebase"));
        bool hasAllowlist = ValidateAllowlist(root.GetProperty("allowlist"));
        HashSet<string> scenarioIds = ValidateScenarios(root.GetProperty("scenarios"));
        IReadOnlyDictionary<string, ExperimentScenarioWindow> scenarioWindows = ValidateMarkers(
            root.GetProperty("markers"),
            scenarioIds,
            monotonicFrequency,
            status);
        IReadOnlyList<ExperimentArtifactPackage> packages = ValidatePackages(
            root.GetProperty("artifact_packages"),
            packageRoot,
            hasAllowlist,
            status,
            scenarioIds);
        ValidateExternalReference(root.GetProperty("external_reference"));
        _ = RequireString(root, "notes", 0, 16_384, "$");

        return new ValidatedExperimentManifest(
            experimentId,
            status,
            Sha256Hex(bytes),
            Path.GetFullPath(packageRoot),
            packages,
            scenarioWindows);
    }

    private static void ValidateSoftware(JsonElement value)
    {
        const string path = "$.software";
        RequireObject(
            value,
            path,
            ["rtx_monitor_version", "coordinator_version", "helper_version", "analyzer_version"]);
        _ = RequireString(value, "rtx_monitor_version", 1, 128, path);
        _ = RequireString(value, "coordinator_version", 1, 128, path);
        ValidateNullableString(value, "helper_version", 128, path);
        ValidateNullableString(value, "analyzer_version", 128, path);
    }

    private static void ValidateHost(JsonElement value)
    {
        const string path = "$.host";
        RequireObject(value, path, ["os", "os_version", "kernel_version", "architecture"]);
        _ = RequireEnum(value, "os", ["windows", "linux"], path);
        _ = RequireString(value, "os_version", 1, 256, path);
        ValidateNullableString(value, "kernel_version", 256, path);
        _ = RequireString(value, "architecture", 1, 64, path);
    }

    private static void ValidateGpuProfile(JsonElement value)
    {
        const string path = "$.gpu_profile";
        RequireObject(
            value,
            path,
            [
                "gpu_uuid",
                "gpu_name",
                "pci_address",
                "pci_vendor_id",
                "pci_device_id",
                "pci_subsystem_vendor_id",
                "pci_subsystem_device_id",
                "pci_revision_id",
                "profile_key",
                "vbios_version",
                "vbios_sha256",
                "driver_version",
                "nvml_version",
                "gsp_version",
            ]);
        _ = RequireString(value, "gpu_uuid", 1, 256, path);
        _ = RequireString(value, "gpu_name", 1, 256, path);
        RequirePattern(
            RequireString(value, "pci_address", 12, 12, path),
            static text =>
                text.Length == 12 &&
                IsLowerHex(text.AsSpan(0, 4)) &&
                text[4] == ':' &&
                IsLowerHex(text.AsSpan(5, 2)) &&
                text[7] == ':' &&
                IsLowerHex(text.AsSpan(8, 2)) &&
                text[10] == '.' &&
                text[11] is >= '0' and <= '7',
            $"{path}.pci_address");
        foreach (string property in new[]
                 {
                     "pci_vendor_id",
                     "pci_device_id",
                     "pci_subsystem_vendor_id",
                     "pci_subsystem_device_id",
                 })
        {
            RequirePattern(
                RequireString(value, property, 4, 4, path),
                static text => IsLowerHex(text.AsSpan()),
                $"{path}.{property}");
        }

        RequirePattern(
            RequireString(value, "pci_revision_id", 2, 2, path),
            static text => IsLowerHex(text.AsSpan()),
            $"{path}.pci_revision_id");
        _ = RequireString(value, "profile_key", 1, 512, path);
        ValidateNullableString(value, "vbios_version", 128, path);
        ValidateNullableSha256(value, "vbios_sha256", path);
        _ = RequireString(value, "driver_version", 1, 128, path);
        _ = RequireString(value, "nvml_version", 1, 128, path);
        ValidateNullableString(value, "gsp_version", 128, path);
    }

    private static long ValidateTimebase(JsonElement value)
    {
        const string path = "$.timebase";
        RequireObject(value, path, ["utc_clock", "monotonic_clock", "monotonic_frequency_hz"]);
        _ = RequireString(value, "utc_clock", 1, 256, path);
        _ = RequireString(value, "monotonic_clock", 1, 256, path);
        return RequireInteger(value, "monotonic_frequency_hz", 1, long.MaxValue, path);
    }

    private static bool ValidateAllowlist(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        const string path = "$.allowlist";
        RequireObject(
            value,
            path,
            ["allowlist_id", "allowlist_sha256", "helper_protocol_version", "operations"]);
        _ = RequireString(value, "allowlist_id", 1, 256, path);
        _ = ValidateSha256(RequireString(value, "allowlist_sha256", 64, 64, path), path);
        _ = RequireInteger(value, "helper_protocol_version", 1, int.MaxValue, path);
        JsonElement operations = RequireArray(value, "operations", 1, 256, path);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            string operationPath = $"{path}.operations[{index}]";
            RequireObject(
                operation,
                operationPath,
                [
                    "operation_id",
                    "source_kind",
                    "offset_bytes",
                    "width_bytes",
                    "endianness",
                    "maximum_samples",
                    "minimum_interval_ms",
                    "rationale",
                    "risk_review",
                ]);
            string id = RequireIdentifier(operation, "operation_id", operationPath);
            if (!ids.Add(id))
            {
                throw new ExperimentManifestException($"Duplicate operation_id '{id}'.");
            }

            _ = RequireEnum(operation, "source_kind", ["pci_config", "bar0_mmio"], operationPath);
            long offset = RequireInteger(operation, "offset_bytes", 0, uint.MaxValue, operationPath);
            long width = RequireInteger(operation, "width_bytes", 1, 8, operationPath);
            if (width is not (1 or 2 or 4 or 8) || offset % width != 0)
            {
                throw new ExperimentManifestException(
                    $"{operationPath} must use an aligned width of 1, 2, 4, or 8 bytes.");
            }

            _ = RequireEnum(operation, "endianness", ["little", "big"], operationPath);
            _ = RequireInteger(operation, "maximum_samples", 1, 1_000_000, operationPath);
            _ = RequireInteger(operation, "minimum_interval_ms", 1, 60_000, operationPath);
            _ = RequireString(operation, "rationale", 1, 4096, operationPath);
            _ = RequireString(operation, "risk_review", 1, 4096, operationPath);
            index++;
        }

        return true;
    }

    private static HashSet<string> ValidateScenarios(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 64)
        {
            throw new ExperimentManifestException("$.scenarios must contain between 1 and 64 entries.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement scenario in value.EnumerateArray())
        {
            string path = $"$.scenarios[{index}]";
            RequireObject(scenario, path, ["scenario_id", "kind", "description", "commands"]);
            string id = RequireIdentifier(scenario, "scenario_id", path);
            if (!ids.Add(id))
            {
                throw new ExperimentManifestException($"Duplicate scenario_id '{id}'.");
            }

            _ = RequireEnum(scenario, "kind", ScenarioKinds, path);
            _ = RequireString(scenario, "description", 1, 4096, path);
            JsonElement commands = RequireArray(scenario, "commands", 0, 64, path);
            int commandIndex = 0;
            foreach (JsonElement command in commands.EnumerateArray())
            {
                _ = RequireStringValue(command, 1, 4096, $"{path}.commands[{commandIndex}]");
                commandIndex++;
            }

            index++;
        }

        return ids;
    }

    private static IReadOnlyDictionary<string, ExperimentScenarioWindow> ValidateMarkers(
        JsonElement value,
        IReadOnlySet<string> scenarioIds,
        long monotonicFrequency,
        string status)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 1_000_000)
        {
            throw new ExperimentManifestException("$.markers must be an array with at most 1000000 entries.");
        }

        var phases = scenarioIds.ToDictionary(
            id => id,
            _ => (Began: false, Ended: false, Begin: (long?)null, End: (long?)null),
            StringComparer.Ordinal);
        long previousMonotonic = -1;
        int index = 0;
        foreach (JsonElement marker in value.EnumerateArray())
        {
            string path = $"$.markers[{index}]";
            RequireObject(
                marker,
                path,
                [
                    "scenario_id",
                    "phase",
                    "utc_unix_ms",
                    "monotonic_ns",
                    "monotonic_frequency_hz",
                    "note",
                ]);
            string scenarioId = RequireIdentifier(marker, "scenario_id", path);
            if (!scenarioIds.Contains(scenarioId))
            {
                throw new ExperimentManifestException(
                    $"{path}.scenario_id does not reference a declared scenario.");
            }

            string phase = RequireEnum(marker, "phase", MarkerPhases, path);
            _ = RequireInteger(marker, "utc_unix_ms", 0, long.MaxValue, path);
            long monotonic = RequireInteger(marker, "monotonic_ns", 0, long.MaxValue, path);
            if (monotonic < previousMonotonic)
            {
                throw new ExperimentManifestException("Experiment markers are not monotonic.");
            }

            previousMonotonic = monotonic;
            long markerFrequency = RequireInteger(
                marker,
                "monotonic_frequency_hz",
                1,
                long.MaxValue,
                path);
            if (markerFrequency != monotonicFrequency)
            {
                throw new ExperimentManifestException(
                    $"{path}.monotonic_frequency_hz differs from the manifest timebase.");
            }

            ValidateNullableString(marker, "note", 4096, path);
            (bool began, bool ended, long? begin, long? end) = phases[scenarioId];
            if (phase == "begin")
            {
                if (began || ended)
                {
                    throw new ExperimentManifestException(
                        $"Scenario '{scenarioId}' has a duplicate or late begin marker.");
                }

                began = true;
                begin = monotonic;
            }
            else if (phase == "end")
            {
                if (!began || ended)
                {
                    throw new ExperimentManifestException(
                        $"Scenario '{scenarioId}' has an end marker without one open begin.");
                }

                ended = true;
                end = monotonic;
            }

            phases[scenarioId] = (began, ended, begin, end);
            index++;
        }

        if (status == "completed" && phases.Any(entry => !entry.Value.Began || !entry.Value.Ended))
        {
            throw new ExperimentManifestException(
                "A completed experiment requires begin and end markers for every scenario.");
        }

        return phases.ToDictionary(
            entry => entry.Key,
            entry => new ExperimentScenarioWindow(entry.Value.Begin, entry.Value.End),
            StringComparer.Ordinal);
    }

    private static IReadOnlyList<ExperimentArtifactPackage> ValidatePackages(
        JsonElement value,
        string packageRoot,
        bool hasAllowlist,
        string status,
        IReadOnlySet<string> scenarioIds)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 4096)
        {
            throw new ExperimentManifestException(
                "$.artifact_packages must be an array with at most 4096 entries.");
        }

        if (status == "completed" && value.GetArrayLength() == 0)
        {
            throw new ExperimentManifestException(
                "A completed experiment must reference at least one verified artifact package.");
        }

        string resolvedRoot = Path.GetFullPath(packageRoot);
        if (!Directory.Exists(resolvedRoot))
        {
            throw new ExperimentManifestException("The package root does not exist.");
        }

        string rootPrefix = EnsureTrailingSeparator(resolvedRoot);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ExperimentArtifactPackage>(value.GetArrayLength());
        int index = 0;
        foreach (JsonElement package in value.EnumerateArray())
        {
            string path = $"$.artifact_packages[{index}]";
            RequireObject(
                package,
                path,
                ["role", "scenario_id", "relative_path", "manifest_sha256"]);
            string role = RequireEnum(package, "role", PackageRoles, path);
            if (role == "privileged_capture" && !hasAllowlist)
            {
                throw new ExperimentManifestException(
                    "A privileged_capture package requires a reviewed allowlist in the manifest.");
            }

            JsonElement scenarioValue = package.GetProperty("scenario_id");
            string? scenarioId = scenarioValue.ValueKind == JsonValueKind.Null
                ? null
                : RequireIdentifier(package, "scenario_id", path);
            if (scenarioId is not null && !scenarioIds.Contains(scenarioId))
            {
                throw new ExperimentManifestException(
                    $"{path}.scenario_id does not reference a declared scenario.");
            }

            string relative = ValidateRelativePath(
                RequireString(package, "relative_path", 1, 1024, path),
                $"{path}.relative_path");
            string hash = ValidateSha256(
                RequireString(package, "manifest_sha256", 64, 64, path),
                $"{path}.manifest_sha256");
            string resolved = Path.GetFullPath(
                Path.Combine(resolvedRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                !paths.Add(resolved) ||
                !hashes.Add(hash))
            {
                throw new ExperimentManifestException(
                    $"{path} is outside the package root or duplicates another package.");
            }

            try
            {
                _ = LabPackage.Verify(resolved, hash);
            }
            catch (LabPackageException error)
            {
                throw new ExperimentManifestException(
                    $"{path} failed anchored package verification: {error.Message}",
                    error);
            }

            result.Add(new ExperimentArtifactPackage(role, scenarioId, relative, hash, resolved));
            index++;
        }

        return result;
    }

    private static void ValidateExternalReference(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        const string path = "$.external_reference";
        RequireObject(
            value,
            path,
            [
                "instrument",
                "measurement_point",
                "unit",
                "sampling_interval_ms",
                "uncertainty",
                "limitations",
            ]);
        _ = RequireString(value, "instrument", 1, 1024, path);
        _ = RequireString(value, "measurement_point", 1, 4096, path);
        _ = RequireString(value, "unit", 1, 128, path);
        double interval = RequireNumber(value, "sampling_interval_ms", path);
        if (interval <= 0)
        {
            throw new ExperimentManifestException(
                "$.external_reference.sampling_interval_ms must be positive.");
        }

        _ = RequireString(value, "uncertainty", 1, 4096, path);
        _ = RequireString(value, "limitations", 1, 4096, path);
    }

    private static void ValidateTimeline(
        string status,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt)
    {
        if ((status is "completed" or "aborted") && (startedAt is null || completedAt is null))
        {
            throw new ExperimentManifestException(
                "Completed and aborted experiments require start and completion timestamps.");
        }

        if (startedAt is not null && startedAt < createdAt)
        {
            throw new ExperimentManifestException(
                "Experiment start time cannot precede creation time.");
        }

        if (completedAt is not null && (startedAt is null || completedAt < startedAt))
        {
            throw new ExperimentManifestException(
                "Experiment completion time cannot precede its start time.");
        }
    }

    private static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            return JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
        }
        catch (JsonException error)
        {
            throw new ExperimentManifestException(
                "The experiment manifest JSON is malformed.",
                error);
        }
    }

    private static byte[] ReadRegularFile(string path, string description)
    {
        try
        {
            return LabPackage.ReadRegularFileSnapshot(
                path,
                description,
                MaximumManifestSizeBytes);
        }
        catch (LabPackageException error)
        {
            throw new ExperimentManifestException(
                $"The {description} could not be read as a stable regular-file snapshot.",
                error);
        }
    }

    private static void RequireObject(JsonElement value, string path, IReadOnlyCollection<string> properties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ExperimentManifestException($"{path} must be an object.");
        }

        var expected = new HashSet<string>(properties, StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!actual.Add(property.Name) || !expected.Contains(property.Name))
            {
                throw new ExperimentManifestException(
                    $"{path} contains duplicate or unsupported property '{property.Name}'.");
            }
        }

        string? missing = expected.FirstOrDefault(property => !actual.Contains(property));
        if (missing is not null)
        {
            throw new ExperimentManifestException($"{path} is missing property '{missing}'.");
        }
    }

    private static JsonElement RequireArray(
        JsonElement parent,
        string property,
        int minimum,
        int maximum,
        string path)
    {
        JsonElement value = parent.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() < minimum ||
            value.GetArrayLength() > maximum)
        {
            throw new ExperimentManifestException(
                $"{path}.{property} must contain between {minimum} and {maximum} entries.");
        }

        return value;
    }

    private static string RequireString(
        JsonElement parent,
        string property,
        int minimumLength,
        int maximumLength,
        string path) =>
        RequireStringValue(parent.GetProperty(property), minimumLength, maximumLength, $"{path}.{property}");

    private static string RequireStringValue(
        JsonElement value,
        int minimumLength,
        int maximumLength,
        string path)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ExperimentManifestException($"{path} must be a string.");
        }

        string result = value.GetString()!;
        if (result.Length < minimumLength ||
            result.Length > maximumLength ||
            result.Any(char.IsControl))
        {
            throw new ExperimentManifestException(
                $"{path} must contain {minimumLength} to {maximumLength} non-control characters.");
        }

        return result;
    }

    private static void ValidateNullableString(
        JsonElement parent,
        string property,
        int maximumLength,
        string path)
    {
        JsonElement value = parent.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Null)
        {
            _ = RequireStringValue(value, 1, maximumLength, $"{path}.{property}");
        }
    }

    private static string RequireEnum(
        JsonElement parent,
        string property,
        IReadOnlyCollection<string> values,
        string path)
    {
        string result = RequireString(parent, property, 1, 256, path);
        if (!values.Contains(result, StringComparer.Ordinal))
        {
            throw new ExperimentManifestException($"{path}.{property} has an unsupported value.");
        }

        return result;
    }

    private static long RequireInteger(
        JsonElement parent,
        string property,
        long minimum,
        long maximum,
        string path)
    {
        JsonElement value = parent.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long result) ||
            result < minimum ||
            result > maximum)
        {
            throw new ExperimentManifestException(
                $"{path}.{property} must be an integer between {minimum} and {maximum}.");
        }

        return result;
    }

    private static double RequireNumber(JsonElement parent, string property, string path)
    {
        JsonElement value = parent.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) ||
            !double.IsFinite(result))
        {
            throw new ExperimentManifestException($"{path}.{property} must be a finite number.");
        }

        return result;
    }

    private static string RequireUuid(JsonElement parent, string property, string path)
    {
        string value = RequireString(parent, property, 36, 36, path);
        if (!Guid.TryParseExact(value, "D", out _))
        {
            throw new ExperimentManifestException($"{path}.{property} must be a canonical UUID.");
        }

        return value;
    }

    private static DateTimeOffset RequireUtcDateTime(JsonElement parent, string property, string path)
    {
        string value = RequireString(parent, property, 1, 64, path);
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset result) ||
            result.Offset != TimeSpan.Zero)
        {
            throw new ExperimentManifestException($"{path}.{property} must be a UTC date-time.");
        }

        return result;
    }

    private static DateTimeOffset? RequireNullableUtcDateTime(
        JsonElement parent,
        string property,
        string path)
    {
        JsonElement value = parent.GetProperty(property);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : RequireUtcDateTime(parent, property, path);
    }

    private static string RequireIdentifier(JsonElement parent, string property, string path)
    {
        string value = RequireString(parent, property, 1, 128, path);
        if (value[0] is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and
                not '.' and not '_' and not '-'))
        {
            throw new ExperimentManifestException(
                $"{path}.{property} must match [a-z0-9][a-z0-9._-]{{0,127}}.");
        }

        return value;
    }

    private static void ValidateNullableSha256(JsonElement parent, string property, string path)
    {
        JsonElement value = parent.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Null)
        {
            _ = ValidateSha256(
                RequireString(parent, property, 64, 64, path),
                $"{path}.{property}");
        }
    }

    private static string ValidateSha256(string value, string path)
    {
        if (value.Length != 64 || !IsLowerHex(value.AsSpan()))
        {
            throw new ExperimentManifestException(
                $"{path} must contain exactly 64 lowercase hexadecimal characters.");
        }

        return value;
    }

    private static string ValidateRelativePath(string value, string path)
    {
        if (value.StartsWith("/", StringComparison.Ordinal) ||
            value.Contains("\\", StringComparison.Ordinal) ||
            value.Contains(":", StringComparison.Ordinal) ||
            value.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ExperimentManifestException(
                $"{path} must be a normalized relative path using '/'.");
        }

        return value;
    }

    private static void RequirePattern(string value, Func<string, bool> predicate, string path)
    {
        if (!predicate(value))
        {
            throw new ExperimentManifestException($"{path} does not match its required format.");
        }
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static void RejectDuplicateProperties(JsonElement value, string path, int depth)
    {
        if (depth > 32)
        {
            throw new ExperimentManifestException("The experiment manifest exceeds the nesting limit.");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ExperimentManifestException(
                        $"{path} contains duplicate property '{property.Name}'.");
                }

                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}", depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index}]", depth + 1);
                index++;
            }
        }
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
