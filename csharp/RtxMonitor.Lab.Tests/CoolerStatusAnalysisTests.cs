using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace RtxMonitor.Lab.Tests;

// These fixtures exercise offline contracts. They are not physical fan evidence.
internal static class CoolerStatusAnalysisTests
{
    private const string FirstCaller = "0x0021d654";
    private const string SecondCaller = "0x0021d824";

    internal static void RunAll(Action<string, Action> run)
    {
        run("cooler analysis preserves caller and entry groups", TestGroupingAndStatistics);
        run("cooler analysis keeps untimed samples unpromoted", TestUntimedSamplesRemainUnknown);
        run("cooler analysis preserves the count byte without interpreting other bits", TestCountByte);
        run("cooler analysis accepts explicit timezone offsets", TestExplicitTimezoneOffsets);
        run("cooler analysis rejects oversized files before JSON parsing", TestOversizedInput);
        run("cooler analysis rejects directories as observation files", () => RejectFile(Path.GetTempPath()));
        run("cooler analysis rejects UNC observation paths", () => RejectFile(@"\\invalid\share\observation.json"));
        run("cooler analysis rejects device observation paths", () => RejectFile(@"\\.\NUL"));
        run("cooler analysis rejects alternate data streams", () =>
        {
            using var input = new TemporaryInput(Fixture().ToJsonString());
            RejectFile(input.Path + ":alternate");
        });
        run("cooler analysis rejects duplicate JSON root properties", () =>
            RejectJson(Fixture().ToJsonString().Replace(
                "\"schema_version\":2", "\"schema_version\":2,\"schema_version\":2",
                StringComparison.Ordinal)));
        run("cooler analysis rejects duplicate nested JSON properties", () =>
            RejectJson(Fixture().ToJsonString().Replace(
                "\"entry_index\":0", "\"entry_index\":0,\"entry_index\":0",
                StringComparison.Ordinal)));

        (string Name, Action<JsonObject> Mutate)[] rejected =
        [
            ("legacy schema", root => root["schema_version"] = 1),
            ("different source", root => root["source_kind"] = "other"),
            ("different profile", root => root["profile_name"] = "other"),
            ("different driver", root => root["gpu_profile"]!["driver_version"] = "611.00"),
            ("different GPU UUID", root => root["gpu_profile"]!["gpu_uuid"] = "GPU-00000000-0000-0000-0000-000000000000"),
            ("different VBIOS", root => root["gpu_profile"]!["vbios_version"] = "94.06.25.00.fd"),
            ("different PCI device", root => root["gpu_profile"]!["pci_device_id"] = "0x2505"),
            ("different GPU-Z binary", root => root["gpuz_sha256"] = new string('0', 64)),
            ("different loaded module", root => root["loaded_nvapi_module"]!["file_sha256"] = new string('0', 64)),
            ("function outside loaded image", root => root["loaded_nvapi_module"]!["end_address"] = "0x61e80001"),
            ("different interface", root => root["interface_id"] = "0x00000000"),
            ("different structure size", root => root["structure_size_bytes"] = 1708),
            ("different word count", root => root["structure_word_count"] = 427),
            ("different entry stride", root => root["entry_stride_bytes"] = 48),
            ("different field offset", root => root["raw_field_byte_offsets_within_entry"]![0] = 0),
            ("inconsistent total calls", root => root["call_count"] = 3),
            ("inconsistent caller calls", root => root["call_sites"]![0]!["call_count"] = 2),
            ("duplicate call sites", root => root["call_sites"]![1]!["caller_rva"] = FirstCaller),
            ("unknown sample caller", root => Sample(root, 0)["caller_rva"] = "0x00000000"),
            ("duplicate sequence", root => Sample(root, 1)["sequence"] = 1),
            ("sequence gap", root => Sample(root, 1)["sequence"] = 3),
            ("failed return", root => Sample(root, 0)["return_status"] = "0xffffffff"),
            ("sample structure drift", root => Sample(root, 0)["structure_version"] = "0x000106a9"),
            ("raw header drift", root => Sample(root, 0)["raw_words"]![0] = "0x000106a9"),
            ("raw count drift", root => Sample(root, 0)["raw_words"]![1] = "0x00000001"),
            ("count exceeds capacity", root => Sample(root, 0)["observed_count"] = 33),
            ("missing entry", root => Sample(root, 0)["raw_entries"]!.AsArray().RemoveAt(1)),
            ("entry ordering drift", root => Sample(root, 0)["raw_entries"]![0]!["entry_index"] = 1),
            ("entry base drift", root => Sample(root, 0)["raw_entries"]![1]!["base_word_index"] = 22),
            ("raw identifier mismatch", root => Sample(root, 0)["raw_entries"]![0]!["raw_identifier_word"] = "0x000000ff"),
            ("raw field mismatch", root => Sample(root, 0)["raw_entries"]![0]!["raw_field_words"]![0] = "0x000000ff"),
            ("truncated words", root => Sample(root, 0)["raw_words"]!.AsArray().RemoveAt(425)),
            ("invalid hexadecimal word", root => Sample(root, 0)["raw_words"]![425] = "0xZZZZZZZZ"),
            ("unknown root field", root => root["fan_rpm"] = 1234),
            ("unknown sample timestamp", root => Sample(root, 0)["timestamp_utc"] = "2026-08-27T20:00:01Z"),
            ("missing required field", root => root.Remove("capture_point")),
            ("reversed capture window", root => root["capture_started_utc"] = "2026-08-27T20:00:11Z"),
            ("non-growing reference", root => root["reference_log"]!["size_bytes_midpoint"] = 100),
            ("stale reference samples", root => root["reference_log"]!["last_sample_local_midpoint"] = "2026-08-27 17:00:00")
        ];
        foreach ((string name, Action<JsonObject> mutate) in rejected)
        {
            run($"cooler analysis rejects {name}", () =>
            {
                JsonObject root = Fixture();
                mutate(root);
                RejectJson(root.ToJsonString());
            });
        }
    }

