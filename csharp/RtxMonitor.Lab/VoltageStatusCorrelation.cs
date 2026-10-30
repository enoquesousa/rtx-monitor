using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace RtxMonitor.Lab;

public sealed record VoltageStatusMapping(
    int WordIndex,
    int OffsetBytes,
    string SemanticField,
    string Unit,
    string ReferenceChannel,
    int SampleCount,
    double MeanAbsoluteErrorVolts,
    double MaximumAbsoluteErrorVolts,
    string Status);

public sealed record VoltageStatusCorrelationReport(
    int SchemaVersion,
    string SourceKind,
    string ObservationSha256,
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
    double ScaleDivisor,
    double ToleranceVolts,
    string MappingStatus,
    VoltageStatusMapping Mapping,
    IReadOnlyList<string> Warnings);

public sealed class VoltageStatusCorrelationException : Exception
{
    public VoltageStatusCorrelationException(string message) : base(message) { }
    public VoltageStatusCorrelationException(string message, Exception inner) : base(message, inner) { }
}

public static class VoltageStatusCorrelation
{
    public const int SchemaVersion = 1;
    public const string SourceKind = "nvapi_voltage_status_reference_correlation";
    public const double ScaleDivisor = 1_000_000.0;
    public const double RoundingToleranceVolts = 0.001;
    private const long MaximumObservationSizeBytes = 16L * 1024 * 1024;
    private const string ExpectedSource = "nvapi_voltage_status_v1_observation";
    private const string ExpectedGpuzSha256 = "6cb0ef29682452de81a9576808881685161411a1fad00938ba04131159979c29";
    private const string ExpectedNvapiSha256 = "fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf";
    private const string ExpectedInterfaceId = "0x465f9bcf";
    private const string ExpectedFunctionRva = "0x00198010";
    private const string ExpectedStructureVersion = "0x0001004c";
    private static readonly string[] TimestampFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFF"];

    public static VoltageStatusCorrelationReport AnalyzeFiles(string observationPath, string gpuzLogPath)
    {
        Observation observation = ReadObservation(observationPath);
        GpuzLogAnalysis log = GpuzSensorLog.AnalyzeFilePrefix(gpuzLogPath, observation.LogPrefixSizeBytes);
        GpuzChannelAnalysis voltage = log.Channels.SingleOrDefault(channel =>
            channel.Name == "GPU Voltage" && channel.Unit == "V") ??
            throw new VoltageStatusCorrelationException("GPU-Z reference channel 'GPU Voltage [V]' is missing or invalid.");

        Window? best = null;
        for (int sessionIndex = 0; sessionIndex < log.SessionCount; sessionIndex++)
        {
            GpuzLogSample[] session = log.Samples.Where(sample => sample.SessionIndex == sessionIndex).ToArray();
            for (int start = 0; start <= session.Length - observation.Microvolts.Count; start++)
            {
                GpuzLogSample[] samples = session.Skip(start).Take(observation.Microvolts.Count).ToArray();
                DateTime first = ParseTimestamp(samples[0].TimestampLocal);
                DateTime last = ParseTimestamp(samples[^1].TimestampLocal);
                if (first < observation.LogBefore.AddSeconds(-2) || last > observation.LogAfter.AddSeconds(2) ||
                    first > observation.LogAfter || last < observation.LogBefore)
                {
                    continue;
                }

                double sum = 0;
                double maximum = 0;
                for (int index = 0; index < samples.Length; index++)
                {
                    if (!double.TryParse(samples[index].Values[voltage.Index], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double reference) || !double.IsFinite(reference))
                    {
                        throw new VoltageStatusCorrelationException("The selected GPU-Z voltage window contains a non-numeric value.");
                    }

                    double error = Math.Abs(observation.Microvolts[index] / ScaleDivisor - reference);
                    sum += error;
                    maximum = Math.Max(maximum, error);
                }

                var candidate = new Window(sessionIndex, samples[0].TimestampLocal,
                    samples[^1].TimestampLocal, sum / samples.Length, maximum);
                if (best is null || candidate.MeanError < best.MeanError) best = candidate;
            }
        }

        if (best is null) throw new VoltageStatusCorrelationException("No GPU-Z sample window overlaps the bounded voltage observation.");
        string status = best.MaximumError <= RoundingToleranceVolts && observation.Microvolts.Distinct().Count() >= 3
            ? "matched_external_reference" : "ambiguous_or_outside_tolerance";
        var mapping = new VoltageStatusMapping(10, 0x28, "gpu_core_voltage", "V", voltage.Name,
            observation.Microvolts.Count, best.MeanError, best.MaximumError,
            best.MaximumError <= RoundingToleranceVolts ? "matched_rounding_tolerance" : "outside_tolerance");
        string[] warnings =
        [
            "The mapping is validated only against an external GPU-Z reference and the exact binary and board profile.",
            "The private structure is not a vendor-published NVAPI contract and must not be generalized to other profiles.",
            "Word 10 is core voltage in microvolts for this profile; it is not power or a 12 V input rail."
        ];
        return new VoltageStatusCorrelationReport(SchemaVersion, SourceKind, observation.Sha256,
            log.Artifact.Sha256, log.Artifact.SizeBytes, observation.GpuzSha256,
            observation.NvapiSha256, ExpectedInterfaceId, ExpectedFunctionRva,
            ExpectedStructureVersion, best.SessionIndex, best.FirstTimestamp, best.LastTimestamp,
            ScaleDivisor, RoundingToleranceVolts, status, mapping, warnings);
    }

