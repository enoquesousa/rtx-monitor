using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace RtxMonitor.Lab;

public sealed record VoltageReferenceCorrelationV2(
    string Source,
    string ArtifactPrefixSha256,
    long ArtifactPrefixSizeBytes,
    string ReferenceChannel,
    string ReferenceUnit,
    string AlignmentMethod,
    int SampleCount,
    int? SelectedSessionIndex,
    string WindowFirstTimestampLocal,
    string WindowLastTimestampLocal,
    double? MaximumAlignmentDeltaMs,
    double MeanAbsoluteErrorVolts,
    double MaximumAbsoluteErrorVolts,
    double ToleranceVolts,
    string Status);

public sealed record VoltageStatusMappingV2(
    int WordIndex,
    int OffsetBytes,
    string SemanticField,
    string Unit,
    int ObservationSampleCount,
    int DistinctRawValueCount,
    string Status);

public sealed record VoltageStatusCorrelationReportV2(
    int SchemaVersion,
    string SourceKind,
    string CaptureSessionId,
    string ProfileName,
    string ObservationSha256,
    string GpuzSha256,
    string NvapiModuleSha256,
    string InterfaceId,
    string FunctionRva,
    string CallerModuleName,
    string CallerRva,
    string StructureVersion,
    double ScaleDivisor,
    string MappingStatus,
    VoltageStatusMappingV2 Mapping,
    VoltageReferenceCorrelationV2 GpuzReference,
    VoltageReferenceCorrelationV2? HwinfoReference,
    IReadOnlyList<string> Warnings);

public sealed class VoltageStatusCorrelationV2Exception : Exception
{
    public VoltageStatusCorrelationV2Exception(string message) : base(message) { }
    public VoltageStatusCorrelationV2Exception(string message, Exception inner) : base(message, inner) { }
}

public static class VoltageStatusCorrelationV2
{
    public const int SchemaVersion = 2;
    public const string SourceKind = "nvapi_voltage_status_reference_correlation";
    public const string ProfileName = "gpuz-2.70.0-nvapi-610.88-voltage-status-v1";
    public const double ScaleDivisor = 1_000_000.0;
    public const double GpuzToleranceVolts = 0.001;
    public const double HwinfoToleranceVolts = 0.002;
    public const double MaximumHwinfoAlignmentDeltaMs = 3_000.0;
    public const double MaximumGpuzBoundaryDeltaMs = 1_500.0;

    private const long MaximumObservationSizeBytes = 16L * 1024 * 1024;
    private const string ExpectedObservationSource = "nvapi_voltage_status_v1_observation";
    private const string ExpectedGpuName = "NVIDIA GeForce RTX 3060";
    private const string ExpectedGpuUuid = "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd";
    private const string ExpectedDriverVersion = "610.88";
    private const string ExpectedNvmlVersion = "13.610.88";
    private const string ExpectedPciBusId = "00000000:01:00.0";
    private const string ExpectedVbiosVersion = "94.06.25.00.fc";
    private const string ExpectedGpuzSha256 = "6cb0ef29682452de81a9576808881685161411a1fad00938ba04131159979c29";
    private const string ExpectedNvapiSha256 = "fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf";
    private const string ExpectedCandidateInventorySha256 = "3aaada9b367dacca7cf74511bae8532bd79b7f8bd06b9bb609056f3d9da1f1d7";
    private const string ExpectedPriorObservationSha256 = "c7a63df5e6a30bccbba5ad8c1a62a9251c40d512cd74060e69e043cfc54f77b3";
    private const string ExpectedInterfaceId = "0x465f9bcf";
    private const string ExpectedFunctionRva = "0x00198010";
    private const string ExpectedCallerModuleName = "GPU-Z.exe";
    private const string ExpectedCallerRva = "0x0021cee7";
    private const string ExpectedStructureVersion = "0x0001004c";
    private static readonly string[] LocalTimestampFormats =
        ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFF"];

