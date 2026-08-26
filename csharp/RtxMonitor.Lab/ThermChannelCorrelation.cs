using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace RtxMonitor.Lab;

public sealed record ThermChannelReferenceMapping(
    int ChannelIndex,
    string SemanticChannel,
    string ReferenceChannel,
    int SampleCount,
    double MeanAbsoluteErrorCelsius,
    double MaximumAbsoluteErrorCelsius,
    string Status);

public sealed record ThermChannelCorrelationReport(
    int SchemaVersion,
    string SourceKind,
    string ThermObservationSha256,
    string GpuzLogPrefixSha256,
    long GpuzLogPrefixSizeBytes,
    string GpuzSha256,
    string NvapiModuleSha256,
    string InterfaceId,
    string FunctionRva,
    string StructureVersion,
    int SelectedSessionIndex,
    string WindowFirstTimestampLocal,
    string WindowLastTimestampLocal,
    double ToleranceCelsius,
    double CombinedMeanAbsoluteErrorCelsius,
    double AlternativeCombinedMeanAbsoluteErrorCelsius,
    string MappingStatus,
    IReadOnlyList<ThermChannelReferenceMapping> Mappings,
    IReadOnlyList<string> Warnings);

public sealed class ThermChannelCorrelationException : Exception
{
    public ThermChannelCorrelationException(string message)
        : base(message)
    {
    }

    public ThermChannelCorrelationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class ThermChannelCorrelation
{
    public const int SchemaVersion = 1;
    public const string SourceKind = "nvapi_therm_channel_reference_correlation";
    public const double RoundingToleranceCelsius = 0.051;
    private const long MaximumObservationSizeBytes = 16L * 1024 * 1024;
    private const string ExpectedObservationSource = "nvapi_therm_channel_v2_observation";
    private const string ExpectedGpuzSha256 =
        "6cb0ef29682452de81a9576808881685161411a1fad00938ba04131159979c29";
    private const string ExpectedNvapiSha256 =
        "fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf";
    private const string ExpectedInterfaceId = "0x65fe3aad";
    private const string ExpectedFunctionRva = "0x001ad310";
    private const string ExpectedStructureVersion = "0x000200a8";

    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFF",
    ];

    public static ThermChannelCorrelationReport AnalyzeFiles(
        string thermObservationPath,
        string gpuzLogPath)
    {
        ThermObservation observation = ReadObservation(thermObservationPath);
        GpuzLogAnalysis log = GpuzSensorLog.AnalyzeFilePrefix(
            gpuzLogPath,
            observation.LogPrefixSizeBytes);
        return Analyze(observation, log);
    }

