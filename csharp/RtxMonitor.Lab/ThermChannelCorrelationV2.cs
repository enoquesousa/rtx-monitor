using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace RtxMonitor.Lab;

public sealed record ThermGpuProfileV2(
    string Name,
    string Uuid,
    string DriverVersion,
    string NvmlVersion,
    string PciBusId,
    string PciVendorId,
    string PciDeviceId,
    string PciSubsystemVendorId,
    string PciSubsystemDeviceId,
    string VbiosVersion);

public sealed record ThermLoadedNvapiModuleProofV2(
    string ModuleName,
    string ProofCommand,
    string FileSha256,
    string MappedStart,
    string MappedEndExclusive,
    long MappedSizeBytes);

public sealed record ThermReferenceSelectionV2(
    string ArtifactFileName,
    string ArtifactPrefixSha256,
    long ArtifactPrefixSizeBytes,
    string SelectionMethod,
    int EligibleSessionCount,
    IReadOnlyList<int> IgnoredSessionIndicesWithoutExactChannels,
    IReadOnlyList<int> RejectedSessionIndicesWithInvalidExactChannelData,
    int SelectedSessionIndex,
    string RecordedBeforeTimestampLocal,
    string RecordedMidpointTimestampLocal,
    string RecordedAfterTimestampLocal,
    string SessionFirstTimestampLocal,
    string SessionLastTimestampLocal,
    string WindowFirstTimestampLocal,
    string WindowMidpointTimestampLocal,
    string WindowLastTimestampLocal,
    bool SessionStartedAtOrBeforeRecordedBefore,
    bool SessionEndedAtOrAfterRecordedAfter,
    bool WindowContainsRecordedMidpoint,
    double BoundaryDistanceMs,
    double MidpointDistanceMs,
    int ReferenceSampleCount,
    int ComparisonSampleCount);

public sealed record ThermChannelMappingComparisonV2(
    int ObservedChannelIndex,
    int ObservedWordIndex,
    string ReferenceChannel,
    string ReferenceUnit,
    int SampleCount,
    double MeanAbsoluteErrorCelsius,
    double MaximumAbsoluteErrorCelsius,
    string Status);

public sealed record ThermChannelComparisonV2(
    string ComparisonKind,
    double CombinedMeanAbsoluteErrorCelsius,
    double CombinedMaximumAbsoluteErrorCelsius,
    IReadOnlyList<ThermChannelMappingComparisonV2> Mappings);

public sealed record ThermChannelCorrelationReportV2(
    int SchemaVersion,
    string SourceKind,
    string CaptureSessionId,
    string ProfileName,
    ThermGpuProfileV2 Gpu,
    string ObservationSha256,
    string IdentityProbeSha256,
    string GpuzSha256,
    string DebuggerSha256,
    string NvapiModuleSha256,
    ThermLoadedNvapiModuleProofV2 LoadedNvapiModule,
    string InterfaceId,
    string FunctionRva,
    string CallerModuleName,
    string CallerRva,
    string StructureVersion,
    int FixedPointFractionalBits,
    string AlignmentMethod,
    double ToleranceCelsius,
    double MinimumDirectAdvantageCelsius,
    ThermReferenceSelectionV2 Selection,
    ThermChannelComparisonV2 DirectComparison,
    ThermChannelComparisonV2 InvertedComparison,
    string MappingStatus,
    IReadOnlyList<string> Warnings);

public sealed class ThermChannelCorrelationV2Exception : Exception
{
    public ThermChannelCorrelationV2Exception(string message) : base(message) { }

    public ThermChannelCorrelationV2Exception(string message, Exception innerException)
        : base(message, innerException) { }
}

public static class ThermChannelCorrelationV2
{
    public const int SchemaVersion = 2;
    public const string SourceKind = "nvapi_therm_channel_reference_correlation";
    public const string ProfileName =
        "gpuz-2.70.0-nvapi-610.88-therm-channel-status-v2";
    public const string AlignmentMethod = "bounded_session_order_linear_resampling";
    public const string SelectionMethod = "recorded_bounds_then_midpoint_distance";
    public const double RoundingToleranceCelsius = 0.051;
    public const double MinimumDirectAdvantageCelsius = 1.0;

