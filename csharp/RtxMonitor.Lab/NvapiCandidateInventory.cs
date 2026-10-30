using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RtxMonitor.Lab;

public sealed record NvapiCandidateInventoryEntry(
    string InterfaceId,
    string CatalogStatus,
    string? PublicFunction,
    string ModuleName,
    string ModuleSha256,
    string Rva,
    int QueryCount,
    int ObservedCallCount,
    string ExecutionStatus,
    string SemanticStatus);

public sealed record NvapiCandidateInventoryReport(
    int SchemaVersion,
    string SourceKind,
    string ClassificationArtifactSha256,
    string CallArtifactSha256,
    string GpuzSha256,
    string CapturedUtc,
    int CandidateCount,
    int ExecutedCandidateCount,
    int ExecutedPublicCatalogCount,
    int ExecutedNotInPublicCatalogCount,
    int ResolvedNotObservedCount,
    IReadOnlyList<NvapiCandidateInventoryEntry> Candidates,
    IReadOnlyList<string> Warnings);

public sealed class NvapiCandidateInventoryException : Exception
{
    public NvapiCandidateInventoryException(string message)
        : base(message)
    {
    }
}

public static partial class NvapiCandidateInventory
{
    public const int SchemaVersion = 1;
    public const string SourceKind = "nvapi_candidate_inventory";
    private const int MaximumInputBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions InputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static NvapiCandidateInventoryReport AnalyzeFiles(
        string classificationPath,
        string callReportPath)
    {
        byte[] classificationBytes = ReadBoundedFile(
            classificationPath,
            "classification report");
        byte[] callBytes = ReadBoundedFile(callReportPath, "call report");
        ClassificationInput classification = Deserialize<ClassificationInput>(
            classificationBytes,
            "classification report");
        CallInput calls = Deserialize<CallInput>(callBytes, "call report");

        Dictionary<string, ClassificationEntryInput> classifications =
            ValidateClassification(classification);
        Dictionary<string, CallTargetInput> targets = ValidateCalls(calls);
        if (!string.Equals(
                classification.GpuzSha256,
                calls.GpuzSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                classification.CapturedUtc,
                calls.CapturedUtc,
                StringComparison.Ordinal))
        {
            throw new NvapiCandidateInventoryException(
                "The classification and call reports do not describe the same capture.");
        }

        if (!classifications.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(targets.Keys))
        {
            throw new NvapiCandidateInventoryException(
                "The classification and call reports contain different interface ID sets.");
        }

        var entries = new List<NvapiCandidateInventoryEntry>(classifications.Count);
        foreach ((string interfaceId, ClassificationEntryInput item) in classifications)
        {
            CallTargetInput target = targets[interfaceId];
            bool executed = target.CallCount > 0;
            entries.Add(
                new NvapiCandidateInventoryEntry(
                    interfaceId,
                    item.Classification!,
                    item.PublicFunction,
                    target.ModuleName!,
                    target.ModuleSha256!,
                    target.Rva!,
                    item.CallCount,
                    target.CallCount,
                    executed ? "executed_entry" : "resolved_entry_not_observed",
                    item.Classification == "public_catalog_match"
                        ? "public_symbol_only"
                        : "unidentified_binary_candidate"));
        }

        entries.Sort(
            (left, right) =>
            {
                int byCalls = right.ObservedCallCount.CompareTo(left.ObservedCallCount);
                return byCalls != 0
                    ? byCalls
                    : string.CompareOrdinal(left.InterfaceId, right.InterfaceId);
            });

        int executedCount = entries.Count(entry => entry.ObservedCallCount > 0);
        int executedPublic = entries.Count(
            entry => entry.ObservedCallCount > 0 &&
                entry.CatalogStatus == "public_catalog_match");
        int executedNotPublic = entries.Count(
            entry => entry.ObservedCallCount > 0 &&
                entry.CatalogStatus == "not_in_public_catalog");
        return new NvapiCandidateInventoryReport(
            SchemaVersion,
            SourceKind,
            Sha256Hex(classificationBytes),
            Sha256Hex(callBytes),
            classification.GpuzSha256!,
            classification.CapturedUtc!,
            entries.Count,
            executedCount,
            executedPublic,
            executedNotPublic,
            entries.Count - executedCount,
            entries,
            [
                "executed_entry proves only that GPU-Z entered the resolved address during this capture; it does not reveal the ABI or returned telemetry fields.",
                "public_symbol_only names a cataloged NVAPI entry but does not prove which GPU-Z field consumed its output.",
                "unidentified_binary_candidate is a prioritized reverse-engineering target, not an identified sensor.",
            ]);
    }