    private static ThermChannelCorrelationReport Analyze(
        ThermObservation observation,
        GpuzLogAnalysis log)
    {
        GpuzChannelAnalysis gpuTemperature = RequireReferenceChannel(
            log,
            "GPU Temperature");
        GpuzChannelAnalysis hotSpot = RequireReferenceChannel(log, "Hot Spot");
        DateTime lowerBound = observation.LogBefore.AddSeconds(-2);
        DateTime upperBound = observation.LogAfter.AddSeconds(2);
        WindowMetrics? best = null;

        for (int sessionIndex = 0; sessionIndex < log.SessionCount; sessionIndex++)
        {
            GpuzLogSample[] session = log.Samples
                .Where(sample => sample.SessionIndex == sessionIndex)
                .ToArray();
            int sampleCount = observation.Channel0.Count;
            for (int start = 0; start <= session.Length - sampleCount; start++)
            {
                GpuzLogSample[] window = session.Skip(start).Take(sampleCount).ToArray();
                DateTime first = ParseTimestamp(window[0].TimestampLocal);
                DateTime last = ParseTimestamp(window[^1].TimestampLocal);
                if (first < lowerBound || last > upperBound ||
                    first > observation.LogAfter || last < observation.LogBefore)
                {
                    continue;
                }

                double[] gpuValues = ReadWindow(window, gpuTemperature.Index);
                double[] hotSpotValues = ReadWindow(window, hotSpot.Index);
                ErrorMetrics channel0ToGpu = CalculateErrors(observation.Channel0, gpuValues);
                ErrorMetrics channel1ToHotSpot = CalculateErrors(
                    observation.Channel1,
                    hotSpotValues);
                ErrorMetrics channel0ToHotSpot = CalculateErrors(
                    observation.Channel0,
                    hotSpotValues);
                ErrorMetrics channel1ToGpu = CalculateErrors(
                    observation.Channel1,
                    gpuValues);
                double combined =
                    (channel0ToGpu.MeanAbsoluteError +
                        channel1ToHotSpot.MeanAbsoluteError) /
                    2;
                double alternative =
                    (channel0ToHotSpot.MeanAbsoluteError +
                        channel1ToGpu.MeanAbsoluteError) /
                    2;
                var candidate = new WindowMetrics(
                    sessionIndex,
                    window[0].TimestampLocal,
                    window[^1].TimestampLocal,
                    channel0ToGpu,
                    channel1ToHotSpot,
                    combined,
                    alternative);
                if (best is null || candidate.CombinedMeanAbsoluteError <
                    best.CombinedMeanAbsoluteError)
                {
                    best = candidate;
                }
            }
        }

        if (best is null)
        {
            throw new ThermChannelCorrelationException(
                "No GPU-Z sample window overlaps the bounded thermal observation.");
        }

        string channel0Status = best.Channel0ToGpu.MaximumAbsoluteError <=
            RoundingToleranceCelsius
            ? "matched_rounding_tolerance"
            : "outside_tolerance";
        string channel1Status = best.Channel1ToHotSpot.MaximumAbsoluteError <=
            RoundingToleranceCelsius
            ? "matched_rounding_tolerance"
            : "outside_tolerance";
        bool unambiguous = channel0Status == "matched_rounding_tolerance" &&
            channel1Status == "matched_rounding_tolerance" &&
            best.AlternativeCombinedMeanAbsoluteError >=
                best.CombinedMeanAbsoluteError + 1.0;
        string mappingStatus = unambiguous
            ? "matched_external_reference"
            : "ambiguous_or_outside_tolerance";

        ThermChannelReferenceMapping[] mappings =
        [
            new(
                0,
                "gpu_die_temperature",
                gpuTemperature.Name,
                observation.Channel0.Count,
                best.Channel0ToGpu.MeanAbsoluteError,
                best.Channel0ToGpu.MaximumAbsoluteError,
                channel0Status),
            new(
                1,
                "gpu_hotspot_temperature",
                hotSpot.Name,
                observation.Channel1.Count,
                best.Channel1ToHotSpot.MeanAbsoluteError,
                best.Channel1ToHotSpot.MaximumAbsoluteError,
                channel1Status),
        ];
        string[] warnings =
        [
            "The labels are validated against an external GPU-Z reference and exact binary hashes; they are not a vendor-published NVAPI contract.",
            "The result applies to the anchored GPU-Z, NVAPI implementation, driver, board profile, structure version, call site, and fixed-point conversion.",
            "A physical sensor identity beyond GPU die and hotspot still requires independent board-level validation.",
        ];

        return new ThermChannelCorrelationReport(
            SchemaVersion,
            SourceKind,
            observation.Sha256,
            log.Artifact.Sha256,
            log.Artifact.SizeBytes,
            observation.GpuzSha256,
            observation.NvapiModuleSha256,
            ExpectedInterfaceId,
            ExpectedFunctionRva,
            ExpectedStructureVersion,
            best.SessionIndex,
            best.FirstTimestampLocal,
            best.LastTimestampLocal,
            RoundingToleranceCelsius,
            best.CombinedMeanAbsoluteError,
            best.AlternativeCombinedMeanAbsoluteError,
            mappingStatus,
            mappings,
            warnings);
    }

