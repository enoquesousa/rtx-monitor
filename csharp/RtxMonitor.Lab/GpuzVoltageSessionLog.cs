using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RtxMonitor.Lab;

internal static class VoltageReferenceRange
{
    internal const double MinimumVolts = 0.1;
    internal const double MaximumVolts = 2.0;
}

internal sealed record GpuzVoltagePrefixArtifact(
    string OriginalFileName,
    long SizeBytes,
    string Sha256);

internal sealed record GpuzVoltagePoint(
    DateTime Timestamp,
    double VoltageVolts);

internal sealed record GpuzVoltageSession(
    int SessionIndex,
    IReadOnlyList<GpuzVoltagePoint> Samples,
    IReadOnlyList<DateTime> InvalidSampleTimestamps);

internal sealed record GpuzVoltagePrefixAnalysis(
    GpuzVoltagePrefixArtifact Artifact,
    IReadOnlyList<GpuzVoltageSession> Sessions);

internal sealed class GpuzVoltagePrefixException : Exception
{
    internal GpuzVoltagePrefixException(string message) : base(message) { }
    internal GpuzVoltagePrefixException(string message, Exception inner) : base(message, inner) { }
}

internal static class GpuzVoltageSessionLog
{
    private const int MaximumSessions = 1_024;
    private static readonly string[] TimestampFormats =
        ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFF"];

    internal static GpuzVoltagePrefixAnalysis AnalyzeFilePrefix(
        string inputPath,
        long prefixLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (prefixLength < 1 || prefixLength > GpuzSensorLog.MaximumInputSizeBytes)
        {
            throw new GpuzVoltagePrefixException(
                "The bounded GPU-Z voltage prefix is outside the analysis limit.");
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(inputPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new GpuzVoltagePrefixException("The GPU-Z log path is invalid.", error);
        }

        FileAttributes attributes = File.GetAttributes(resolved);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new GpuzVoltagePrefixException(
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
                throw new GpuzVoltagePrefixException(
                    "The GPU-Z log is shorter than its recorded bounded prefix.");
            }

            bytes = new byte[checked((int)prefixLength)];
            stream.ReadExactly(bytes);
        }

        if (bytes[^1] != (byte)'\n')
        {
            throw new GpuzVoltagePrefixException(
                "The GPU-Z prefix does not end at a complete LF-terminated CSV row.");
        }

        int[] offsets = FindSessionOffsets(bytes);
        if (offsets.Length is < 1 or > MaximumSessions || offsets[0] != 0)
        {
            throw new GpuzVoltagePrefixException(
                "The GPU-Z prefix does not contain a bounded set of complete sessions.");
        }

        var sessions = new List<GpuzVoltageSession>(offsets.Length);
        for (int index = 0; index < offsets.Length; index++)
        {
            int start = offsets[index];
            int end = index + 1 < offsets.Length ? offsets[index + 1] : bytes.Length;
            ReadOnlySpan<byte> sessionBytes = bytes.AsSpan(start, end - start);
            GpuzLogAnalysis parsed;
            try
            {
                parsed = GpuzSensorLog.Analyze(
                    sessionBytes,
                    Path.GetFileName(resolved));
            }
            catch (GpuzLogException error)
            {
                throw new GpuzVoltagePrefixException(
                    $"GPU-Z session {index} is malformed.",
                    error);
            }

            GpuzChannelAnalysis[] voltageChannels = parsed.Channels
                .Where(channel => channel.Name == "GPU Voltage" && channel.Unit == "V")
                .ToArray();
            if (voltageChannels.Length == 0)
            {
                // Appended GPU-Z logs can legitimately contain older sessions with a
                // different sensor layout. They are not candidates for this correlation.
                continue;
            }
            if (voltageChannels.Length != 1)
            {
                throw new GpuzVoltagePrefixException(
                    $"GPU-Z session {index} contains more than one exact 'GPU Voltage [V]' channel.");
            }

            GpuzChannelAnalysis voltage = voltageChannels[0];
            var points = new List<GpuzVoltagePoint>(parsed.Samples.Count);
            var invalidSampleTimestamps = new List<DateTime>();
            foreach (var sample in parsed.Samples)
            {
                DateTime timestamp = ParseTimestamp(sample.TimestampLocal);
                double? value;
                try
                {
                    value = ParseOptionalVoltage(
                        sample.Values[voltage.Index],
                        index);
                }
                catch (GpuzVoltagePrefixException)
                {
                    invalidSampleTimestamps.Add(timestamp);
                    continue;
                }

                if (value is not null)
                {
                    points.Add(new GpuzVoltagePoint(timestamp, value.Value));
                }
            }

            sessions.Add(new GpuzVoltageSession(
                index,
                points,
                invalidSampleTimestamps));
        }

        if (sessions.Count == 0)
        {
            throw new GpuzVoltagePrefixException(
                "The GPU-Z prefix has no session with an exact 'GPU Voltage [V]' channel.");
        }

        return new GpuzVoltagePrefixAnalysis(
            new GpuzVoltagePrefixArtifact(
                Path.GetFileName(resolved),
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            sessions);
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
            throw new GpuzVoltagePrefixException(
                "A GPU-Z voltage timestamp is invalid.");
        }

        return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }

    private static double? ParseOptionalVoltage(string text, int sessionIndex)
    {
        if (text.Length == 0 || text == "-")
        {
            return null;
        }

        if (!double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value) ||
            !double.IsFinite(value) ||
            value < VoltageReferenceRange.MinimumVolts ||
            value > VoltageReferenceRange.MaximumVolts)
        {
            throw new GpuzVoltagePrefixException(
                $"GPU-Z session {sessionIndex} contains an invalid non-missing " +
                "'GPU Voltage [V]' value outside the supported 0.1-2.0 V range.");
        }

        return value;
    }
}
