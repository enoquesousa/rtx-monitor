using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RtxMonitor.Lab;

/// <summary>Offline validation and unsigned-word statistics for the fixed observation-v2 layout.</summary>
public static partial class CoolerStatusAnalysis
{
    public const int SchemaVersion = 1;
    public const string SourceKind = "nvapi_cooler_status_analysis";
    public const int MaximumInputBytes = 16 * 1024 * 1024;
    private const string ObservationKind = "nvapi_cooler_status_v1_observation";
    private const string ProfileName = "gpuz-2.70.0-nvapi-610.88-cooler-status-v1";
    private const string GpuzSha = "6cb0ef29682452de81a9576808881685161411a1fad00938ba04131159979c29";
    private const string ModuleSha = "fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf";
    private const string CandidateSha = "3aaada9b367dacca7cf74511bae8532bd79b7f8bd06b9bb609056f3d9da1f1d7";
    private const string PriorSha = "f580f67da61df2287257fb023fe277d310fdf424f588bbd96d01ac01433f8de2";
    private const string StructureVersion = "0x000106a8";
    private const string FunctionRva = "0x001b9f10";
    private static readonly string[] CallerRvas = ["0x0021d654", "0x0021d824"];
    private static readonly int[] RawOffsets = [4, 8, 12, 16];

    public static CoolerStatusAnalysisReport AnalyzeFile(string inputPath)
    {
        try
        {
            Require(!string.IsNullOrWhiteSpace(inputPath), "An observation input path is required.");
            byte[] bytes = ReadBoundedFile(inputPath);
            ReadOnlyMemory<byte> json = bytes;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            {
                json = json[3..];
            }
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            return Analyze(document.RootElement, bytes, Path.GetFileName(inputPath));
        }
        catch (CoolerStatusAnalysisException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException or NotSupportedException)
        {
            throw new CoolerStatusAnalysisException($"The cooler observation could not be analyzed: {error.Message}", error);
        }
    }