    public static VoltageStatusCorrelationReportV2 AnalyzeFiles(
        string observationPath,
        string gpuzLogPath,
        string? hwinfoLogPath = null)
    {
        Observation observation = ReadObservation(observationPath);
        bool observationHasHwinfo = observation.HwinfoReference is not null;
        bool callerProvidedHwinfo = !string.IsNullOrWhiteSpace(hwinfoLogPath);
        if (observationHasHwinfo != callerProvidedHwinfo)
        {
            throw new VoltageStatusCorrelationV2Exception(
                observationHasHwinfo
                    ? "The observation records HWiNFO evidence, so --hwinfo-log is required."
                    : "The observation has no HWiNFO evidence, so --hwinfo-log must be omitted.");
        }

        GpuzVoltagePrefixAnalysis gpuz;
        try
        {
            gpuz = GpuzVoltageSessionLog.AnalyzeFilePrefix(
                gpuzLogPath,
                observation.GpuzReference.SizeBytesAfter);
        }
        catch (GpuzVoltagePrefixException error)
        {
            throw new VoltageStatusCorrelationV2Exception(
                "The bounded GPU-Z reference prefix is invalid.",
                error);
        }

        ValidateArtifact(
            "GPU-Z",
            gpuz.Artifact.OriginalFileName,
            gpuz.Artifact.SizeBytes,
            gpuz.Artifact.Sha256,
            observation.GpuzReference);
        SelectedGpuzWindow gpuzWindow = SelectGpuzWindow(
            gpuz,
            observation.GpuzReference);
        VoltageReferenceCorrelationV2 gpuzResult = CompareGpuz(
            observation.Microvolts,
            gpuzWindow,
            gpuz.Artifact);

        VoltageReferenceCorrelationV2? hwinfoResult = null;
        if (observation.HwinfoReference is LogReference hwinfoReference)
        {
            HwinfoVoltageLogAnalysis hwinfo;
            try
            {
                hwinfo = HwinfoVoltageSensorLog.AnalyzeFilePrefix(
                    hwinfoLogPath!,
                    hwinfoReference.SizeBytesAfter);
            }
            catch (HwinfoVoltageLogException error)
            {
                throw new VoltageStatusCorrelationV2Exception(
                    "The bounded HWiNFO reference prefix is invalid.",
                    error);
            }

            ValidateArtifact(
                "HWiNFO",
                hwinfo.Artifact.OriginalFileName,
                hwinfo.Artifact.SizeBytes,
                hwinfo.Artifact.Sha256,
                hwinfoReference);
            hwinfoResult = CompareHwinfo(
                observation.Microvolts,
                gpuzWindow,
                hwinfo,
                hwinfoReference);
        }

        int distinctRawValueCount = observation.Microvolts.Distinct().Count();
        bool referencesMatch =
            gpuzResult.Status == "matched_rounding_tolerance" &&
            (hwinfoResult is null || hwinfoResult.Status == "matched_rounding_tolerance");
        string mappingStatus = referencesMatch && distinctRawValueCount >= 3
            ? "matched_external_reference"
            : "ambiguous_or_outside_tolerance";
        var mapping = new VoltageStatusMappingV2(
            WordIndex: 10,
            OffsetBytes: 40,
            SemanticField: "gpu_core_voltage",
            Unit: "V",
            ObservationSampleCount: observation.Microvolts.Count,
            DistinctRawValueCount: distinctRawValueCount,
            Status: mappingStatus);

        string[] warnings =
        [
            "This is a passive correlation against external software references, not a vendor-published NVAPI contract.",
            "The result is limited to the exact GPU, board, VBIOS, driver, GPU-Z, NVAPI module, and call-site profile recorded by the observation.",
            hwinfoResult is null
                ? "HWiNFO was not recorded for this session; GPU-Z is the sole external reference."
                : "HWiNFO is corroborating evidence only and was accepted because its recorded log prefix grew throughout the bounded session.",
        ];

        return new VoltageStatusCorrelationReportV2(
            SchemaVersion,
            SourceKind,
            observation.CaptureSessionId,
            ProfileName,
            observation.Sha256,
            ExpectedGpuzSha256,
            ExpectedNvapiSha256,
            ExpectedInterfaceId,
            ExpectedFunctionRva,
            ExpectedCallerModuleName,
            ExpectedCallerRva,
            ExpectedStructureVersion,
            ScaleDivisor,
            mappingStatus,
            mapping,
            gpuzResult,
            hwinfoResult,
            warnings);
    }