    private static void TestGroupingAndStatistics()
    {
        JsonObject root = Fixture();
        JsonArray samples = root["samples"]!.AsArray();
        JsonObject repeatedFirst = Sample(root, 0).DeepClone().AsObject();
        repeatedFirst["sequence"] = 3;
        SetField(repeatedFirst, entry: 0, field: 0, uint.MaxValue);
        JsonObject repeatedSecond = Sample(root, 1).DeepClone().AsObject();
        repeatedSecond["sequence"] = 4;
        samples.Add(repeatedFirst);
        samples.Add(repeatedSecond);
        root["call_count"] = 4;
        root["call_sites"]![0]!["call_count"] = 2;
        root["call_sites"]![1]!["call_count"] = 2;

        using var input = new TemporaryInput(root.ToJsonString());
        CoolerStatusAnalysisReport report = CoolerStatusAnalysis.AnalyzeFile(input.Path);
        Check(report.SampleCount == 4, "all interleaved calls must remain represented");
        Check(report.CallSites.Count == 2, "the two callers must remain separate");
        Check(report.Fields.Count == 16, "two callers times two entries times four fields must yield 16 groups");
        CoolerStatusFieldAnalysis changed = report.Fields.Single(field =>
            field.CallerRva == FirstCaller && field.EntryOrdinal == 0 &&
            field.RawFieldByteOffsetWithinEntry == 4);
        Check(changed.ValidSampleCount == 2 && changed.FirstSequence == 1 && changed.LastSequence == 3,
            "interleaved samples must use original global sequence positions");
        Check(changed.BaseWordIndex == 10 && changed.RawFieldAbsoluteByteOffset == 44,
            "entry-relative offsets must resolve to the original raw buffer location");
        Check(changed.MinimumRawValue == 0x10000001u && changed.MaximumRawValue == uint.MaxValue &&
            changed.FirstRawValue == 0x10000001u && changed.LastRawValue == uint.MaxValue,
            "unsigned raw statistics must retain the full 32-bit range");
        Check(changed.DistinctValueCount == 2 && changed.TransitionCount == 1,
            "only changes within a caller and entry group count as transitions");
        Check(changed.RawIdentifierWords.SequenceEqual(new uint[] { 1 }),
            "raw entry identifiers must be retained separately from sensor meaning");

        CoolerStatusFieldAnalysis stable = report.Fields.Single(field =>
            field.CallerRva == SecondCaller && field.EntryOrdinal == 0 &&
            field.RawFieldByteOffsetWithinEntry == 4);
        Check(stable.ValidSampleCount == 2 && stable.DistinctValueCount == 1 && stable.TransitionCount == 0 &&
            stable.MinimumRawValue == 0x10000002u && stable.MaximumRawValue == 0x10000002u,
            "the changed first caller must not contaminate the stable second caller");
        Check(report.Fields.Single(field => field.CallerRva == FirstCaller && field.EntryOrdinal == 1 &&
            field.RawFieldByteOffsetWithinEntry == 4).MinimumRawValue == 0x50000001u,
            "a second entry must not share the first entry's values");
    }

