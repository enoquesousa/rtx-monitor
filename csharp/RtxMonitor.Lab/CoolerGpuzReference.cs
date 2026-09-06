using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RtxMonitor.Lab;

public sealed record CoolerGpuzReferenceContext(
    string SourceKind,
    string IntegrityStatus,
    string AlignmentMethod,
    string OriginalFileName,
    long PrefixSizeBytes,
    string PrefixSha256,
    int SelectedSessionIndex,
    string WindowFirstTimestampLocal,
    string WindowLastTimestampLocal,
    int WindowSampleCount,
    IReadOnlyList<CoolerGpuzChannelRange> Channels,
    IReadOnlyList<string> Warnings);

public sealed record CoolerGpuzChannelRange(
    string Name,
    string Unit,
    string Status,
    int SampleCount,
    int MissingCount,
    double? Minimum,
    double? Maximum,
    double? Mean);

/// <summary>
/// Window context for legacy observations without individual timestamps or a sealed reference.
/// Values are never paired to raw words and cannot promote a candidate or identify a fan.
/// </summary>
public static class CoolerGpuzReference
{
    private static readonly (string Name, string Unit)[] FanChannels =
    [
        ("Fan 1 Speed (RPM)", "RPM"), ("Fan 1 Speed (%)", "%"),
        ("Fan 2 Speed (RPM)", "RPM"), ("Fan 2 Speed (%)", "%"),
    ];
    private static readonly string[] TimestampFormats =
        ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFF"];