    private static VoltageReferenceCorrelationV2 CompareGpuz(
        IReadOnlyList<long> microvolts,
        SelectedGpuzWindow window,
        GpuzVoltagePrefixArtifact artifact)
    {
        int comparisonCount = Math.Min(microvolts.Count, window.Samples.Count);
        if (comparisonCount < 3)
        {
            throw new VoltageStatusCorrelationV2Exception(
                "The bounded GPU-Z window must provide at least three comparison points.");
        }

        int[] rawIndices = EvenlySpacedIndices(microvolts.Count, comparisonCount);
        int[] referenceIndices = EvenlySpacedIndices(window.Samples.Count, comparisonCount);
        var errors = new double[comparisonCount];
        for (int index = 0; index < comparisonCount; index++)
        {
            double rawVolts = microvolts[rawIndices[index]] / ScaleDivisor;
            double referenceVolts = window.Samples[referenceIndices[index]].VoltageVolts;
            errors[index] = RequireFiniteDerivedMetric(
                Math.Abs(rawVolts - referenceVolts),
                "GPU-Z absolute voltage error");
        }

        double maximum = RequireFiniteDerivedMetric(
            errors.Max(),
            "GPU-Z maximum absolute voltage error");
        double mean = RequireFiniteDerivedMetric(
            errors.Average(),
            "GPU-Z mean absolute voltage error");
        return new VoltageReferenceCorrelationV2(
            Source: "GPU-Z",
            ArtifactPrefixSha256: artifact.Sha256,
            ArtifactPrefixSizeBytes: artifact.SizeBytes,
            ReferenceChannel: "GPU Voltage",
            ReferenceUnit: "V",
            AlignmentMethod: "bounded_session_order_linear_resampling",
            SampleCount: comparisonCount,
            SelectedSessionIndex: window.SessionIndex,
            WindowFirstTimestampLocal: FormatLocal(window.Samples[0].Timestamp),
            WindowLastTimestampLocal: FormatLocal(window.Samples[^1].Timestamp),
            MaximumAlignmentDeltaMs: null,
            MeanAbsoluteErrorVolts: mean,
            MaximumAbsoluteErrorVolts: maximum,
            ToleranceVolts: GpuzToleranceVolts,
            Status: maximum <= GpuzToleranceVolts
                ? "matched_rounding_tolerance"
                : "outside_tolerance");
    }

    private static VoltageReferenceCorrelationV2 CompareHwinfo(
        IReadOnlyList<long> microvolts,
        SelectedGpuzWindow gpuzWindow,
        HwinfoVoltageLogAnalysis hwinfo,
        LogReference reference)
    {
        HwinfoVoltageLogSample[] bounded = hwinfo.Samples
            .Where(sample =>
                sample.ParsedTimestampLocal >= reference.LastSampleLocalBefore &&
                sample.ParsedTimestampLocal <= reference.LastSampleLocalAfter)
            .OrderBy(sample => sample.ParsedTimestampLocal)
            .ToArray();
        int comparisonCount = Math.Min(microvolts.Count, bounded.Length);
        if (comparisonCount < 3)
        {
            throw new VoltageStatusCorrelationV2Exception(
                "The bounded HWiNFO window must provide at least three comparison points.");
        }

        int[] rawIndices = EvenlySpacedIndices(microvolts.Count, comparisonCount);
        var errors = new double[comparisonCount];
        var deltas = new double[comparisonCount];
        DateTime first = gpuzWindow.Samples[0].Timestamp;
        DateTime last = gpuzWindow.Samples[^1].Timestamp;
        for (int index = 0; index < comparisonCount; index++)
        {
            int rawIndex = rawIndices[index];
            double fraction = microvolts.Count == 1
                ? 0
                : rawIndex / (double)(microvolts.Count - 1);
            DateTime estimatedTimestamp = first + TimeSpan.FromTicks(
                checked((long)Math.Round((last - first).Ticks * fraction)));
            HwinfoVoltageLogSample nearest = bounded.MinBy(sample =>
                Math.Abs((sample.ParsedTimestampLocal - estimatedTimestamp).Ticks))!;
            double deltaMs = Math.Abs(
                (nearest.ParsedTimestampLocal - estimatedTimestamp).TotalMilliseconds);
            deltas[index] = deltaMs;
            errors[index] = RequireFiniteDerivedMetric(
                Math.Abs(microvolts[rawIndex] / ScaleDivisor - nearest.VoltageVolts),
                "HWiNFO absolute voltage error");
        }

        double maximumError = RequireFiniteDerivedMetric(
            errors.Max(),
            "HWiNFO maximum absolute voltage error");
        double meanError = RequireFiniteDerivedMetric(
            errors.Average(),
            "HWiNFO mean absolute voltage error");
        double maximumDelta = RequireFiniteDerivedMetric(
            deltas.Max(),
            "HWiNFO maximum alignment delta");
        bool matched = maximumError <= HwinfoToleranceVolts &&
            maximumDelta <= MaximumHwinfoAlignmentDeltaMs;
        return new VoltageReferenceCorrelationV2(
            Source: "HWiNFO",
            ArtifactPrefixSha256: hwinfo.Artifact.Sha256,
            ArtifactPrefixSizeBytes: hwinfo.Artifact.SizeBytes,
            ReferenceChannel: "GPU Core Voltage",
            ReferenceUnit: "V",
            AlignmentMethod: "nearest_timestamp_to_bounded_order_estimate",
            SampleCount: comparisonCount,
            SelectedSessionIndex: null,
            WindowFirstTimestampLocal: bounded[0].TimestampLocal,
            WindowLastTimestampLocal: bounded[^1].TimestampLocal,
            MaximumAlignmentDeltaMs: maximumDelta,
            MeanAbsoluteErrorVolts: meanError,
            MaximumAbsoluteErrorVolts: maximumError,
            ToleranceVolts: HwinfoToleranceVolts,
            Status: matched ? "matched_rounding_tolerance" : "outside_tolerance");
    }

