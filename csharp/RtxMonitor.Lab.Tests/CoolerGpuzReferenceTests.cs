using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RtxMonitor.Lab.Tests;

internal static class CoolerGpuzReferenceTests
{
    private const string Header = "Date, Fan 1 Speed (RPM) [RPM], Fan 1 Speed (%) [%], Fan 2 Speed (RPM) [RPM], Fan 2 Speed (%) [%]\n";
    private const string Rows = "2026-08-27 17:00:00,1090,30,1100,30\n2026-08-27 17:00:05,1100,30,1110,30\n2026-08-27 17:00:10,1110,30,1120,30\n";

    internal static void RunAll(Action<string, Action> run)
    {
        run("cooler GPU-Z context isolates changed historical layouts and preserves unknown state", ValidWindow);
        run("cooler GPU-Z hash covers only recorded prefix", BoundedPrefix);
        run("cooler GPU-Z context rejects ambiguous sessions and incomplete boundaries", InvalidWindows);
        run("cooler GPU-Z missing channels and samples stay absent", MissingValues);
        run("cooler GPU-Z rejects invalid fan data and duplicate channels", InvalidValues);
        run("cooler CLI emits analysis and structured argument failures", CliContract);
    }

    private static CoolerStatusAnalysisReport Observation(long prefixLength)
    {
        var report = CoolerStatusAnalysis.AnalyzeFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nvapi-cooler-status-v1-observation-v2.json"));
        return report with
        {
            Session = report.Session with
            {
                ReferenceLog = report.Session.ReferenceLog with { SizeBytesAfter = prefixLength },
            },
        };
    }

    private static void WithLog(string content, Action<string, CoolerStatusAnalysisReport> action, string trailing = "")
    {
        string path = Path.Combine(Path.GetTempPath(), $"rtxmon-cooler-reference-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, content + trailing, new UTF8Encoding(false));
            action(path, Observation(Encoding.UTF8.GetByteCount(content)));
        }
        finally { File.Delete(path); }
    }

    private static void ValidWindow()
    {
        WithLog("Date, GPU Voltage [V]\n2026-08-27 16:00:00,0.9\n" + Header +
            "2026-08-27 16:59:59,1090,30,1100,30\n2026-08-27 16:59:59,1091,30,1101,30\n" + Rows, (path, observation) =>
        {
            var result = CoolerGpuzReference.AnalyzeFile(path, observation);
            Check(result.SelectedSessionIndex == 1 && result.WindowSampleCount == 3, "wrong session/window");
            Check(result.Channels.Count == 4 && result.Channels[0].Minimum == 1090 && result.Channels[0].Maximum == 1110, "wrong RPM range");
            Check(result.Channels[1].Minimum == 30 && result.Channels[1].Maximum == 30, "wrong percentage range");
            Check(result.IntegrityStatus == "unsealed_historical_reference" && result.AlignmentMethod == "window_only_no_sample_pairing", "evidence overstated");
            Check(observation.MappingStatus == "raw_unknown" && observation.EvidenceStage == "raw_unknown", "candidate promoted");
            using var json = JsonDocument.Parse(LabJson.SerializeCoolerStatusAnalysis(observation with { GpuzReference = result }));
            Check(json.RootElement.GetProperty("gpuz_reference").GetProperty("channels").GetArrayLength() == 4, "missing JSON context");
        });
    }

    private static void BoundedPrefix()
    {
        string? firstHash = null;
        WithLog(Header + Rows, (path, observation) => firstHash = CoolerGpuzReference.AnalyzeFile(path, observation).PrefixSha256);
        WithLog(Header + Rows, (path, observation) =>
        {
            var result = CoolerGpuzReference.AnalyzeFile(path, observation);
            Check(result.PrefixSha256 == firstHash, "appended data changed prefix hash");
        }, trailing: "ignored incomplete appended row");
        WithLog(Header + Rows, (path, observation) =>
        {
            Reject(() => CoolerGpuzReference.AnalyzeFile(path, observation with
            {
                Session = observation.Session with { ReferenceLog = observation.Session.ReferenceLog with { SizeBytesAfter = new FileInfo(path).Length + 1 } },
            }));
            Reject(() => CoolerGpuzReference.AnalyzeFile(path, observation with
            {
                Session = observation.Session with { ReferenceLog = observation.Session.ReferenceLog with { SizeBytesAfter = observation.Session.ReferenceLog.SizeBytesAfter - 1 } },
            }));
        });
    }

    private static void InvalidWindows()
    {
        foreach (string text in new[]
        {
            Header + Rows + Header + Rows,
            Header + Rows.Replace("2026-08-27 17:00:05", "2026-08-27 17:00:04", StringComparison.Ordinal),
            Header + Rows.Replace("2026-08-27 17:00:05", "2026-08-27 17:00:00", StringComparison.Ordinal),
            Header + Rows + "2026-08-27 17:00:11,1100,30,1110,30\n",
        })
        {
            WithLog(text, (path, observation) => Reject(() => CoolerGpuzReference.AnalyzeFile(path, observation)));
        }
    }