    public static CoolerGpuzReferenceContext AnalyzeFile(
        string inputPath,
        CoolerStatusAnalysisReport observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        CoolerStatusReferenceLog reference = observation.Session.ReferenceLog;
        long prefixLength = reference.SizeBytesAfter;
        if (prefixLength < 1 || prefixLength > GpuzSensorLog.MaximumInputSizeBytes)
        {
            throw new CoolerStatusAnalysisException("The recorded GPU-Z prefix exceeds the 16 MiB analysis limit.");
        }

        string path;
        try { path = Path.GetFullPath(inputPath); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new CoolerStatusAnalysisException("The GPU-Z reference path is invalid.", error);
        }
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path[Path.GetPathRoot(path)!.Length..].Contains(':') ||
            (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new CoolerStatusAnalysisException("The GPU-Z reference must be a regular local file.");
        }

        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan))
        {
            if (stream.Length < prefixLength)
            {
                throw new CoolerStatusAnalysisException("The GPU-Z log is shorter than the recorded prefix.");
            }
            bytes = new byte[checked((int)prefixLength)];
            stream.ReadExactly(bytes);
        }
        if (bytes[^1] != (byte)'\n')
        {
            throw new CoolerStatusAnalysisException("The recorded GPU-Z prefix must end at a complete LF-terminated row.");
        }

        DateTime first = Timestamp(reference.LastSampleLocalBefore);
        DateTime middle = Timestamp(reference.LastSampleLocalMidpoint);
        DateTime last = Timestamp(reference.LastSampleLocalAfter);
        if (!(first < middle && middle < last))
        {
            throw new CoolerStatusAnalysisException("GPU-Z reference boundaries must increase strictly.");
        }

        int[] offsets = SessionOffsets(bytes);
        var candidates = new List<(int Index, GpuzLogAnalysis Analysis, GpuzLogSample[] Window)>();
        for (int index = 0; index < offsets.Length; index++)
        {
            int end = index + 1 < offsets.Length ? offsets[index + 1] : bytes.Length;
            GpuzLogAnalysis analysis;
            try
            {
                analysis = GpuzSensorLog.Analyze(bytes.AsSpan(offsets[index], end - offsets[index]), Path.GetFileName(path));
            }
            catch (GpuzLogException error)
            {
                throw new CoolerStatusAnalysisException($"GPU-Z session {index} is malformed.", error);
            }

            DateTime[] times = analysis.Samples.Select(sample => Timestamp(sample.TimestampLocal)).ToArray();
            DateTime[] windowTimes = times.Where(time => time >= first && time <= last).ToArray();
            if (windowTimes.Length == 0) { continue; }
            if (windowTimes.Zip(windowTimes.Skip(1), (previous, current) => current <= previous).Any(value => value))
            {
                throw new CoolerStatusAnalysisException("The selected GPU-Z window has duplicate or unordered timestamps.");
            }
            if (!times.Contains(first) || !times.Contains(middle) || !times.Contains(last))
            {
                throw new CoolerStatusAnalysisException("A GPU-Z session overlaps the window but does not contain all three recorded boundaries.");
            }
            candidates.Add((index, analysis, analysis.Samples.Where((_, i) => times[i] >= first && times[i] <= last).ToArray()));
        }
        if (candidates.Count != 1 || candidates[0].Index != offsets.Length - 1 ||
            Timestamp(candidates[0].Analysis.Samples[^1].TimestampLocal) != last)
        {
            throw new CoolerStatusAnalysisException("The bounded GPU-Z prefix must end in exactly one session matching the recorded window.");
        }

        (int selectedIndex, GpuzLogAnalysis selected, GpuzLogSample[] window) = candidates[0];
        var ranges = new List<CoolerGpuzChannelRange>();
        foreach ((string name, string unit) in FanChannels)
        {
            GpuzChannelAnalysis[] matches = selected.Channels.Where(channel => channel.Name == name && channel.Unit == unit).ToArray();
            if (matches.Length > 1)
            {
                throw new CoolerStatusAnalysisException($"GPU-Z contains an ambiguous duplicate '{name} [{unit}]' channel.");
            }
            if (matches.Length == 0)
            {
                ranges.Add(new(name, unit, "channel_unavailable", 0, window.Length, null, null, null));
                continue;
            }
            var values = new List<double>();
            foreach (GpuzLogSample sample in window)
            {
                string raw = sample.Values[matches[0].Index].Trim();
                if (raw is "" or "-") { continue; }
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                    !double.IsFinite(value) || value < 0 || value > (unit == "%" ? 100 : uint.MaxValue))
                {
                    throw new CoolerStatusAnalysisException($"GPU-Z channel '{name}' contains an invalid non-missing value.");
                }
                values.Add(value);
            }
            ranges.Add(new(name, unit, values.Count == 0 ? "no_valid_samples" : "available",
                values.Count, window.Length - values.Count,
                values.Count == 0 ? null : values.Min(), values.Count == 0 ? null : values.Max(),
                values.Count == 0 ? null : values.Average()));
        }

        return new("gpuz_cooler_window_context", "unsealed_historical_reference", "window_only_no_sample_pairing",
            Path.GetFileName(path), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            selectedIndex, reference.LastSampleLocalBefore, reference.LastSampleLocalAfter, window.Length, ranges,
            [
                "The prefix hash is computed during this analysis; the legacy observation does not contain a historical reference hash.",
                "Only local wall-clock window boundaries are available. No individual raw sample is paired to a reference row.",
                "Similar RPM ranges or equal fan percentages do not identify an entry, a physical fan, PWM, or a validated sensor.",
            ]);
    }

    private static DateTime Timestamp(string value)
    {
        if (!DateTime.TryParseExact(value, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time))
        {
            throw new CoolerStatusAnalysisException("A GPU-Z local timestamp is invalid.");
        }
        return DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
    }

    private static int[] SessionOffsets(ReadOnlySpan<byte> bytes)
    {
        var offsets = new List<int>();
        int start = 0;
        while (start < bytes.Length)
        {
            int newline = bytes[start..].IndexOf((byte)'\n');
            if (newline < 0) { break; }
            ReadOnlySpan<byte> line = bytes.Slice(start, newline);
            if (start == 0 && line.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { line = line[3..]; }
            int comma = line.IndexOf((byte)',');
            if (comma >= 0 && Encoding.Latin1.GetString(line[..comma]).Trim().Trim('"') == "Date")
            {
                if (offsets.Count >= GpuzSensorLog.MaximumSessions)
                {
                    throw new CoolerStatusAnalysisException("The GPU-Z prefix contains too many appended sessions.");
                }
                offsets.Add(start);
            }
            start += newline + 1;
        }
        if (offsets.Count == 0 || offsets[0] != 0)
        {
            throw new CoolerStatusAnalysisException("The GPU-Z prefix does not start with a session header.");
        }
        return offsets.ToArray();
    }
}
