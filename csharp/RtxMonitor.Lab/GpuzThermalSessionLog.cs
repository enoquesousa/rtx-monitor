using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RtxMonitor.Lab;

internal sealed record GpuzThermalPrefixArtifact(
    string OriginalFileName,
    long SizeBytes,
    string Sha256);

internal sealed record GpuzThermalPoint(
    DateTime Timestamp,
    double GpuTemperatureCelsius,
    double HotSpotCelsius);

internal sealed record GpuzThermalSession(
    int SessionIndex,
    IReadOnlyList<GpuzThermalPoint> Samples);

internal sealed record GpuzThermalPrefixAnalysis(
    GpuzThermalPrefixArtifact Artifact,
    IReadOnlyList<GpuzThermalSession> Sessions,
    IReadOnlyList<int> IgnoredSessionIndicesWithoutExactChannels,
    IReadOnlyList<int> RejectedSessionIndicesWithInvalidExactChannelData);

internal sealed class GpuzThermalPrefixException : Exception
{
    internal GpuzThermalPrefixException(string message) : base(message) { }

    internal GpuzThermalPrefixException(string message, Exception innerException)
        : base(message, innerException) { }
}

internal static class GpuzThermalSessionLog
{
    private const int MaximumSessions = 1_024;
    private const double MinimumTemperatureCelsius = -100.0;
    private const double MaximumTemperatureCelsius = 300.0;
    private static readonly string[] TimestampFormats =
        ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFF"];

    internal static GpuzThermalPrefixAnalysis AnalyzeFilePrefix(
        string inputPath,
        long prefixLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (prefixLength < 1 || prefixLength > GpuzSensorLog.MaximumInputSizeBytes)
        {
            throw new GpuzThermalPrefixException(
                "The bounded GPU-Z thermal prefix is outside the analysis limit.");
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(inputPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new GpuzThermalPrefixException("The GPU-Z log path is invalid.", error);
        }

        FileAttributes attributes = File.GetAttributes(resolved);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new GpuzThermalPrefixException(
                "The GPU-Z log must be a regular local file and cannot be a reparse point.");
        }

        byte[] bytes;
        using (var stream = new FileStream(
            resolved,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan))
        {
            if (stream.Length < prefixLength)
            {
                throw new GpuzThermalPrefixException(
                    "The GPU-Z log is shorter than its recorded bounded prefix.");
            }

            bytes = new byte[checked((int)prefixLength)];
            stream.ReadExactly(bytes);
        }

        if (bytes[^1] != (byte)'\n')
        {
            throw new GpuzThermalPrefixException(
                "The GPU-Z prefix does not end at a complete LF-terminated CSV row.");
        }

        int[] offsets = FindSessionOffsets(bytes);
        if (offsets.Length is < 1 or > MaximumSessions || offsets[0] != 0)
        {
            throw new GpuzThermalPrefixException(
                "The GPU-Z prefix does not contain a bounded set of complete sessions.");
        }

        var sessions = new List<GpuzThermalSession>(offsets.Length);
        var ignored = new List<int>();
        var rejected = new List<int>();
        for (int sessionIndex = 0; sessionIndex < offsets.Length; sessionIndex++)
        {
            int start = offsets[sessionIndex];
            int end = sessionIndex + 1 < offsets.Length
                ? offsets[sessionIndex + 1]
                : bytes.Length;
            GpuzLogAnalysis parsed;
            try
            {
                parsed = GpuzSensorLog.Analyze(
                    bytes.AsSpan(start, end - start),
                    Path.GetFileName(resolved));
            }
            catch (GpuzLogException error)
            {
                throw new GpuzThermalPrefixException(
                    $"GPU-Z session {sessionIndex} is malformed.",
                    error);
            }

            GpuzChannelAnalysis? gpuTemperature = FindExactChannel(
                parsed,
                "GPU Temperature",
                "°C");
            GpuzChannelAnalysis? hotSpot = FindExactChannel(parsed, "Hot Spot", "°C");
            if (gpuTemperature is null || hotSpot is null)
            {
                ignored.Add(sessionIndex);
                continue;
            }

            GpuzThermalPoint[] points;
            try
            {
                points = parsed.Samples
                    .Select(sample => new GpuzThermalPoint(
                        ParseTimestamp(sample.TimestampLocal),
                        ParseTemperature(
                            sample.Values[gpuTemperature.Index],
                            sessionIndex,
                            "GPU Temperature"),
                        ParseTemperature(
                            sample.Values[hotSpot.Index],
                            sessionIndex,
                            "Hot Spot")))
                    .ToArray();
            }
            catch (GpuzThermalPrefixException)
            {
                rejected.Add(sessionIndex);
                continue;
            }

            sessions.Add(new GpuzThermalSession(sessionIndex, points));
        }

        return new GpuzThermalPrefixAnalysis(
            new GpuzThermalPrefixArtifact(
                Path.GetFileName(resolved),
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            sessions,
            ignored,
            rejected);
    }

    private static GpuzChannelAnalysis? FindExactChannel(
        GpuzLogAnalysis analysis,
        string name,
        string unit)
    {
        GpuzChannelAnalysis[] matches = analysis.Channels
            .Where(channel =>
                string.Equals(channel.Name, name, StringComparison.Ordinal) &&
                string.Equals(channel.Unit, unit, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static int[] FindSessionOffsets(ReadOnlySpan<byte> bytes)
    {
        var offsets = new List<int>();
        int lineStart = 0;
        while (lineStart < bytes.Length)
        {
            int relativeEnd = bytes[lineStart..].IndexOf((byte)'\n');
            if (relativeEnd < 0)
            {
                break;
            }

            int lineEnd = lineStart + relativeEnd;
            ReadOnlySpan<byte> line = bytes[lineStart..lineEnd];
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            ReadOnlySpan<byte> normalized = line;
            if (lineStart == 0 && normalized.Length >= 3 &&
                normalized[0] == 0xef && normalized[1] == 0xbb &&
                normalized[2] == 0xbf)
            {
                normalized = normalized[3..];
            }

            int comma = normalized.IndexOf((byte)',');
            if (comma >= 0)
            {
                string firstField = Encoding.Latin1.GetString(normalized[..comma])
                    .Trim()
                    .Trim('"');
                if (firstField == "Date")
                {
                    offsets.Add(lineStart);
                }
            }

            lineStart = lineEnd + 1;
        }

        return offsets.ToArray();
    }

    private static DateTime ParseTimestamp(string text)
    {
        if (!DateTime.TryParseExact(
                text,
                TimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime value))
        {
            throw new GpuzThermalPrefixException(
                "A GPU-Z thermal timestamp is invalid.");
        }

        return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }

    private static double ParseTemperature(
        string text,
        int sessionIndex,
        string channel)
    {
        if (!double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value) ||
            !double.IsFinite(value) ||
            value < MinimumTemperatureCelsius ||
            value > MaximumTemperatureCelsius)
        {
            throw new GpuzThermalPrefixException(
                $"GPU-Z session {sessionIndex} contains an invalid {channel} value.");
        }

        return value;
    }
}