    private static Dictionary<string, ClassificationEntryInput> ValidateClassification(
        ClassificationInput input)
    {
        if (input.SchemaVersion != 1 ||
            input.SourceKind != "nvapi_interface_classification" ||
            !ValidSha(input.ObservationArtifactSha256) ||
            !ValidSha(input.InterfaceTableArtifactSha256) ||
            !ValidSha(input.GpuzSha256) ||
            !ValidUtc(input.CapturedUtc) ||
            input.Interfaces is null ||
            input.Interfaces.Count == 0 ||
            input.Warnings is null ||
            input.Warnings.Count == 0 ||
            input.Warnings.Any(string.IsNullOrWhiteSpace))
        {
            throw new NvapiCandidateInventoryException(
                "The classification report has invalid provenance or structure.");
        }

        var result = new Dictionary<string, ClassificationEntryInput>(StringComparer.Ordinal);
        int publicCount = 0;
        foreach (ClassificationEntryInput item in input.Interfaces)
        {
            bool isPublic = item.Classification == "public_catalog_match";
            bool isUnknown = item.Classification == "not_in_public_catalog";
            if (!ValidInterfaceId(item.InterfaceId) ||
                item.CallCount < 1 ||
                (!isPublic && !isUnknown) ||
                (isPublic && !ValidPublicFunction(item.PublicFunction)) ||
                (isUnknown && item.PublicFunction is not null) ||
                !result.TryAdd(item.InterfaceId!, item))
            {
                throw new NvapiCandidateInventoryException(
                    "The classification report contains an invalid or duplicate interface entry.");
            }

            publicCount += isPublic ? 1 : 0;
        }

        if (input.ObservedUniqueInterfaceCount != result.Count ||
            input.PublicCatalogMatchCount != publicCount ||
            input.NotInPublicCatalogCount != result.Count - publicCount ||
            input.ObservationCount != result.Values.Sum(item => item.CallCount))
        {
            throw new NvapiCandidateInventoryException(
                "The classification report counts do not match its interface entries.");
        }

        return result;
    }

    private static Dictionary<string, CallTargetInput> ValidateCalls(CallInput input)
    {
        if (input.SchemaVersion != 1 ||
            input.SourceKind != "nvapi_function_call_observation" ||
            !ValidSha(input.GpuzSha256) ||
            !ValidSha(input.ResolutionReportSha256) ||
            !ValidUtc(input.CapturedUtc) ||
            input.DurationSeconds < 1 ||
            input.Targets is null ||
            input.Targets.Count == 0 ||
            string.IsNullOrWhiteSpace(input.Warning))
        {
            throw new NvapiCandidateInventoryException(
                "The call report has invalid provenance or structure.");
        }

        var result = new Dictionary<string, CallTargetInput>(StringComparer.Ordinal);
        var targetKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (CallTargetInput target in input.Targets)
        {
            if (target.ModuleName is not ("nvapi.dll" or "nvapi_impl.dll") ||
                !ValidSha(target.ModuleSha256) ||
                !ValidRva(target.Rva) ||
                target.CallCount < 0 ||
                target.InterfaceIds is null ||
                target.InterfaceIds.Count == 0 ||
                !targetKeys.Add($"{target.ModuleSha256}:{target.Rva}"))
            {
                throw new NvapiCandidateInventoryException(
                    "The call report contains an invalid or duplicate binary target.");
            }

            foreach (string? interfaceId in target.InterfaceIds)
            {
                if (!ValidInterfaceId(interfaceId) ||
                    !result.TryAdd(interfaceId!, target))
                {
                    throw new NvapiCandidateInventoryException(
                        "The call report contains an invalid or duplicate interface ID.");
                }
            }
        }

        if (input.TargetCount != input.Targets.Count ||
            input.ObservedTargetCount != input.Targets.Count(target => target.CallCount > 0) ||
            input.CallCount != input.Targets.Sum(target => target.CallCount))
        {
            throw new NvapiCandidateInventoryException(
                "The call report counts do not match its targets.");
        }

        return result;
    }