    private const long MaximumObservationSizeBytes = 16L * 1024 * 1024;
    private const string ExpectedObservationSource = "nvapi_therm_channel_v2_observation";
    private const string ExpectedGpuName = "NVIDIA GeForce RTX 3060";
    private const string ExpectedGpuUuid = "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd";
    private const string ExpectedDriverVersion = "610.88";
    private const string ExpectedNvmlVersion = "13.610.88";
    private const string ExpectedPciBusId = "00000000:01:00.0";
    private const string ExpectedVbiosVersion = "94.06.25.00.fc";
    private const string ExpectedGpuzSha256 =
        "6cb0ef29682452de81a9576808881685161411a1fad00938ba04131159979c29";
    private const string ExpectedNvapiSha256 =
        "fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf";
    private const string ExpectedCandidateInventorySha256 =
        "3aaada9b367dacca7cf74511bae8532bd79b7f8bd06b9bb609056f3d9da1f1d7";
    private const string ExpectedPriorObservationSha256 =
        "c7a63df5e6a30bccbba5ad8c1a62a9251c40d512cd74060e69e043cfc54f77b3";
    private const string ExpectedInterfaceId = "0x65fe3aad";
    private const string ExpectedFunctionRva = "0x001ad310";
    private const string ExpectedCallerModuleName = "GPU-Z.exe";
    private const string ExpectedCallerRva = "0x002225b5";
    private const string ExpectedStructureVersion = "0x000200a8";
    private const int ExpectedStructureSizeBytes = 168;
    private const int ExpectedFractionalBits = 8;
    private const int ExpectedBufferEbpDisplacementBytes = -172;
    private const double MinimumTemperatureCelsius = -100.0;
    private const double MaximumTemperatureCelsius = 300.0;

    private static readonly string[] LocalTimestampFormats =
        ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFF"];

    public static ThermChannelCorrelationReportV2 AnalyzeFiles(
        string observationPath,
        string gpuzLogPath)
    {
        Observation observation = ReadObservation(observationPath);
        GpuzThermalPrefixAnalysis gpuz;
        try
        {
            gpuz = GpuzThermalSessionLog.AnalyzeFilePrefix(
                gpuzLogPath,
                observation.GpuzReference.SizeBytesAfter);
        }
        catch (GpuzThermalPrefixException error)
        {
            throw new ThermChannelCorrelationV2Exception(
                "The bounded GPU-Z thermal reference prefix is invalid.",
                error);
        }

        ValidateArtifact(gpuz.Artifact, observation.GpuzReference);
        ValidateSealedArtifactLocation(
            observationPath,
            gpuzLogPath,
            observation.GpuzReference.SealedRelativePath);
        SelectedWindow selected = SelectWindow(gpuz, observation.GpuzReference);
        int comparisonCount = Math.Min(
            observation.Channel0.Count,
            selected.Samples.Count);
        if (comparisonCount < 3)
        {
            throw new ThermChannelCorrelationV2Exception(
                "The selected thermal/reference window must provide at least three comparison points.");
        }

        int[] observedIndices = EvenlySpacedIndices(
            observation.Channel0.Count,
            comparisonCount);
        int[] referenceIndices = EvenlySpacedIndices(
            selected.Samples.Count,
            comparisonCount);
        double[] channel0 = observedIndices
            .Select(index => observation.Channel0[index])
            .ToArray();
        double[] channel1 = observedIndices
            .Select(index => observation.Channel1[index])
            .ToArray();
        double[] gpuTemperature = referenceIndices
            .Select(index => selected.Samples[index].GpuTemperatureCelsius)
            .ToArray();
        double[] hotSpot = referenceIndices
            .Select(index => selected.Samples[index].HotSpotCelsius)
            .ToArray();

        ThermChannelComparisonV2 direct = CreateComparison(
            "direct",
            CreateMapping(0, 10, "GPU Temperature", channel0, gpuTemperature),
            CreateMapping(1, 11, "Hot Spot", channel1, hotSpot));
        ThermChannelComparisonV2 inverted = CreateComparison(
            "inverted",
            CreateMapping(0, 10, "Hot Spot", channel0, hotSpot),
            CreateMapping(1, 11, "GPU Temperature", channel1, gpuTemperature));
        bool directMatches = direct.Mappings.All(mapping =>
            mapping.Status == "matched_rounding_tolerance");
        bool directWins = inverted.CombinedMeanAbsoluteErrorCelsius >=
            direct.CombinedMeanAbsoluteErrorCelsius + MinimumDirectAdvantageCelsius;
        string mappingStatus = directMatches && directWins
            ? "matched_external_reference"
            : "ambiguous_or_outside_tolerance";

        var selection = new ThermReferenceSelectionV2(
            gpuz.Artifact.OriginalFileName,
            gpuz.Artifact.Sha256,
            gpuz.Artifact.SizeBytes,
            SelectionMethod,
            selected.EligibleSessionCount,
            gpuz.IgnoredSessionIndicesWithoutExactChannels,
            gpuz.RejectedSessionIndicesWithInvalidExactChannelData,
            selected.SessionIndex,
            FormatLocal(observation.GpuzReference.LastSampleLocalBefore),
            FormatLocal(observation.GpuzReference.LastSampleLocalMidpoint),
            FormatLocal(observation.GpuzReference.LastSampleLocalAfter),
            FormatLocal(selected.SessionFirstTimestamp),
            FormatLocal(selected.SessionLastTimestamp),
            FormatLocal(selected.Samples[0].Timestamp),
            FormatLocal(selected.WindowMidpointTimestamp),
            FormatLocal(selected.Samples[^1].Timestamp),
            SessionStartedAtOrBeforeRecordedBefore: true,
            SessionEndedAtOrAfterRecordedAfter: true,
            WindowContainsRecordedMidpoint: true,
            selected.BoundaryDistanceMs,
            selected.MidpointDistanceMs,
            selected.Samples.Count,
            comparisonCount);
        string[] warnings =
        [
            "The GPU-Z session was selected only by the recorded before/midpoint/after bounds; temperature error was not used for selection.",
            "Sessions without the exact 'GPU Temperature [°C]' and 'Hot Spot [°C]' pair were isolated and ignored rather than merged.",
            "Sessions containing an invalid value in either exact thermal channel were rejected as a whole and reported by index.",
            "Equal second-resolution GPU-Z timestamps are preserved in source order; only backward timestamps make a session ineligible.",
            "Direct and inverted mappings are both reported; the result remains limited to the exact GPU, VBIOS, driver, executable, module, debugger, call site, and structure profile.",
        ];

        return new ThermChannelCorrelationReportV2(
            SchemaVersion,
            SourceKind,
            observation.CaptureSessionId,
            ProfileName,
            observation.Profile.Gpu,
            observation.Sha256,
            observation.Profile.IdentityProbeSha256,
            ExpectedGpuzSha256,
            observation.Profile.DebuggerSha256,
            ExpectedNvapiSha256,
            observation.Profile.LoadedNvapiModule,
            ExpectedInterfaceId,
            ExpectedFunctionRva,
            ExpectedCallerModuleName,
            ExpectedCallerRva,
            ExpectedStructureVersion,
            ExpectedFractionalBits,
            AlignmentMethod,
            RoundingToleranceCelsius,
            MinimumDirectAdvantageCelsius,
            selection,
            direct,
            inverted,
            mappingStatus,
            warnings);
    }