    private static CoolerStatusAnalysisReport Analyze(JsonElement root, byte[] bytes, string fileName)
    {
        Shape(root, "observation", "schema_version source_kind profile_name capture_started_utc captured_utc duration_seconds process_id gpuz_sha256 debugger_sha256 debugger_file_version gpu_profile identity_probe_sha256 candidate_inventory_sha256 prior_observation_sha256 nvapi_module_sha256 loaded_nvapi_module interface_id function_rva caller_module_name caller_rvas capture_point buffer_pointer_expression return_status_register structure_version structure_size_bytes structure_word_count count_byte_offset entry_base_offset entry_stride_bytes entry_capacity raw_field_byte_offsets_within_entry reference_log call_count call_sites samples warning");
        Require(Integer(root, "schema_version", 1, 2) == 2, "Only cooler observation schema version 2 is accepted.");
        Equal(root, "source_kind", ObservationKind);
        Equal(root, "profile_name", ProfileName);
        Equal(root, "gpuz_sha256", GpuzSha);
        Equal(root, "candidate_inventory_sha256", CandidateSha);
        Equal(root, "prior_observation_sha256", PriorSha);
        Equal(root, "nvapi_module_sha256", ModuleSha);
        Equal(root, "interface_id", "0x35aed5e8");
        Equal(root, "function_rva", FunctionRva);
        Equal(root, "caller_module_name", "GPU-Z.exe");
        Equal(root, "capture_point", "post_call_return");
        Equal(root, "buffer_pointer_expression", "poi(@esp+4)");
        Equal(root, "return_status_register", "eax");
        Equal(root, "structure_version", StructureVersion);
        ExactInteger(root, "structure_size_bytes", 1704);
        ExactInteger(root, "structure_word_count", 426);
        ExactInteger(root, "count_byte_offset", 4);
        ExactInteger(root, "entry_base_offset", 40);
        ExactInteger(root, "entry_stride_bytes", 52);
        ExactInteger(root, "entry_capacity", 32);
        JsonElement callers = Array(root, "caller_rvas", 2, 2);
        JsonElement offsets = Array(root, "raw_field_byte_offsets_within_entry", 4, 4);
        for (int index = 0; index < CallerRvas.Length; index++)
        {
            Require(ValueText(callers[index], "caller_rvas item") == CallerRvas[index], "The ordered caller RVA profile does not match.");
        }
        for (int index = 0; index < RawOffsets.Length; index++)
        {
            Require(Number(offsets[index], "raw offset", 0, 16) == RawOffsets[index], "The ordered raw field offsets do not match.");
        }

        CoolerStatusGpuProfile gpu = ValidateGpu(root.GetProperty("gpu_profile"));
        CoolerStatusLoadedModule module = ValidateModule(root.GetProperty("loaded_nvapi_module"));
        CoolerStatusAnalysisSession session = ValidateSession(root);
        int sampleCount = Integer(root, "call_count", 2, 1024);
        JsonElement samples = Array(root, "samples", 2, 1024);
        Require(samples.GetArrayLength() == sampleCount, "call_count does not match samples.");
        var sites = CallerRvas.ToDictionary(caller => caller, caller => new SiteAccumulator(caller), StringComparer.Ordinal);
        var fields = new Dictionary<(string Caller, int Entry, int Offset), FieldAccumulator>();
        for (int index = 0; index < sampleCount; index++)
        {
            ValidateSample(samples[index], index + 1, sites, fields);
        }

        JsonElement summaries = Array(root, "call_sites", 2, 2);
        for (int index = 0; index < CallerRvas.Length; index++)
        {
            JsonElement summary = summaries[index];
            Shape(summary, "call_sites item", "caller_rva call_count");
            Equal(summary, "caller_rva", CallerRvas[index]);
            Require(Integer(summary, "call_count", 1, 1024) == sites[CallerRvas[index]].Count,
                "The caller summary does not match the observed samples.");
        }

        var source = new CoolerStatusAnalysisSource(
            fileName, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            2, ObservationKind, ProfileName, Integer(root, "process_id", 1, int.MaxValue), GpuzSha,
            Sha(root, "debugger_sha256"), Text(root, "debugger_file_version"), Sha(root, "identity_probe_sha256"),
            CandidateSha, PriorSha, ModuleSha, "0x35aed5e8", FunctionRva, "GPU-Z.exe", StructureVersion, gpu, module);
        string sourceWarning = Text(root, "warning");
        return new CoolerStatusAnalysisReport(
            SchemaVersion, SourceKind, "raw_unknown", "raw_unknown", "unavailable_sequence_only", source, session,
            sampleCount, CallerRvas.Select(caller => sites[caller].Build()).ToArray(),
            fields.OrderBy(item => item.Key.Caller, StringComparer.Ordinal).ThenBy(item => item.Key.Entry)
                .ThenBy(item => item.Key.Offset).Select(item => item.Value.Build()).ToArray(),
            [
                "The unsigned values describe buffer positions only; no field is assigned a sensor name, unit, or fan index.",
                "Observation v2 records sequence order but no per-call timestamp; temporal alignment and sensor correlation are unavailable.",
                "Raw identifier words are retained as uninterpreted values, and transitions compare consecutive observations of each positional group.",
                "Artifact hashes and capture metadata identify the supplied evidence; this offline analysis does not repeat the hardware or executable identity probes.",
                sourceWarning,
            ]);
    }

    private static CoolerStatusGpuProfile ValidateGpu(JsonElement gpu)
    {
        Shape(gpu, "gpu_profile", "gpu_index gpu_name gpu_uuid driver_version nvml_version pci_bus_id pci_vendor_id pci_device_id pci_subsystem_vendor_id pci_subsystem_device_id vbios_version");
        Equal(gpu, "gpu_name", "NVIDIA GeForce RTX 3060");
        Equal(gpu, "gpu_uuid", "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd");
        Equal(gpu, "driver_version", "610.88");
        Equal(gpu, "nvml_version", "13.610.88");
        Equal(gpu, "pci_bus_id", "00000000:01:00.0");
        Equal(gpu, "pci_vendor_id", "0x10de");
        Equal(gpu, "pci_device_id", "0x2504");
        Equal(gpu, "pci_subsystem_vendor_id", "0x10de");
        Equal(gpu, "pci_subsystem_device_id", "0x1536");
        Equal(gpu, "vbios_version", "94.06.25.00.fc");
        return new CoolerStatusGpuProfile(Integer(gpu, "gpu_index", 0, 31), Text(gpu, "gpu_name"),
            Text(gpu, "gpu_uuid"), Text(gpu, "driver_version"), Text(gpu, "nvml_version"), Text(gpu, "pci_bus_id"),
            Text(gpu, "pci_vendor_id"), Text(gpu, "pci_device_id"), Text(gpu, "pci_subsystem_vendor_id"),
            Text(gpu, "pci_subsystem_device_id"), Text(gpu, "vbios_version"));
    }