    private static T Deserialize<T>(byte[] bytes, string label)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, InputOptions) ??
                throw new NvapiCandidateInventoryException(
                    $"The {label} must contain a JSON object.");
        }
        catch (JsonException error)
        {
            throw new NvapiCandidateInventoryException(
                $"The {label} is not valid: {error.Message}");
        }
    }

    private static byte[] ReadBoundedFile(string path, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumInputBytes)
        {
            throw new NvapiCandidateInventoryException(
                $"The {label} must exist and contain 1 to {MaximumInputBytes} bytes.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != info.Length)
        {
            throw new NvapiCandidateInventoryException(
                $"The {label} changed while it was being read.");
        }

        return bytes;
    }

    private static bool ValidSha(string? value) =>
        value is not null && Sha256Regex().IsMatch(value);

    private static bool ValidInterfaceId(string? value) =>
        value is not null && InterfaceIdRegex().IsMatch(value);

    private static bool ValidRva(string? value) =>
        value is not null && RvaRegex().IsMatch(value);

    private static bool ValidPublicFunction(string? value) =>
        value is not null && PublicFunctionRegex().IsMatch(value);

    private static bool ValidUtc(string? value) =>
        value is not null &&
        DateTimeOffset.TryParse(value, out DateTimeOffset timestamp) &&
        timestamp.Offset == TimeSpan.Zero;

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^0x[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceIdRegex();

    [GeneratedRegex("^0x[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex RvaRegex();

    [GeneratedRegex("^NvAPI_[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PublicFunctionRegex();

    private sealed class ClassificationInput
    {
        public int SchemaVersion { get; init; }
        public string? SourceKind { get; init; }
        public string? ObservationArtifactSha256 { get; init; }
        public string? InterfaceTableArtifactSha256 { get; init; }
        public string? GpuzSha256 { get; init; }
        public string? CapturedUtc { get; init; }
        public int ObservationCount { get; init; }
        public int ObservedUniqueInterfaceCount { get; init; }
        public int PublicCatalogMatchCount { get; init; }
        public int NotInPublicCatalogCount { get; init; }
        public List<ClassificationEntryInput>? Interfaces { get; init; }
        public List<string>? Warnings { get; init; }
    }

    private sealed class ClassificationEntryInput
    {
        public string? InterfaceId { get; init; }
        public int CallCount { get; init; }
        public string? Classification { get; init; }
        public string? PublicFunction { get; init; }
    }

    private sealed class CallInput
    {
        public int SchemaVersion { get; init; }
        public string? SourceKind { get; init; }
        public string? CapturedUtc { get; init; }
        public int DurationSeconds { get; init; }
        public string? GpuzSha256 { get; init; }
        public string? ResolutionReportSha256 { get; init; }
        public int TargetCount { get; init; }
        public int ObservedTargetCount { get; init; }
        public int CallCount { get; init; }
        public List<CallTargetInput>? Targets { get; init; }
        public string? Warning { get; init; }
    }

    private sealed class CallTargetInput
    {
        public string? ModuleName { get; init; }
        public string? ModuleSha256 { get; init; }
        public string? Rva { get; init; }
        public List<string>? InterfaceIds { get; init; }
        public int CallCount { get; init; }
    }
}