    private static SelectedGpuzWindow SelectGpuzWindow(
        GpuzVoltagePrefixAnalysis gpuz,
        LogReference reference)
    {
        var candidates = new List<SelectedGpuzWindow>();
        foreach (GpuzVoltageSession session in gpuz.Sessions)
        {
            if (session.InvalidSampleTimestamps.Any(timestamp =>
                    timestamp >= reference.LastSampleLocalBefore &&
                    timestamp <= reference.LastSampleLocalAfter))
            {
                continue;
            }

            GpuzVoltagePoint[][] timestampGroups = session.Samples
                .Where(point =>
                    point.Timestamp >= reference.LastSampleLocalBefore &&
                    point.Timestamp <= reference.LastSampleLocalAfter)
                .GroupBy(point => point.Timestamp)
                .Select(group => group.ToArray())
                .ToArray();
            if (timestampGroups.Any(group =>
                    group.Max(point => point.VoltageVolts) -
                    group.Min(point => point.VoltageVolts) > GpuzToleranceVolts))
            {
                continue;
            }

            GpuzVoltagePoint[] points = timestampGroups
                .Select(group => new GpuzVoltagePoint(
                    group[0].Timestamp,
                    group.Average(point => point.VoltageVolts)))
                .OrderBy(point => point.Timestamp)
                .ToArray();
            if (points.Length < 3 || Enumerable.Range(1, points.Length - 1).Any(index =>
                    points[index].Timestamp <= points[index - 1].Timestamp))
            {
                continue;
            }

            double firstBoundaryDeltaMs = Math.Abs(
                (points[0].Timestamp - reference.LastSampleLocalBefore).TotalMilliseconds);
            double midpointDeltaMs = points.Min(point => Math.Abs(
                (point.Timestamp - reference.LastSampleLocalMidpoint).TotalMilliseconds));
            double lastBoundaryDeltaMs = Math.Abs(
                (points[^1].Timestamp - reference.LastSampleLocalAfter).TotalMilliseconds);
            if (firstBoundaryDeltaMs > MaximumGpuzBoundaryDeltaMs ||
                midpointDeltaMs > MaximumGpuzBoundaryDeltaMs ||
                lastBoundaryDeltaMs > MaximumGpuzBoundaryDeltaMs)
            {
                continue;
            }

            double boundaryDistanceMs = firstBoundaryDeltaMs + midpointDeltaMs + lastBoundaryDeltaMs;
            candidates.Add(new SelectedGpuzWindow(
                session.SessionIndex,
                points,
                boundaryDistanceMs));
        }

        if (candidates.Count == 0)
        {
            throw new VoltageStatusCorrelationV2Exception(
                "No strictly ordered GPU-Z session covers the recorded before/midpoint/after boundaries within tolerance.");
        }

        SelectedGpuzWindow selected = candidates
            .OrderBy(candidate => candidate.BoundaryDistanceMs)
            .ThenBy(candidate => candidate.SessionIndex)
            .First();
        int equallyClose = candidates.Count(candidate =>
            Math.Abs(candidate.BoundaryDistanceMs - selected.BoundaryDistanceMs) < 0.0001);
        if (equallyClose != 1)
        {
            throw new VoltageStatusCorrelationV2Exception(
                "Multiple GPU-Z sessions are equally compatible with the recorded time boundaries.");
        }

        return selected;
    }