    private static ThermChannelMappingComparisonV2 CreateMapping(
        int channelIndex,
        int wordIndex,
        string referenceChannel,
        IReadOnlyList<double> observed,
        IReadOnlyList<double> reference)
    {
        ErrorMetrics errors = CalculateErrors(observed, reference);
        return new ThermChannelMappingComparisonV2(
            channelIndex,
            wordIndex,
            referenceChannel,
            "°C",
            observed.Count,
            errors.MeanAbsoluteError,
            errors.MaximumAbsoluteError,
            errors.MaximumAbsoluteError <= RoundingToleranceCelsius
                ? "matched_rounding_tolerance"
                : "outside_tolerance");
    }

    private static ThermChannelComparisonV2 CreateComparison(
        string kind,
        ThermChannelMappingComparisonV2 first,
        ThermChannelMappingComparisonV2 second) =>
        new(
            kind,
            (first.MeanAbsoluteErrorCelsius + second.MeanAbsoluteErrorCelsius) / 2,
            Math.Max(
                first.MaximumAbsoluteErrorCelsius,
                second.MaximumAbsoluteErrorCelsius),
            [first, second]);

    private static SelectedWindow SelectWindow(
        GpuzThermalPrefixAnalysis gpuz,
        LogReference reference)
    {
        var candidates = new List<SelectedWindow>();
        foreach (GpuzThermalSession session in gpuz.Sessions)
        {
            if (session.Samples.Count < 3 || !IsChronologicallyOrdered(session.Samples))
            {
                continue;
            }

            DateTime sessionFirst = session.Samples[0].Timestamp;
            DateTime sessionLast = session.Samples[^1].Timestamp;
            if (sessionFirst > reference.LastSampleLocalBefore ||
                sessionLast < reference.LastSampleLocalAfter)
            {
                continue;
            }

            GpuzThermalPoint[] bounded = session.Samples
                .Where(sample =>
                    sample.Timestamp >= reference.LastSampleLocalBefore &&
                    sample.Timestamp <= reference.LastSampleLocalAfter)
                .ToArray();
            if (bounded.Length < 3 ||
                bounded[0].Timestamp > reference.LastSampleLocalMidpoint ||
                bounded[^1].Timestamp < reference.LastSampleLocalMidpoint)
            {
                continue;
            }

            GpuzThermalPoint midpoint = bounded.MinBy(sample =>
                Math.Abs((sample.Timestamp - reference.LastSampleLocalMidpoint).Ticks))!;
            double boundaryDistance =
                Math.Abs((bounded[0].Timestamp - reference.LastSampleLocalBefore)
                    .TotalMilliseconds) +
                Math.Abs((bounded[^1].Timestamp - reference.LastSampleLocalAfter)
                    .TotalMilliseconds);
            double midpointDistance = Math.Abs(
                (midpoint.Timestamp - reference.LastSampleLocalMidpoint).TotalMilliseconds);
            candidates.Add(new SelectedWindow(
                session.SessionIndex,
                sessionFirst,
                sessionLast,
                bounded,
                midpoint.Timestamp,
                boundaryDistance,
                midpointDistance,
                EligibleSessionCount: 0));
        }

        if (candidates.Count == 0)
        {
            throw new ThermChannelCorrelationV2Exception(
                "No exact-channel GPU-Z session covers all recorded before/midpoint/after bounds with at least three ordered samples.");
        }

        SelectedWindow selected = candidates
            .OrderBy(candidate => candidate.BoundaryDistanceMs)
            .ThenBy(candidate => candidate.MidpointDistanceMs)
            .ThenBy(candidate => candidate.SessionIndex)
            .First();
        int equallyCompatible = candidates.Count(candidate =>
            Math.Abs(candidate.BoundaryDistanceMs - selected.BoundaryDistanceMs) < 0.0001 &&
            Math.Abs(candidate.MidpointDistanceMs - selected.MidpointDistanceMs) < 0.0001);
        if (equallyCompatible != 1)
        {
            throw new ThermChannelCorrelationV2Exception(
                "Multiple GPU-Z sessions are equally compatible with the recorded bounds and midpoint.");
        }

        return selected with { EligibleSessionCount = candidates.Count };
    }