    private static void MissingValues()
    {
        WithLog(Header + Rows.Replace(",1090,", ",-,", StringComparison.Ordinal), (path, observation) =>
        {
            var range = CoolerGpuzReference.AnalyzeFile(path, observation).Channels[0];
            Check(range.SampleCount == 2 && range.MissingCount == 1 && range.Minimum == 1100, "missing RPM became zero");
        });
        WithLog(Header.Replace("Fan 1 Speed (RPM)", "Different Channel", StringComparison.Ordinal) + Rows, (path, observation) =>
        {
            var range = CoolerGpuzReference.AnalyzeFile(path, observation).Channels[0];
            Check(range.Status == "channel_unavailable" && range.SampleCount == 0 && range.Minimum is null, "absent channel invented");
        });
        WithLog(Header + Rows.Replace(",30,", ",-,", StringComparison.Ordinal), (path, observation) =>
        {
            var range = CoolerGpuzReference.AnalyzeFile(path, observation).Channels[1];
            Check(range.Status == "no_valid_samples" && range.Mean is null, "missing percentages became values");
        });
    }

    private static void InvalidValues()
    {
        foreach (string value in new[] { "NaN", "Infinity", "-1", "1e99" })
        {
            WithLog(Header + Rows.Replace(",1090,", $",{value},", StringComparison.Ordinal),
                (path, observation) => Reject(() => CoolerGpuzReference.AnalyzeFile(path, observation)));
        }
        WithLog(Header + Rows.Replace(",30,", ",101,", StringComparison.Ordinal),
            (path, observation) => Reject(() => CoolerGpuzReference.AnalyzeFile(path, observation)));
        WithLog(Header.Replace("Fan 2 Speed (RPM)", "Fan 1 Speed (RPM)", StringComparison.Ordinal) + Rows,
            (path, observation) => Reject(() => CoolerGpuzReference.AnalyzeFile(path, observation)));
    }

    private static void CliContract()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nvapi-cooler-status-v1-observation-v2.json");
        using var output = new StringWriter();
        using var error = new StringWriter();
        int result = LabCli.Run(["analyze-nvapi-cooler-status", "--input", fixture], output, error);
        Check(result == 0 && error.ToString().Length == 0, "CLI analysis failed");
        using var json = JsonDocument.Parse(output.ToString());
        Check(json.RootElement.GetProperty("mapping_status").GetString() == "raw_unknown", "CLI candidate promoted");
        WithLog(Header + Rows, (path, _) =>
        {
            JsonObject observation = JsonNode.Parse(File.ReadAllText(fixture))!.AsObject();
            string[] rows = Rows.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            observation["reference_log"]!["size_bytes_before"] = Encoding.UTF8.GetByteCount(Header + rows[0] + "\n");
            observation["reference_log"]!["size_bytes_midpoint"] = Encoding.UTF8.GetByteCount(Header + rows[0] + "\n" + rows[1] + "\n");
            observation["reference_log"]!["size_bytes_after"] = Encoding.UTF8.GetByteCount(Header + Rows);
            string observationPath = Path.Combine(Path.GetTempPath(), $"rtxmon-cooler-cli-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(observationPath, observation.ToJsonString(), new UTF8Encoding(false));
                output.GetStringBuilder().Clear();
                error.GetStringBuilder().Clear();
                int withReference = LabCli.Run(
                    ["analyze-nvapi-cooler-status", "--input", observationPath, "--gpuz-log", path], output, error);
                Check(withReference == 0 && error.ToString().Length == 0, "CLI reference analysis failed");
                using var referenceJson = JsonDocument.Parse(output.ToString());
                JsonElement emittedReference = referenceJson.RootElement.GetProperty("gpuz_reference");
                Check(emittedReference.GetProperty("window_sample_count").GetInt32() == 3 &&
                    emittedReference.GetProperty("channels")[0].GetProperty("minimum").GetDouble() == 1090,
                    "CLI must attach reference ranges from the recorded window");
                Check(referenceJson.RootElement.GetProperty("mapping_status").GetString() == "raw_unknown" &&
                    emittedReference.GetProperty("alignment_method").GetString() == "window_only_no_sample_pairing",
                    "CLI reference context cannot promote an untimed raw observation");
            }
            finally { File.Delete(observationPath); }
        });
        output.GetStringBuilder().Clear();
        result = LabCli.Run(["analyze-nvapi-cooler-status", "--input", fixture, "--unknown", "value"], output, error);
        Check(result == 2 && output.ToString().Length == 0 && error.ToString().Contains("invalid_arguments", StringComparison.Ordinal), "CLI argument error not structured");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (CoolerStatusAnalysisException) { return; }
        throw new InvalidOperationException("Expected invalid reference to be rejected.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