    private static void TestUntimedSamplesRemainUnknown()
    {
        using var input = new TemporaryInput(Fixture().ToJsonString());
        CoolerStatusAnalysisReport report = CoolerStatusAnalysis.AnalyzeFile(input.Path);
        Check(report.MappingStatus == "raw_unknown", "offline structure alone cannot establish RPM or PWM");
        Check(report.EvidenceStage == "raw_unknown" && report.TemporalAlignment == "unavailable_sequence_only",
            "capture boundaries and sequence order cannot supply missing per-call timestamps");
        Check(report.GpuzReference is null, "an absent reference must not be fabricated");
        Check(report.Warnings.Count > 0, "untimed observations must explain their evidence limitations");
        Check(report.Fields.All(field => field.ValidSampleCount == 1 && field.TransitionCount == 0),
            "one sample per caller cannot imply variation or temporal correlation");
    }

    private static void TestCountByte()
    {
        JsonObject root = Fixture();
        Sample(root, 0)["raw_words"]![1] = "0xffff0002";
        Sample(root, 1)["raw_words"]![1] = "0x12340001";
        Sample(root, 1)["observed_count"] = 1;
        Sample(root, 1)["raw_entries"]!.AsArray().RemoveAt(1);
        using var input = new TemporaryInput(root.ToJsonString());
        CoolerStatusAnalysisReport report = CoolerStatusAnalysis.AnalyzeFile(input.Path);
        Check(report.Fields.Count == 12, "only the declared entries may form groups");
        Check(report.CallSites.Single(site => site.CallerRva == SecondCaller).MaximumObservedEntryCount == 1,
            "upper bits of the count word cannot become additional entries");
    }

    private static void TestExplicitTimezoneOffsets()
    {
        JsonObject root = Fixture();
        root["capture_started_utc"] = "2026-08-27T17:00:00-03:00";
        root["captured_utc"] = "2026-08-27T17:00:10-03:00";
        using var input = new TemporaryInput(root.ToJsonString());
        CoolerStatusAnalysisReport report = CoolerStatusAnalysis.AnalyzeFile(input.Path);
        Check(report.Session.CaptureStartedUtc == "2026-08-27T17:00:00-03:00",
            "offset-bearing provenance must remain intact while comparing absolute instants");
    }

    private static void TestOversizedInput()
    {
        using var input = new TemporaryInput("{");
        using (var stream = new FileStream(input.Path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(16L * 1024 * 1024 + 1);
        }

        CoolerStatusAnalysisException error = RejectFile(input.Path);
        Check(error.Message.Contains("16777216", StringComparison.Ordinal),
            "an oversized malformed document must fail on the input bound before JSON parsing");
    }

    private static void RejectJson(string json)
    {
        using var input = new TemporaryInput(json);
        RejectFile(input.Path);
    }

    private static CoolerStatusAnalysisException RejectFile(string path)
    {
        try
        {
            CoolerStatusAnalysis.AnalyzeFile(path);
        }
        catch (CoolerStatusAnalysisException error)
        {
            return error;
        }

        throw new InvalidOperationException("inconsistent cooler evidence must fail with its documented exception");
    }

    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "nvapi-cooler-status-v1-observation-v2.json")))!.AsObject();

    private static JsonObject Sample(JsonObject root, int index) => root["samples"]![index]!.AsObject();

    private static void SetField(JsonObject sample, int entry, int field, uint value)
    {
        string raw = "0x" + value.ToString("x8", CultureInfo.InvariantCulture);
        sample["raw_words"]![10 + 13 * entry + 1 + field] = raw;
        sample["raw_entries"]![entry]!["raw_field_words"]![field] = raw;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class TemporaryInput : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"rtx-monitor-cooler-tests-{Guid.NewGuid():N}.json");

        internal TemporaryInput(string content) => File.WriteAllText(Path, content, new UTF8Encoding(false));

        public void Dispose() => File.Delete(Path);
    }
}