    private static bool IsChronologicallyOrdered(IReadOnlyList<GpuzThermalPoint> samples) =>
        Enumerable.Range(1, samples.Count - 1).All(index =>
            samples[index].Timestamp >= samples[index - 1].Timestamp);

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
            throw new ThermChannelCorrelationV2Exception(
                "The thermal observation path is invalid.",
                error);
        }

        FileAttributes attributes = File.GetAttributes(resolved);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new ThermChannelCorrelationV2Exception(
                "The thermal observation must be a regular local file and cannot be a reparse point.");
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
                throw new ThermChannelCorrelationV2Exception(
                    "The thermal observation size is outside the analysis limit.");
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length)
            {
                throw new ThermChannelCorrelationV2Exception(
                    "The thermal observation changed while it was being read.");
            }
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
            JsonElement root = document.RootElement;
            RequireObjectProperties(
                root,
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
                throw new ThermChannelCorrelationV2Exception(
                    "The capture UTC bounds are inconsistent with duration_seconds.");
            }

            ProfileEvidence profile = ValidateProfile(RequireObject(root, "profile"));
            JsonElement references = RequireObject(root, "references");
            RequireObjectProperties(references, "gpuz");
            LogReference gpuzReference = ReadLogReference(
                RequireObject(references, "gpuz"));
            if ((gpuzReference.LastSampleLocalAfter -
                    gpuzReference.LastSampleLocalBefore).TotalSeconds > duration + 10)
            {
                throw new ThermChannelCorrelationV2Exception(
                    "The GPU-Z reference interval is inconsistent with duration_seconds.");
            }

            JsonElement samples = RequireArray(root, "samples");
            int callCount = RequireInt32(root, "call_count", 6, 100_000);
            if (samples.GetArrayLength() != callCount)
            {
                throw new ThermChannelCorrelationV2Exception(
                    "call_count must equal the number of thermal samples.");
            }

            var channel0 = new List<double>();
            var channel1 = new List<double>();
            int expectedSequence = 0;
            foreach (JsonElement sample in samples.EnumerateArray())
            {
                expectedSequence++;
                RequireObjectProperties(
                    sample,
                    "sequence", "thread_id", "caller_rva", "channel_index",
                    "return_status", "structure_version", "channel_mask",
                    "raw_words", "selected_word_index", "selected_raw_fixed_8",
                    "selected_celsius");
                Require(sample, "sequence", expectedSequence);
                RequireHex32(sample, "thread_id");
                Require(sample, "caller_rva", ExpectedCallerRva);
                int channelIndex = RequireInt32(sample, "channel_index", 0, 1);
                Require(sample, "return_status", "0x00000000");
                Require(sample, "structure_version", ExpectedStructureVersion);
                string expectedMask = channelIndex == 0 ? "0x00000001" : "0x00000002";
                Require(sample, "channel_mask", expectedMask);
                JsonElement rawWords = RequireArray(sample, "raw_words");
                if (rawWords.GetArrayLength() != 42)
                {
                    throw new ThermChannelCorrelationV2Exception(
                        "Every thermal sample must contain exactly 42 DWORDs.");
                }

                string[] words = rawWords.EnumerateArray()
                    .Select((word, index) =>
                        RequireHex32Value(word, $"raw_words[{index}]"))
                    .ToArray();
                if (words[0] != ExpectedStructureVersion || words[1] != expectedMask)
                {
                    throw new ThermChannelCorrelationV2Exception(
                        "The captured thermal structure header or channel mask is not allowlisted.");
                }

                int selectedWordIndex = RequireInt32(
                    sample,
                    "selected_word_index",
                    10,
                    11);
                if (selectedWordIndex != 10 + channelIndex)
                {
                    throw new ThermChannelCorrelationV2Exception(
                        "The selected thermal word does not match the channel index.");
                }

                uint selectedWord = uint.Parse(
                    words[selectedWordIndex].AsSpan(2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture);
                int raw = RequireInt32(
                    sample,
                    "selected_raw_fixed_8",
                    -25_600,
                    76_800);
                if (unchecked((int)selectedWord) != raw)
                {
                    throw new ThermChannelCorrelationV2Exception(
                        "The selected raw thermal word does not match raw_words.");
                }

                double celsius = RequireFiniteDouble(sample, "selected_celsius");
                if (celsius < MinimumTemperatureCelsius ||
                    celsius > MaximumTemperatureCelsius ||
                    Math.Abs(celsius - raw / 256.0) > 1e-12)
                {
                    throw new ThermChannelCorrelationV2Exception(
                        "selected_celsius does not exactly represent the fixed-point word.");
                }

                (channelIndex == 0 ? channel0 : channel1).Add(celsius);
            }

            if (channel0.Count < 3 || channel0.Count != channel1.Count)
            {
                throw new ThermChannelCorrelationV2Exception(
                    "The thermal observation must contain at least three balanced channel 0/1 samples.");
            }

            string warning = RequireString(root, "warning");
            if (warning.Length is < 1 or > 2_000)
            {
                throw new ThermChannelCorrelationV2Exception(
                    "The observation warning is missing or too long.");
            }

            return new Observation(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                sessionId,
                profile,
                gpuzReference,
                channel0,
                channel1);
        }
        catch (ThermChannelCorrelationV2Exception)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException)
        {
            throw new ThermChannelCorrelationV2Exception(
                "The thermal observation v2 JSON is malformed or incomplete.",
                error);
        }
    }

    private static ProfileEvidence ValidateProfile(JsonElement profile)
    {
        RequireObjectProperties(
            profile,
            "profile_name", "gpu", "identity_probe_sha256", "gpuz_sha256",
            "debugger_sha256", "debugger_file_version",
            "candidate_inventory_sha256", "prior_observation_sha256",
            "nvapi_module_sha256", "loaded_nvapi_module", "interface_id", "function_rva",
            "caller_module_name", "caller_rva", "buffer_ebp_displacement_bytes",
            "structure_version", "structure_size_bytes",
            "fixed_point_fractional_bits", "value_word_indices");
        Require(profile, "profile_name", ProfileName);
        JsonElement gpu = RequireObject(profile, "gpu");
        RequireObjectProperties(
            gpu,
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
        string identityProbeSha256 = RequireSha256(profile, "identity_probe_sha256");
        Require(profile, "gpuz_sha256", ExpectedGpuzSha256);
        string debuggerSha256 = RequireSha256(profile, "debugger_sha256");
        string debuggerVersion = RequireString(profile, "debugger_file_version");
        if (debuggerVersion.Length is < 1 or > 128)
        {
            throw new ThermChannelCorrelationV2Exception(
                "debugger_file_version is invalid.");
        }

        Require(profile, "candidate_inventory_sha256", ExpectedCandidateInventorySha256);
        Require(profile, "prior_observation_sha256", ExpectedPriorObservationSha256);
        Require(profile, "nvapi_module_sha256", ExpectedNvapiSha256);
        ThermLoadedNvapiModuleProofV2 loadedNvapiModule = ValidateLoadedNvapiModule(
            RequireObject(profile, "loaded_nvapi_module"));
        Require(profile, "interface_id", ExpectedInterfaceId);
        Require(profile, "function_rva", ExpectedFunctionRva);
        Require(profile, "caller_module_name", ExpectedCallerModuleName);
        Require(profile, "caller_rva", ExpectedCallerRva);
        Require(
            profile,
            "buffer_ebp_displacement_bytes",
            ExpectedBufferEbpDisplacementBytes);
        Require(profile, "structure_version", ExpectedStructureVersion);
        Require(profile, "structure_size_bytes", ExpectedStructureSizeBytes);
        Require(profile, "fixed_point_fractional_bits", ExpectedFractionalBits);
        JsonElement indices = RequireArray(profile, "value_word_indices");
        if (indices.GetArrayLength() != 2 ||
            indices[0].GetInt32() != 10 ||
            indices[1].GetInt32() != 11)
        {
            throw new ThermChannelCorrelationV2Exception(
                "The thermal value-word indices are not allowlisted.");
        }

        return new ProfileEvidence(
            new ThermGpuProfileV2(
                ExpectedGpuName,
                ExpectedGpuUuid,
                ExpectedDriverVersion,
                ExpectedNvmlVersion,
                ExpectedPciBusId,
                "0x10de",
                "0x2504",
                "0x10de",
                "0x1536",
                ExpectedVbiosVersion),
            identityProbeSha256,
            debuggerSha256,
            loadedNvapiModule);
    }

    private static ThermLoadedNvapiModuleProofV2 ValidateLoadedNvapiModule(
        JsonElement module)
    {
        RequireObjectProperties(
            module,
            "module_name", "proof_command", "file_sha256", "mapped_start",
            "mapped_end_exclusive", "mapped_size_bytes");
        Require(module, "module_name", "nvapi_impl.dll");
        Require(module, "proof_command", "lmv m nvapi_impl");
        Require(module, "file_sha256", ExpectedNvapiSha256);
        string mappedStartText = RequireHex32Value(
            module.GetProperty("mapped_start"),
            "loaded_nvapi_module.mapped_start");
        string mappedEndText = RequireHex32Value(
            module.GetProperty("mapped_end_exclusive"),
            "loaded_nvapi_module.mapped_end_exclusive");
        uint mappedStart = uint.Parse(
            mappedStartText.AsSpan(2),
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture);
        uint mappedEnd = uint.Parse(
            mappedEndText.AsSpan(2),
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture);
        long mappedSize = RequireInt64(
            module,
            "mapped_size_bytes",
            1,
            GpuzSensorLog.MaximumInputSizeBytes);
        if (mappedStart >= mappedEnd || mappedSize != (long)mappedEnd - mappedStart)
        {
            throw new ThermChannelCorrelationV2Exception(
                "The loaded nvapi_impl.dll mapped range is internally inconsistent.");
        }

        uint functionRva = uint.Parse(
            ExpectedFunctionRva.AsSpan(2),
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture);
        ulong functionAddress = (ulong)mappedStart + functionRva;
        if (functionAddress < mappedStart || functionAddress >= mappedEnd)
        {
            throw new ThermChannelCorrelationV2Exception(
                "The allowlisted thermal function RVA is outside the loaded nvapi_impl.dll range.");
        }

        return new ThermLoadedNvapiModuleProofV2(
            "nvapi_impl.dll",
            "lmv m nvapi_impl",
            ExpectedNvapiSha256,
            mappedStartText,
            mappedEndText,
            mappedSize);
    }

    private static LogReference ReadLogReference(JsonElement value)
    {
        RequireObjectProperties(
            value,
            "file_name", "sealed_relative_path", "prefix_sha256", "prefix_size_bytes",
            "size_bytes_before",
            "size_bytes_midpoint", "size_bytes_after", "last_write_utc_before",
            "last_write_utc_midpoint", "last_write_utc_after",
            "last_sample_local_before", "last_sample_local_midpoint",
            "last_sample_local_after", "grew_during_capture");
        string fileName = RequireString(value, "file_name");
        if (fileName != Path.GetFileName(fileName) || fileName.Length is < 1 or > 260 ||
            fileName.Any(char.IsControl))
        {
            throw new ThermChannelCorrelationV2Exception(
                "The GPU-Z reference file_name is invalid.");
        }

        string sealedRelativePath = RequireString(value, "sealed_relative_path");
        if (!string.Equals(sealedRelativePath, fileName, StringComparison.Ordinal) ||
            sealedRelativePath != Path.GetFileName(sealedRelativePath) ||
            sealedRelativePath.Any(char.IsControl))
        {
            throw new ThermChannelCorrelationV2Exception(
                "The sealed GPU-Z prefix must use the exact source basename inside the capture directory.");
        }

        string prefixSha256 = RequireSha256(value, "prefix_sha256");
        long prefixSize = RequireInt64(
            value,
            "prefix_size_bytes",
            1,
            GpuzSensorLog.MaximumInputSizeBytes);
        long sizeBefore = RequireInt64(
            value,
            "size_bytes_before",
            1,
            GpuzSensorLog.MaximumInputSizeBytes);
        long sizeMidpoint = RequireInt64(
            value,
            "size_bytes_midpoint",
            1,
            GpuzSensorLog.MaximumInputSizeBytes);
        long sizeAfter = RequireInt64(
            value,
            "size_bytes_after",
            1,
            GpuzSensorLog.MaximumInputSizeBytes);
        DateTimeOffset writeBefore = RequireUtcTimestamp(value, "last_write_utc_before");
        DateTimeOffset writeMidpoint = RequireUtcTimestamp(value, "last_write_utc_midpoint");
        DateTimeOffset writeAfter = RequireUtcTimestamp(value, "last_write_utc_after");
        DateTime sampleBefore = RequireLocalTimestamp(value, "last_sample_local_before");
        DateTime sampleMidpoint = RequireLocalTimestamp(value, "last_sample_local_midpoint");
        DateTime sampleAfter = RequireLocalTimestamp(value, "last_sample_local_after");
        if (value.GetProperty("grew_during_capture").ValueKind != JsonValueKind.True ||
            !(sizeBefore < sizeMidpoint && sizeMidpoint < sizeAfter) ||
            !(writeBefore < writeMidpoint && writeMidpoint < writeAfter) ||
            !(sampleBefore < sampleMidpoint && sampleMidpoint < sampleAfter) ||
            prefixSize != sizeAfter)
        {
            throw new ThermChannelCorrelationV2Exception(
                "The GPU-Z reference did not prove strict three-point log growth.");
        }

        return new LogReference(
            fileName,
            sealedRelativePath,
            prefixSha256,
            sizeBefore,
            sizeMidpoint,
            sizeAfter,
            sampleBefore,
            sampleMidpoint,
            sampleAfter);
    }

    private static void ValidateArtifact(
        GpuzThermalPrefixArtifact actual,
        LogReference expected)
    {
        if (!string.Equals(
                actual.OriginalFileName,
                expected.FileName,
                StringComparison.OrdinalIgnoreCase) ||
            actual.SizeBytes != expected.SizeBytesAfter ||
            !string.Equals(actual.Sha256, expected.PrefixSha256, StringComparison.Ordinal))
        {
            throw new ThermChannelCorrelationV2Exception(
                "The GPU-Z file does not match the recorded LF-complete prefix name, size, and SHA-256.");
        }
    }

    private static void ValidateSealedArtifactLocation(
        string observationPath,
        string artifactPath,
        string sealedRelativePath)
    {
        string resolvedObservation = Path.GetFullPath(observationPath);
        string resolvedArtifact = Path.GetFullPath(artifactPath);
        if (!string.Equals(
                Path.GetDirectoryName(resolvedObservation),
                Path.GetDirectoryName(resolvedArtifact),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFileName(resolvedArtifact),
                sealedRelativePath,
                StringComparison.Ordinal))
        {
            throw new ThermChannelCorrelationV2Exception(
                "The GPU-Z reference must be the named sealed prefix beside the observation v2 file.");
        }
    }

    private static ErrorMetrics CalculateErrors(
        IReadOnlyList<double> observed,
        IReadOnlyList<double> reference)
    {
        if (observed.Count != reference.Count || observed.Count < 3)
        {
            throw new ThermChannelCorrelationV2Exception(
                "Thermal and reference windows have incompatible sample counts.");
        }

        double sum = 0;
        double maximum = 0;
        for (int index = 0; index < observed.Count; index++)
        {
            double error = Math.Abs(observed[index] - reference[index]);
            sum += error;
            maximum = Math.Max(maximum, error);
        }

        return new ErrorMetrics(sum / observed.Count, maximum);
    }

    internal static int[] EvenlySpacedIndices(int sourceCount, int selectedCount)
    {
        if (sourceCount < selectedCount || selectedCount < 3)
        {
            throw new ThermChannelCorrelationV2Exception(
                "The ordered thermal resampling bounds are invalid.");
        }

        return Enumerable.Range(0, selectedCount)
            .Select(index => (int)Math.Round(
                (long)index * (sourceCount - 1) / (double)(selectedCount - 1),
                MidpointRounding.AwayFromZero))
            .ToArray();
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
            throw new ThermChannelCorrelationV2Exception(
                "A local GPU-Z reference timestamp is invalid.");
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
            throw new ThermChannelCorrelationV2Exception(
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
            throw new ThermChannelCorrelationV2Exception(
                $"Observation property '{name}' must be a canonical UUID.");
        }

        return value.ToLowerInvariant();
    }

    private static void RequireObjectProperties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ThermChannelCorrelationV2Exception(
                "A thermal observation object is invalid.");
        }

        string[] actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length ||
            actual.Except(expected, StringComparer.Ordinal).Any() ||
            expected.Except(actual, StringComparer.Ordinal).Any())
        {
            throw new ThermChannelCorrelationV2Exception(
                "The thermal observation contains missing or unsupported properties.");
        }
    }

    private static JsonElement RequireObject(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ThermChannelCorrelationV2Exception(
                $"Observation property '{name}' must be an object.");
        }

        return value;
    }

    private static JsonElement RequireArray(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new ThermChannelCorrelationV2Exception(
                $"Observation property '{name}' must be an array.");
        }

        return value;
    }

    private static string RequireString(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string result)
        {
            throw new ThermChannelCorrelationV2Exception(
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
            throw new ThermChannelCorrelationV2Exception(
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
            throw new ThermChannelCorrelationV2Exception(
                $"Observation property '{name}' must be a lowercase 32-bit hexadecimal word.");
        }

        return text;
    }

    private static int RequireInt32(
        JsonElement root,
        string name,
        int minimum,
        int maximum) =>
        checked((int)RequireInt64(root, name, minimum, maximum));

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
            throw new ThermChannelCorrelationV2Exception(
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
            throw new ThermChannelCorrelationV2Exception(
                $"Observation property '{name}' must be a finite number.");
        }

        return result;
    }

    private static void Require(JsonElement root, string name, string expected)
    {
        if (!string.Equals(RequireString(root, name), expected, StringComparison.Ordinal))
        {
            throw new ThermChannelCorrelationV2Exception(
                $"Observation property '{name}' is not allowlisted.");
        }
    }

    private static void Require(JsonElement root, string name, int expected)
    {
        if (root.GetProperty(name).ValueKind != JsonValueKind.Number ||
            !root.GetProperty(name).TryGetInt32(out int actual) || actual != expected)
        {
            throw new ThermChannelCorrelationV2Exception(
                $"Observation property '{name}' is not allowlisted.");
        }
    }

    private static string FormatLocal(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private sealed record ProfileEvidence(
        ThermGpuProfileV2 Gpu,
        string IdentityProbeSha256,
        string DebuggerSha256,
        ThermLoadedNvapiModuleProofV2 LoadedNvapiModule);

    private sealed record LogReference(
        string FileName,
        string SealedRelativePath,
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
        ProfileEvidence Profile,
        LogReference GpuzReference,
        IReadOnlyList<double> Channel0,
        IReadOnlyList<double> Channel1);

    private sealed record ErrorMetrics(
        double MeanAbsoluteError,
        double MaximumAbsoluteError);

    private sealed record SelectedWindow(
        int SessionIndex,
        DateTime SessionFirstTimestamp,
        DateTime SessionLastTimestamp,
        IReadOnlyList<GpuzThermalPoint> Samples,
        DateTime WindowMidpointTimestamp,
        double BoundaryDistanceMs,
        double MidpointDistanceMs,
        int EligibleSessionCount);
}

public static class ThermChannelCorrelationV2Json
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static string Serialize(ThermChannelCorrelationReportV2 report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, SerializerOptions);
    }
}