    private static Observation ReadObservation(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string resolved = Path.GetFullPath(path);
        FileAttributes attributes = File.GetAttributes(resolved);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new VoltageStatusCorrelationException("The voltage observation must be a regular local file.");
        byte[] bytes = File.ReadAllBytes(resolved);
        if (bytes.Length is < 1 || bytes.Length > MaximumObservationSizeBytes)
            throw new VoltageStatusCorrelationException("The voltage observation size is outside the analysis limit.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            Require(root, "schema_version", 1); Require(root, "source_kind", ExpectedSource);
            Require(root, "interface_id", ExpectedInterfaceId); Require(root, "function_rva", ExpectedFunctionRva);
            Require(root, "structure_version", ExpectedStructureVersion); Require(root, "structure_size_bytes", 76);
            Require(root, "value_word_index", 10); Require(root, "value_offset_bytes", 40);
            Require(root, "scale_divisor", 1_000_000);
            string gpuz = root.GetProperty("gpuz_sha256").GetString()!;
            string nvapi = root.GetProperty("nvapi_module_sha256").GetString()!;
            if (gpuz != ExpectedGpuzSha256 || nvapi != ExpectedNvapiSha256)
                throw new VoltageStatusCorrelationException("The voltage observation does not match the fixed binary profile.");
            JsonElement reference = root.GetProperty("reference_log");
            long prefix = reference.GetProperty("size_bytes_after").GetInt64();
            DateTime before = ParseTimestamp(reference.GetProperty("last_sample_local_before").GetString());
            DateTime after = ParseTimestamp(reference.GetProperty("last_sample_local_after").GetString());
            JsonElement samples = root.GetProperty("samples");
            int count = root.GetProperty("call_count").GetInt32();
            if (samples.GetArrayLength() != count || count < 3) throw new VoltageStatusCorrelationException("The voltage sample count is invalid.");
            var values = new List<long>(); int sequence = 0;
            foreach (JsonElement sample in samples.EnumerateArray())
            {
                sequence++;
                if (sample.GetProperty("sequence").GetInt32() != sequence || sample.GetProperty("return_status").GetString() != "0x00000000")
                    throw new VoltageStatusCorrelationException("The voltage sample sequence or status is invalid.");
                long raw = sample.GetProperty("selected_raw_microvolts").GetInt64();
                double volts = sample.GetProperty("selected_volts").GetDouble();
                if (raw <= 0 || Math.Abs(volts - raw / ScaleDivisor) > 1e-12)
                    throw new VoltageStatusCorrelationException("A voltage sample violates the microvolt conversion.");
                values.Add(raw);
            }
            return new Observation(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), gpuz, nvapi,
                prefix, before, after, values);
        }
        catch (VoltageStatusCorrelationException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new VoltageStatusCorrelationException("The voltage observation JSON is malformed or incomplete.", error); }
    }

    private static DateTime ParseTimestamp(string? value) => DateTime.TryParseExact(value, TimestampFormats,
        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result)
        ? DateTime.SpecifyKind(result, DateTimeKind.Unspecified)
        : throw new VoltageStatusCorrelationException("A local GPU-Z timestamp is invalid.");
    private static void Require(JsonElement root, string name, string expected)
    { if (root.GetProperty(name).GetString() != expected) throw new VoltageStatusCorrelationException($"Voltage observation property '{name}' is invalid."); }
    private static void Require(JsonElement root, string name, int expected)
    { if (root.GetProperty(name).GetInt32() != expected) throw new VoltageStatusCorrelationException($"Voltage observation property '{name}' is invalid."); }
    private sealed record Observation(string Sha256, string GpuzSha256, string NvapiSha256, long LogPrefixSizeBytes,
        DateTime LogBefore, DateTime LogAfter, IReadOnlyList<long> Microvolts);
    private sealed record Window(int SessionIndex, string FirstTimestamp, string LastTimestamp, double MeanError, double MaximumError);
}