    private static Observation ReadObservation(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string resolved;
        try
        {
            resolved = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new VoltageStatusCorrelationV2Exception(
                "The voltage observation path is invalid.", error);
        }

        FileAttributes attributes = File.GetAttributes(resolved);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new VoltageStatusCorrelationV2Exception(
                "The voltage observation must be a regular local file and cannot be a reparse point.");
        }

        byte[] bytes;
        using (var stream = new FileStream(
            resolved,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan))
        {
            if (stream.Length is < 1 or > MaximumObservationSizeBytes)
            {
                throw new VoltageStatusCorrelationV2Exception(
                    "The voltage observation size is outside the analysis limit.");
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length)
            {
                throw new VoltageStatusCorrelationV2Exception(
                    "The voltage observation changed while it was being read.");
            }
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            RequireObjectProperties(root,
                "schema_version", "source_kind", "capture_session_id",
                "capture_started_utc", "captured_utc", "duration_seconds",
                "process_id", "profile", "references", "call_count",
                "samples", "warning");
            Require(root, "schema_version", 2);
            Require(root, "source_kind", ExpectedObservationSource);
            string sessionId = RequireCanonicalGuid(root, "capture_session_id");
            DateTimeOffset started = RequireUtcTimestamp(root, "capture_started_utc");
            DateTimeOffset captured = RequireUtcTimestamp(root, "captured_utc");
            int duration = RequireInt32(root, "duration_seconds", 10, 60);
            _ = RequireInt32(root, "process_id", 1, int.MaxValue);
            if (captured <= started ||
                (captured - started).TotalSeconds < duration - 2 ||
                (captured - started).TotalSeconds > duration + 30)
            {
                throw new VoltageStatusCorrelationV2Exception(
                    "The capture UTC bounds are inconsistent with duration_seconds.");
            }

            JsonElement profile = RequireObject(root, "profile");
            ValidateProfile(profile);
            JsonElement references = RequireObject(root, "references");
            RequireObjectProperties(references, "gpuz", "hwinfo");
            LogReference gpuzReference = ReadLogReference(
                RequireObject(references, "gpuz"),
                "GPU-Z");
            LogReference? hwinfoReference = references.GetProperty("hwinfo").ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Object => ReadLogReference(
                    references.GetProperty("hwinfo"), "HWiNFO"),
                _ => throw new VoltageStatusCorrelationV2Exception(
                    "references.hwinfo must be either an object or null."),
            };

            JsonElement samples = RequireArray(root, "samples");
            int callCount = RequireInt32(root, "call_count", 3, 100_000);
            if (samples.GetArrayLength() != callCount)
            {
                throw new VoltageStatusCorrelationV2Exception(
                    "call_count must equal the number of voltage samples.");
            }