    private static ThermObservation ReadObservation(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(inputPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new ThermChannelCorrelationException(
                "The thermal observation path is invalid.",
                error);
        }

        FileAttributes attributes = File.GetAttributes(resolvedPath);
        if ((attributes & FileAttributes.Directory) != 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ThermChannelCorrelationException(
                "The thermal observation must be a regular local file.");
        }

        using var stream = new FileStream(
            resolvedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length < 1 || stream.Length > MaximumObservationSizeBytes)
        {
            throw new ThermChannelCorrelationException(
                $"The thermal observation must be between 1 and " +
                $"{MaximumObservationSizeBytes} bytes.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.Position != stream.Length)
        {
            throw new ThermChannelCorrelationException(
                "The thermal observation changed while it was being read.");
        }

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
            RequireNumber(root, "schema_version", 1);
            RequireString(root, "source_kind", ExpectedObservationSource);
            string gpuzSha256 = RequireString(root, "gpuz_sha256");
            string nvapiSha256 = RequireString(root, "nvapi_module_sha256");
            RequireString(root, "interface_id", ExpectedInterfaceId);
            RequireString(root, "function_rva", ExpectedFunctionRva);
            RequireString(root, "structure_version", ExpectedStructureVersion);
            RequireNumber(root, "structure_size_bytes", 168);
            RequireNumber(root, "fixed_point_fractional_bits", 8);
            if (gpuzSha256 != ExpectedGpuzSha256 || nvapiSha256 != ExpectedNvapiSha256)
            {
                throw new ThermChannelCorrelationException(
                    "The thermal observation does not match the fixed binary profile.");
            }

            JsonElement reference = root.GetProperty("reference_log");
            long prefixLength = reference.GetProperty("size_bytes_after").GetInt64();
            if (prefixLength < 1 || prefixLength > GpuzSensorLog.MaximumInputSizeBytes)
            {
                throw new ThermChannelCorrelationException(
                    "The captured GPU-Z log prefix length is outside the analysis limit.");
            }

            DateTime before = ParseTimestamp(
                reference.GetProperty("last_sample_local_before").GetString());
            DateTime after = ParseTimestamp(
                reference.GetProperty("last_sample_local_after").GetString());
            if (after < before || (after - before).TotalSeconds > 120)
            {
                throw new ThermChannelCorrelationException(
                    "The captured reference-log interval is invalid.");
            }

            JsonElement samples = root.GetProperty("samples");
            int declaredCount = root.GetProperty("call_count").GetInt32();
            if (samples.ValueKind != JsonValueKind.Array ||
                samples.GetArrayLength() != declaredCount ||
                declaredCount < 2)
            {
                throw new ThermChannelCorrelationException(
                    "The thermal sample count is invalid.");
            }

            var channel0 = new List<double>();
            var channel1 = new List<double>();
            int sequence = 0;
            foreach (JsonElement sample in samples.EnumerateArray())
            {
                sequence++;
                if (sample.GetProperty("sequence").GetInt32() != sequence ||
                    sample.GetProperty("return_status").GetString() != "0x00000000" ||
                    sample.GetProperty("structure_version").GetString() !=
                        ExpectedStructureVersion)
                {
                    throw new ThermChannelCorrelationException(
                        "The thermal sample sequence or success contract is invalid.");
                }

                int channel = sample.GetProperty("channel_index").GetInt32();
                int selectedIndex = sample.GetProperty("selected_word_index").GetInt32();
                int raw = sample.GetProperty("selected_raw_fixed_8").GetInt32();
                double celsius = sample.GetProperty("selected_celsius").GetDouble();
                if (channel is < 0 or > 1 || selectedIndex != 10 + channel ||
                    !double.IsFinite(celsius) || Math.Abs(celsius - raw / 256.0) > 1e-12)
                {
                    throw new ThermChannelCorrelationException(
                        "A thermal sample violates the fixed v2 layout or scale.");
                }

                (channel == 0 ? channel0 : channel1).Add(celsius);
            }

            if (channel0.Count == 0 || channel0.Count != channel1.Count)
            {
                throw new ThermChannelCorrelationException(
                    "The thermal observation must contain balanced channel 0/1 samples.");
            }

            return new ThermObservation(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                gpuzSha256,
                nvapiSha256,
                prefixLength,
                before,
                after,
                channel0,
                channel1);
        }
        catch (ThermChannelCorrelationException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException)
        {
            throw new ThermChannelCorrelationException(
                "The thermal observation JSON is malformed or incomplete.",
                error);
        }
    }

    private static GpuzChannelAnalysis RequireReferenceChannel(
        GpuzLogAnalysis analysis,
        string name)
    {
        GpuzChannelAnalysis? channel = analysis.Channels.SingleOrDefault(
            candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (channel is null || channel.Unit != "°C" ||
            channel.Category != "temperature")
        {
            throw new ThermChannelCorrelationException(
                $"GPU-Z reference channel '{name} [°C]' is missing or invalid.");
        }

        return channel;
    }

    private static double[] ReadWindow(
        IReadOnlyList<GpuzLogSample> samples,
        int channelIndex)
    {
        var result = new double[samples.Count];
        for (int index = 0; index < samples.Count; index++)
        {
            if (!double.TryParse(
                    samples[index].Values[channelIndex],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value) ||
                !double.IsFinite(value))
            {
                throw new ThermChannelCorrelationException(
                    "The selected GPU-Z reference window contains a non-numeric value.");
            }

            result[index] = value;
        }

        return result;
    }

    private static ErrorMetrics CalculateErrors(
        IReadOnlyList<double> observed,
        IReadOnlyList<double> reference)
    {
        if (observed.Count != reference.Count || observed.Count == 0)
        {
            throw new ThermChannelCorrelationException(
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

    private static DateTime ParseTimestamp(string? value)
    {
        if (value is null || !DateTime.TryParseExact(
                value,
                TimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime timestamp))
        {
            throw new ThermChannelCorrelationException(
                "A local GPU-Z timestamp is invalid.");
        }

        return DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified);
    }

    private static string RequireString(
        JsonElement parent,
        string propertyName,
        string? expected = null)
    {
        string? value = parent.GetProperty(propertyName).GetString();
        if (value is null || (expected is not null && value != expected))
        {
            throw new ThermChannelCorrelationException(
                $"Thermal observation property '{propertyName}' is invalid.");
        }

        return value;
    }

    private static void RequireNumber(
        JsonElement parent,
        string propertyName,
        int expected)
    {
        if (parent.GetProperty(propertyName).GetInt32() != expected)
        {
            throw new ThermChannelCorrelationException(
                $"Thermal observation property '{propertyName}' is invalid.");
        }
    }

    private sealed record ThermObservation(
        string Sha256,
        string GpuzSha256,
        string NvapiModuleSha256,
        long LogPrefixSizeBytes,
        DateTime LogBefore,
        DateTime LogAfter,
        IReadOnlyList<double> Channel0,
        IReadOnlyList<double> Channel1);

    private sealed record ErrorMetrics(
        double MeanAbsoluteError,
        double MaximumAbsoluteError);

    private sealed record WindowMetrics(
        int SessionIndex,
        string FirstTimestampLocal,
        string LastTimestampLocal,
        ErrorMetrics Channel0ToGpu,
        ErrorMetrics Channel1ToHotSpot,
        double CombinedMeanAbsoluteError,
        double AlternativeCombinedMeanAbsoluteError);
}