    private static CoolerStatusLoadedModule ValidateModule(JsonElement module)
    {
        Shape(module, "loaded_nvapi_module", "file_name file_sha256 start_address end_address proof_source");
        Equal(module, "file_name", "nvapi_impl.dll");
        Equal(module, "file_sha256", ModuleSha);
        Equal(module, "proof_source", "cdb_modload_target_image");
        uint start = Word(module.GetProperty("start_address"), "module start_address");
        uint end = Word(module.GetProperty("end_address"), "module end_address");
        Require(start > 0 && end > start && (ulong)start + 0x001b9f10U < end,
            "The loaded module range does not contain the fixed function RVA.");
        return new CoolerStatusLoadedModule("nvapi_impl.dll", ModuleSha, Text(module, "start_address"),
            Text(module, "end_address"), "cdb_modload_target_image");
    }

    private static CoolerStatusAnalysisSession ValidateSession(JsonElement root)
    {
        string startedText = Text(root, "capture_started_utc");
        string endedText = Text(root, "captured_utc");
        DateTimeOffset started = Instant(startedText, "capture_started_utc");
        DateTimeOffset ended = Instant(endedText, "captured_utc");
        int duration = Integer(root, "duration_seconds", 10, 60);
        Require(ended > started && (ended - started).TotalSeconds >= duration,
            "The capture window must contain the declared capture duration.");
        JsonElement reference = root.GetProperty("reference_log");
        Shape(reference, "reference_log", "size_bytes_before size_bytes_midpoint size_bytes_after last_write_utc_before last_write_utc_midpoint last_write_utc_after last_sample_local_before last_sample_local_midpoint last_sample_local_after grew_during_capture");
        long beforeSize = Number(reference.GetProperty("size_bytes_before"), "size_bytes_before", 1, long.MaxValue);
        long middleSize = Number(reference.GetProperty("size_bytes_midpoint"), "size_bytes_midpoint", 1, long.MaxValue);
        long afterSize = Number(reference.GetProperty("size_bytes_after"), "size_bytes_after", 1, long.MaxValue);
        Require(beforeSize < middleSize && middleSize < afterSize, "Reference log sizes must increase at both capture checkpoints.");
        Require(reference.GetProperty("grew_during_capture").ValueKind == JsonValueKind.True,
            "The reference log must report growth during capture.");
        string beforeWriteText = Text(reference, "last_write_utc_before");
        string middleWriteText = Text(reference, "last_write_utc_midpoint");
        string afterWriteText = Text(reference, "last_write_utc_after");
        DateTimeOffset beforeWrite = Instant(beforeWriteText, "last_write_utc_before");
        DateTimeOffset middleWrite = Instant(middleWriteText, "last_write_utc_midpoint");
        DateTimeOffset afterWrite = Instant(afterWriteText, "last_write_utc_after");
        Require(beforeWrite < middleWrite && middleWrite < afterWrite && beforeWrite <= started &&
            middleWrite >= started && middleWrite <= ended,
            "Reference log write timestamps are inconsistent with the capture window or checkpoint order.");
        string beforeLocal = Text(reference, "last_sample_local_before");
        string middleLocal = Text(reference, "last_sample_local_midpoint");
        string afterLocal = Text(reference, "last_sample_local_after");
        DateTime beforeSample = LocalInstant(beforeLocal);
        DateTime middleSample = LocalInstant(middleLocal);
        DateTime afterSample = LocalInstant(afterLocal);
        Require(beforeSample < middleSample && middleSample < afterSample,
            "Reference log sample timestamps must increase at both capture checkpoints.");
        return new CoolerStatusAnalysisSession(startedText, endedText, duration,
            new CoolerStatusReferenceLog(beforeSize, middleSize, afterSize, beforeWriteText, middleWriteText,
                afterWriteText, beforeLocal, middleLocal, afterLocal, true));
    }