            var values = new List<long>(callCount);
            int expectedSequence = 0;
            foreach (JsonElement sample in samples.EnumerateArray())
            {
                expectedSequence++;
                RequireObjectProperties(sample,
                    "sequence", "thread_id", "caller_rva", "return_status",
                    "raw_words", "selected_raw_microvolts", "selected_volts");
                Require(sample, "sequence", expectedSequence);
                RequireHex32(sample, "thread_id");
                Require(sample, "caller_rva", ExpectedCallerRva);
                Require(sample, "return_status", "0x00000000");
                JsonElement rawWords = RequireArray(sample, "raw_words");
                if (rawWords.GetArrayLength() != 19)
                {
                    throw new VoltageStatusCorrelationV2Exception(
                        "Every voltage sample must contain exactly 19 DWORDs.");
                }

                string[] words = rawWords.EnumerateArray()
                    .Select((word, index) => RequireHex32Value(word, $"raw_words[{index}]"))
                    .ToArray();
                if (words[0] != ExpectedStructureVersion)
                {
                    throw new VoltageStatusCorrelationV2Exception(
                        "The captured voltage structure version is not allowlisted.");
                }

                long raw = RequireInt64(
                    sample,
                    "selected_raw_microvolts",
                    minimum: 100_000,
                    maximum: 2_000_000);
                uint word10 = uint.Parse(
                    words[10].AsSpan(2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture);
                if (word10 != raw)
                {
                    throw new VoltageStatusCorrelationV2Exception(
                        "raw_words[10] does not equal selected_raw_microvolts.");
                }

                double volts = RequireFiniteDouble(sample, "selected_volts");
                if (Math.Abs(volts - raw / ScaleDivisor) > 1e-12)
                {
                    throw new VoltageStatusCorrelationV2Exception(
                        "selected_volts does not exactly represent word 10 in microvolts.");
                }

                values.Add(raw);
            }

            string warning = RequireString(root, "warning");
            if (warning.Length is < 1 or > 2_000)
            {
                throw new VoltageStatusCorrelationV2Exception(
                    "The observation warning is missing or too long.");
            }

            return new Observation(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                sessionId,
                gpuzReference,
                hwinfoReference,
                values);
        }
        catch (VoltageStatusCorrelationV2Exception)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException)
        {
            throw new VoltageStatusCorrelationV2Exception(
                "The voltage observation v2 JSON is malformed or incomplete.",
                error);
        }
    }

    private static void ValidateProfile(JsonElement profile)
    {
        RequireObjectProperties(profile,
            "profile_name", "gpu", "identity_probe_sha256", "gpuz_sha256",
            "debugger_sha256", "debugger_file_version",
            "candidate_inventory_sha256", "prior_observation_sha256",
            "nvapi_module_sha256", "loaded_nvapi_module", "interface_id", "function_rva",
            "caller_module_name", "caller_rva",
            "buffer_ebp_displacement_bytes", "structure_version",
            "structure_size_bytes", "value_word_index", "value_offset_bytes",
            "scale_divisor");
        Require(profile, "profile_name", ProfileName);
        JsonElement gpu = RequireObject(profile, "gpu");
        RequireObjectProperties(gpu,
            "name", "uuid", "driver_version", "nvml_version", "pci_bus_id",
            "pci_vendor_id", "pci_device_id", "pci_subsystem_vendor_id",
            "pci_subsystem_device_id", "vbios_version");
        Require(gpu, "name", ExpectedGpuName);
        Require(gpu, "uuid", ExpectedGpuUuid);
        Require(gpu, "driver_version", ExpectedDriverVersion);
        Require(gpu, "nvml_version", ExpectedNvmlVersion);
        Require(gpu, "pci_bus_id", ExpectedPciBusId);
        Require(gpu, "pci_vendor_id", "0x10de");
        Require(gpu, "pci_device_id", "0x2504");
        Require(gpu, "pci_subsystem_vendor_id", "0x10de");
        Require(gpu, "pci_subsystem_device_id", "0x1536");
        Require(gpu, "vbios_version", ExpectedVbiosVersion);
        RequireSha256(profile, "identity_probe_sha256");
        Require(profile, "gpuz_sha256", ExpectedGpuzSha256);
        RequireSha256(profile, "debugger_sha256");
        string debuggerVersion = RequireString(profile, "debugger_file_version");
        if (debuggerVersion.Length is < 1 or > 128)
        {
            throw new VoltageStatusCorrelationV2Exception(
                "debugger_file_version is invalid.");
        }

        Require(profile, "candidate_inventory_sha256", ExpectedCandidateInventorySha256);
        Require(profile, "prior_observation_sha256", ExpectedPriorObservationSha256);
        Require(profile, "nvapi_module_sha256", ExpectedNvapiSha256);
        JsonElement loadedModule = RequireObject(profile, "loaded_nvapi_module");
        RequireObjectProperties(loadedModule,
            "file_name", "file_sha256", "start_address", "end_address", "proof_source");
        Require(loadedModule, "file_name", "nvapi_impl.dll");
        Require(loadedModule, "file_sha256", ExpectedNvapiSha256);
        Require(loadedModule, "proof_source", "cdb_modload_target_image");
        uint loadedStart = Convert.ToUInt32(
            RequireHex32Value(loadedModule.GetProperty("start_address"), "start_address")[2..],
            16);
        uint loadedEnd = Convert.ToUInt32(
            RequireHex32Value(loadedModule.GetProperty("end_address"), "end_address")[2..],
            16);
        if (loadedEnd <= loadedStart)
        {
            throw new VoltageStatusCorrelationV2Exception(
                "The recorded NVAPI target-image address range is invalid.");
        }

        uint functionRva = Convert.ToUInt32(ExpectedFunctionRva[2..], 16);
        ulong functionAddress = (ulong)loadedStart + functionRva;
        if (functionAddress >= loadedEnd)
        {
            throw new VoltageStatusCorrelationV2Exception(
                "The allowlisted voltage function RVA is outside the loaded nvapi_impl.dll range.");
        }

        Require(profile, "interface_id", ExpectedInterfaceId);
        Require(profile, "function_rva", ExpectedFunctionRva);
        Require(profile, "caller_module_name", ExpectedCallerModuleName);
        Require(profile, "caller_rva", ExpectedCallerRva);
        Require(profile, "buffer_ebp_displacement_bytes", -80);
        Require(profile, "structure_version", ExpectedStructureVersion);
        Require(profile, "structure_size_bytes", 76);
        Require(profile, "value_word_index", 10);
        Require(profile, "value_offset_bytes", 40);
        Require(profile, "scale_divisor", 1_000_000);
    }

    private static LogReference ReadLogReference(JsonElement value, string source)
    {
        RequireObjectProperties(value,
            "file_name", "prefix_sha256", "size_bytes_before",
            "size_bytes_midpoint", "size_bytes_after", "last_write_utc_before",
            "last_write_utc_midpoint", "last_write_utc_after",
            "last_sample_local_before", "last_sample_local_midpoint",
            "last_sample_local_after", "grew_during_capture");
        string fileName = RequireString(value, "file_name");
        if (fileName != Path.GetFileName(fileName) || fileName.Length is < 1 or > 260 ||
            fileName.Any(char.IsControl))
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"The {source} reference file_name is invalid.");
        }

        string prefixSha256 = RequireSha256(value, "prefix_sha256");
        long sizeBefore = RequireInt64(value, "size_bytes_before", 1, 64L * 1024 * 1024);
        long sizeMidpoint = RequireInt64(value, "size_bytes_midpoint", 1, 64L * 1024 * 1024);
        long sizeAfter = RequireInt64(value, "size_bytes_after", 1, 64L * 1024 * 1024);
        DateTimeOffset writeBefore = RequireUtcTimestamp(value, "last_write_utc_before");
        DateTimeOffset writeMidpoint = RequireUtcTimestamp(value, "last_write_utc_midpoint");
        DateTimeOffset writeAfter = RequireUtcTimestamp(value, "last_write_utc_after");
        DateTime sampleBefore = RequireLocalTimestamp(value, "last_sample_local_before");
        DateTime sampleMidpoint = RequireLocalTimestamp(value, "last_sample_local_midpoint");
        DateTime sampleAfter = RequireLocalTimestamp(value, "last_sample_local_after");
        if (value.GetProperty("grew_during_capture").ValueKind != JsonValueKind.True ||
            !(sizeBefore < sizeMidpoint && sizeMidpoint < sizeAfter) ||
            !(writeBefore < writeMidpoint && writeMidpoint < writeAfter) ||
            !(sampleBefore < sampleMidpoint && sampleMidpoint < sampleAfter))
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"The {source} reference did not prove strict three-point log growth.");
        }

        return new LogReference(
            fileName,
            prefixSha256,
            sizeBefore,
            sizeMidpoint,
            sizeAfter,
            sampleBefore,
            sampleMidpoint,
            sampleAfter);
    }

    private static void ValidateArtifact(
        string source,
        string actualFileName,
        long actualSize,
        string actualSha256,
        LogReference expected)
    {
        if (!string.Equals(actualFileName, expected.FileName, StringComparison.OrdinalIgnoreCase) ||
            actualSize != expected.SizeBytesAfter ||
            !string.Equals(actualSha256, expected.PrefixSha256, StringComparison.Ordinal))
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"The {source} file does not match the recorded bounded prefix name, size, and SHA-256.");
        }
    }

    internal static int[] EvenlySpacedIndices(int sourceCount, int selectedCount)
    {
        if (sourceCount < selectedCount || selectedCount < 2)
        {
            throw new VoltageStatusCorrelationV2Exception(
                "The ordered resampling bounds are invalid.");
        }

        return Enumerable.Range(0, selectedCount)
            .Select(index => (int)Math.Round(
                (long)index * (sourceCount - 1) / (double)(selectedCount - 1),
                MidpointRounding.AwayFromZero))
            .ToArray();
    }

    private static double RequireFiniteDerivedMetric(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"The derived {name} is non-finite.");
        }

        return value;
    }

    private static DateTime ParseLocalTimestamp(string? value)
    {
        if (value is null || !DateTime.TryParseExact(
                value,
                LocalTimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed))
        {
            throw new VoltageStatusCorrelationV2Exception(
                "A local reference timestamp is invalid.");
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
    }

    private static DateTime RequireLocalTimestamp(JsonElement root, string name) =>
        ParseLocalTimestamp(RequireString(root, name));

    private static DateTimeOffset RequireUtcTimestamp(JsonElement root, string name)
    {
        string value = RequireString(root, name);
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"Observation property '{name}' must be an explicit UTC timestamp.");
        }

        return parsed;
    }

    private static string RequireCanonicalGuid(JsonElement root, string name)
    {
        string value = RequireString(root, name);
        if (!Guid.TryParseExact(value, "D", out Guid parsed) ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"Observation property '{name}' must be a canonical UUID.");
        }

        return value.ToLowerInvariant();
    }

    private static void RequireObjectProperties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new VoltageStatusCorrelationV2Exception("An observation object is invalid.");
        }

        string[] actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length ||
            actual.Except(expected, StringComparer.Ordinal).Any() ||
            expected.Except(actual, StringComparer.Ordinal).Any())
        {
            throw new VoltageStatusCorrelationV2Exception(
                "The voltage observation contains missing or unsupported properties.");
        }
    }

    private static JsonElement RequireObject(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"Observation property '{name}' must be an object.");
        }

        return value;
    }

    private static JsonElement RequireArray(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"Observation property '{name}' must be an array.");
        }

        return value;
    }

    private static string RequireString(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string result)
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"Observation property '{name}' must be a string.");
        }

        return result;
    }

    private static string RequireSha256(JsonElement root, string name)
    {
        string value = RequireString(root, name);
        if (value.Length != 64 || value.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"Observation property '{name}' must be a lowercase SHA-256 digest.");
        }

        return value;
    }

    private static void RequireHex32(JsonElement root, string name) =>
        _ = RequireHex32Value(root.GetProperty(name), name);

    private static string RequireHex32Value(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text ||
            text.Length != 10 || !text.StartsWith("0x", StringComparison.Ordinal) ||
            text.AsSpan(2).IndexOfAnyExcept("0123456789abcdef".AsSpan()) >= 0)
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"Observation property '{name}' must be a lowercase 32-bit hexadecimal word.");
        }

        return text;
    }

    private static int RequireInt32(
        JsonElement root,
        string name,
        int minimum,
        int maximum)
    {
        long value = RequireInt64(root, name, minimum, maximum);
        return checked((int)value);
    }

    private static long RequireInt64(
        JsonElement root,
        string name,
        long minimum,
        long maximum)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result) ||
            result < minimum || result > maximum)
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"Observation property '{name}' is outside its integer bounds.");
        }

        return result;
    }

    private static double RequireFiniteDouble(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) ||
            !double.IsFinite(result))
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"Observation property '{name}' must be a finite number.");
        }

        return result;
    }

    private static void Require(JsonElement root, string name, string expected)
    {
        if (!string.Equals(RequireString(root, name), expected, StringComparison.Ordinal))
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"Observation property '{name}' is not allowlisted.");
        }
    }

    private static void Require(JsonElement root, string name, int expected)
    {
        if (root.GetProperty(name).ValueKind != JsonValueKind.Number ||
            !root.GetProperty(name).TryGetInt32(out int actual) || actual != expected)
        {
            throw new VoltageStatusCorrelationV2Exception(
                $"Observation property '{name}' is not allowlisted.");
        }
    }

    private static string FormatLocal(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private sealed record LogReference(
        string FileName,
        string PrefixSha256,
        long SizeBytesBefore,
        long SizeBytesMidpoint,
        long SizeBytesAfter,
        DateTime LastSampleLocalBefore,
        DateTime LastSampleLocalMidpoint,
        DateTime LastSampleLocalAfter);

    private sealed record Observation(
        string Sha256,
        string CaptureSessionId,
        LogReference GpuzReference,
        LogReference? HwinfoReference,
        IReadOnlyList<long> Microvolts);

    private sealed record SelectedGpuzWindow(
        int SessionIndex,
        IReadOnlyList<GpuzVoltagePoint> Samples,
        double BoundaryDistanceMs);
}