    private static void ValidateSample(JsonElement sample, int expectedSequence,
        Dictionary<string, SiteAccumulator> sites,
        Dictionary<(string Caller, int Entry, int Offset), FieldAccumulator> fields)
    {
        Shape(sample, "sample", "sequence thread_id caller_rva return_status buffer_address structure_version observed_count raw_words raw_entries");
        int sequence = Integer(sample, "sequence", 1, 1024);
        Require(sequence == expectedSequence, "Sample sequence must be contiguous, ordered, and start at one.");
        string caller = Text(sample, "caller_rva");
        Require(sites.ContainsKey(caller), "The sample caller is outside the fixed profile.");
        Equal(sample, "return_status", "0x00000000");
        Equal(sample, "structure_version", StructureVersion);
        _ = Word(sample.GetProperty("thread_id"), "thread_id");
        uint buffer = Word(sample.GetProperty("buffer_address"), "buffer_address");
        Require(buffer != 0 && (ulong)buffer + 1704U <= (ulong)uint.MaxValue + 1U,
            "The sample buffer range is invalid for the captured 32-bit layout.");
        int count = Integer(sample, "observed_count", 1, 32);
        JsonElement raw = Array(sample, "raw_words", 426, 426);
        var words = new uint[426];
        for (int index = 0; index < words.Length; index++)
        {
            words[index] = Word(raw[index], $"raw_words[{index}]");
        }
        Require(words[0] == 0x000106a8U, "The raw structure version differs from the profile.");
        // The existing bounded capture extracts only this byte; the remaining bits stay uninterpreted.
        Require((words[1] & 0xffU) == (uint)count, "The raw count byte differs from observed_count.");
        JsonElement entries = Array(sample, "raw_entries", 1, 32);
        Require(entries.GetArrayLength() == count, "The raw entry count differs from observed_count.");
        sites[caller].Add(sequence, count);
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            JsonElement entry = entries[ordinal];
            Shape(entry, "raw entry", "entry_index base_word_index raw_identifier_word raw_field_words");
            int baseWord = 10 + ordinal * 13;
            Require(Integer(entry, "entry_index", 0, 31) == ordinal &&
                Integer(entry, "base_word_index", 10, 413) == baseWord,
                "The raw entry ordinal or base position is inconsistent with the fixed layout.");
            uint identifier = Word(entry.GetProperty("raw_identifier_word"), "raw_identifier_word");
            Require(identifier == words[baseWord], "The raw identifier word differs from the complete buffer.");
            JsonElement rawFields = Array(entry, "raw_field_words", 4, 4);
            for (int index = 0; index < RawOffsets.Length; index++)
            {
                int offset = RawOffsets[index];
                uint value = Word(rawFields[index], "raw_field_words item");
                Require(value == words[baseWord + offset / 4], "A raw field word differs from the complete buffer.");
                var key = (caller, ordinal, offset);
                if (!fields.TryGetValue(key, out FieldAccumulator? field))
                {
                    field = new FieldAccumulator(caller, ordinal, offset);
                    fields.Add(key, field);
                }
                field.Add(sequence, value, identifier);
            }
        }
    }

    private static byte[] ReadBoundedFile(string path)
    {
        Require(!path.StartsWith(@"\\", StringComparison.Ordinal) &&
            !path.StartsWith("//", StringComparison.Ordinal),
            "The cooler observation must identify a local file, without a UNC or device path.");
        string resolvedPath = Path.GetFullPath(path);
        Require(!resolvedPath.StartsWith(@"\\", StringComparison.Ordinal) &&
            !resolvedPath[Path.GetPathRoot(resolvedPath)!.Length..].Contains(':'),
            "The cooler observation must identify a local file, without a device path or alternate stream.");
        FileAttributes attributes = File.GetAttributes(resolvedPath);
        Require((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0,
            "The cooler observation must be a regular file, not a directory or reparse point.");
        using var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(stream.Length > 0 && stream.Length <= MaximumInputBytes,
            $"The observation must contain between 1 and {MaximumInputBytes} bytes.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        Require(stream.ReadByte() == -1, "The observation changed size while being read.");
        return bytes;
    }

    private static void Shape(JsonElement value, string label, string members)
    {
        Require(value.ValueKind == JsonValueKind.Object, $"{label} must be an object.");
        var remaining = members.Split(' ').ToHashSet(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            Require(remaining.Remove(property.Name), $"{label} contains an unknown or duplicate property: {property.Name}.");
        }
        Require(remaining.Count == 0, $"{label} is missing a required property.");
    }

    private static JsonElement Array(JsonElement parent, string name, int minimum, int maximum)
    {
        JsonElement value = parent.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= minimum &&
            value.GetArrayLength() <= maximum, $"{name} has an invalid array length or type.");
        return value;
    }

    private static string Text(JsonElement parent, string name) => ValueText(parent.GetProperty(name), name);

    private static string ValueText(JsonElement value, string label)
    {
        Require(value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()),
            $"{label} must be a nonempty string.");
        return value.GetString()!;
    }

    private static void Equal(JsonElement parent, string name, string expected) =>
        Require(Text(parent, name) == expected, $"{name} does not match the fixed observation profile.");

    private static int Integer(JsonElement parent, string name, int minimum, int maximum) =>
        (int)Number(parent.GetProperty(name), name, minimum, maximum);

    private static long Number(JsonElement value, string label, long minimum, long maximum)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) &&
            number >= minimum && number <= maximum, $"{label} must be an integer in [{minimum}, {maximum}].");
        return value.GetInt64();
    }

    private static void ExactInteger(JsonElement parent, string name, int expected) =>
        Require(Integer(parent, name, 0, int.MaxValue) == expected, $"{name} differs from the fixed layout.");

    private static uint Word(JsonElement value, string label)
    {
        string text = ValueText(value, label);
        Require(text.Length == 10 && text.StartsWith("0x", StringComparison.Ordinal) &&
            text.AsSpan(2).IndexOfAnyExcept("0123456789abcdef") < 0, $"{label} must be a lowercase 32-bit hexadecimal word.");
        return uint.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    private static string Sha(JsonElement parent, string name)
    {
        string text = Text(parent, name);
        Require(text.Length == 64 && text.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0,
            $"{name} must be a lowercase SHA-256 digest.");
        return text;
    }

    private static DateTimeOffset Instant(string text, string label)
    {
        Require(InstantPattern().IsMatch(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _), $"{label} must be an ISO timestamp with an explicit UTC offset.");
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    private static DateTime LocalInstant(string text)
    {
        Require(DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _), "A local reference sample timestamp is invalid.");
        return DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstantPattern();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new CoolerStatusAnalysisException(message);
    }

    private sealed class SiteAccumulator(string caller)
    {
        public int Count { get; private set; }
        private int minimum = 32;
        private int maximum;
        private int first;
        private int last;

        public void Add(int sequence, int entries)
        {
            if (Count++ == 0) first = sequence;
            last = sequence;
            minimum = Math.Min(minimum, entries);
            maximum = Math.Max(maximum, entries);
        }

        public CoolerStatusCallSiteAnalysis Build() => new(caller, Count, minimum, maximum, first, last);
    }

    private sealed class FieldAccumulator(string caller, int ordinal, int offset)
    {
        private int count;
        private int firstSequence;
        private int lastSequence;
        private uint minimum = uint.MaxValue;
        private uint maximum;
        private uint first;
        private uint last;
        private int transitions;
        private readonly HashSet<uint> distinct = [];
        private readonly HashSet<uint> identifiers = [];

        public void Add(int sequence, uint value, uint identifier)
        {
            if (count == 0)
            {
                first = value;
                firstSequence = sequence;
            }
            else if (last != value)
            {
                transitions++;
            }
            count++;
            last = value;
            lastSequence = sequence;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            distinct.Add(value);
            identifiers.Add(identifier);
        }

        public CoolerStatusFieldAnalysis Build() => new(caller, ordinal, 10 + ordinal * 13, offset,
            40 + ordinal * 52 + offset, count, firstSequence, lastSequence, minimum, maximum, first, last,
            distinct.Count, transitions, identifiers.Order().ToArray());
    }
}
