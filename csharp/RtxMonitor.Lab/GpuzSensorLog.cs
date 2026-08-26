using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RtxMonitor.Lab;

public sealed record GpuzLogArtifact(
    string OriginalFileName,
    long SizeBytes,
    string Sha256,
    string TextEncoding);

public sealed record GpuzNumericStatistics(
    int SampleCount,
    double Minimum,
    double Maximum,
    double Mean,
    double StandardDeviation,
    double Latest);

public sealed record GpuzChannelAnalysis(
    int Index,
    string Name,
    string Unit,
    string SourceScope,
    string Category,
    string Representation,
    int SampleCount,
    int MissingCount,
    GpuzNumericStatistics? NumericStatistics,
    string LatestRaw,
    IReadOnlyList<string> DistinctRawValues);

public sealed record GpuzLogSample(
    int SessionIndex,
    string TimestampLocal,
    IReadOnlyList<string> Values);

public sealed record GpuzLogAnalysis(
    int SchemaVersion,
    string SourceKind,
    GpuzLogArtifact Artifact,
    int SampleCount,
    int SessionCount,
    string FirstTimestampLocal,
    string LastTimestampLocal,
    double? MedianIntervalMs,
    bool TimestampsHaveTimezone,
    IReadOnlyList<GpuzChannelAnalysis> Channels,
    IReadOnlyList<GpuzLogSample> Samples,
    IReadOnlyList<string> Warnings);

public sealed class GpuzLogException : Exception
{
    public GpuzLogException(string message)
        : base(message)
    {
    }

    public GpuzLogException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class GpuzSensorLog
{
    public const int SchemaVersion = 1;
    public const string SourceKind = "gpuz_sensor_log_reference";
    public const long MaximumInputSizeBytes = 16L * 1024 * 1024;
    public const int MaximumChannels = 256;
    public const int MaximumSamples = 250_000;

    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFF",
    ];

    private static readonly HashSet<string> KnownGpuBoardChannels = new(StringComparer.Ordinal)
    {
        "GPU Clock",
        "Memory Clock",
        "GPU Temperature",
        "Hot Spot",
        "Fan 1 Speed (%)",
        "Fan 1 Speed (RPM)",
        "Fan 2 Speed (%)",
        "Fan 2 Speed (RPM)",
        "Memory Used",
        "GPU Load",
        "Memory Controller Load",
        "Video Engine Load",
        "Bus Interface Load",
        "Board Power Draw",
        "GPU Chip Power Draw",
        "PWR_SRC Power Draw",
        "PWR_SRC Voltage",
        "PCIe Slot Power",
        "PCIe Slot Voltage",
        "8-Pin #1 Power",
        "8-Pin #1 Voltage",
        "Power Consumption (%)",
        "PerfCap Reason",
        "GPU Voltage",
    };

    public static GpuzLogAnalysis AnalyzeFile(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(inputPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new GpuzLogException("The GPU-Z log path is invalid.", error);
        }

        FileAttributes attributes = File.GetAttributes(resolvedPath);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new GpuzLogException("The GPU-Z log path must identify a regular file.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new GpuzLogException("A GPU-Z log cannot be a reparse point.");
        }

        using var stream = new FileStream(
            resolvedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumInputSizeBytes)
        {
            throw new GpuzLogException(
                $"The GPU-Z log exceeds the {MaximumInputSizeBytes}-byte limit.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.Position != stream.Length)
        {
            throw new GpuzLogException("The GPU-Z log changed while it was being read.");
        }

        string fileName = Path.GetFileName(resolvedPath);
        return Analyze(bytes, fileName);
    }

    public static GpuzLogAnalysis AnalyzeFilePrefix(string inputPath, long prefixLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (prefixLength < 1 || prefixLength > MaximumInputSizeBytes)
        {
            throw new GpuzLogException(
                $"The requested GPU-Z log prefix must be between 1 and " +
                $"{MaximumInputSizeBytes} bytes.");
        }

        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(inputPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new GpuzLogException("The GPU-Z log path is invalid.", error);
        }

        FileAttributes attributes = File.GetAttributes(resolvedPath);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new GpuzLogException("The GPU-Z log path must identify a regular file.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new GpuzLogException("A GPU-Z log cannot be a reparse point.");
        }

        using var stream = new FileStream(
            resolvedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length < prefixLength)
        {
            throw new GpuzLogException(
                $"The GPU-Z log is shorter than the {prefixLength}-byte captured prefix.");
        }

        byte[] bytes = new byte[checked((int)prefixLength)];
        stream.ReadExactly(bytes);
        return Analyze(bytes, Path.GetFileName(resolvedPath));
    }

    public static GpuzLogAnalysis Analyze(ReadOnlySpan<byte> content, string originalFileName)
    {
        if (content.Length == 0)
        {
            throw new GpuzLogException("The GPU-Z log is empty.");
        }

        if (content.Length > MaximumInputSizeBytes)
        {
            throw new GpuzLogException(
                $"The GPU-Z log exceeds the {MaximumInputSizeBytes}-byte limit.");
        }

        if (content.Contains((byte)0))
        {
            throw new GpuzLogException("The GPU-Z log contains a NUL byte.");
        }

        (string text, string encoding) = DecodeText(content);
        IReadOnlyList<IReadOnlyList<string>> rows = ParseCsv(text);
        if (rows.Count < 2)
        {
            throw new GpuzLogException(
                "The GPU-Z log must contain a header and at least one sample.");
        }

        IReadOnlyList<string> header = TrimTrailingEmptyField(rows[0]);
        if (header.Count < 2 ||
            !string.Equals(header[0].Trim(), "Date", StringComparison.Ordinal))
        {
            throw new GpuzLogException("The first GPU-Z log column must be 'Date'.");
        }

        int channelCount = header.Count - 1;
        if (channelCount > MaximumChannels)
        {
            throw new GpuzLogException(
                $"The GPU-Z log contains more than {MaximumChannels} channels.");
        }

        var channelHeaders = new List<(string Name, string Unit)>(channelCount);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 1; index < header.Count; index++)
        {
            (string name, string unit) = ParseHeader(header[index]);
            if (!names.Add(name))
            {
                throw new GpuzLogException($"The GPU-Z log repeats channel '{name}'.");
            }

            channelHeaders.Add((name, unit));
        }

        int sampleCapacity = rows.Count - 1;
        if (sampleCapacity > MaximumSamples)
        {
            throw new GpuzLogException(
                $"The GPU-Z log contains more than {MaximumSamples} samples.");
        }

        var samples = new List<GpuzLogSample>(sampleCapacity);
        var timestamps = new List<DateTime>(sampleCapacity);
        var timestampSessions = new List<int>(sampleCapacity);
        int repeatedHeaderCount = 0;
        int currentSessionIndex = 0;
        var channelValues = Enumerable.Range(0, channelCount)
            .Select(_ => new List<string>(sampleCapacity))
            .ToArray();

        for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            IReadOnlyList<string> row = TrimTrailingEmptyField(rows[rowIndex]);
            if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0]))
            {
                continue;
            }

            if (string.Equals(row[0].Trim(), "Date", StringComparison.Ordinal))
            {
                ValidateRepeatedHeader(row, header.Count, channelHeaders, rowIndex + 1);
                repeatedHeaderCount++;
                currentSessionIndex++;
                continue;
            }

            if (row.Count != header.Count)
            {
                throw new GpuzLogException(
                    $"GPU-Z log row {rowIndex + 1} has {row.Count} fields; " +
                    $"expected {header.Count}.");
            }

            string timestampRaw = row[0].Trim();
            if (!DateTime.TryParseExact(
                    timestampRaw,
                    TimestampFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime timestamp))
            {
                throw new GpuzLogException(
                    $"GPU-Z log row {rowIndex + 1} has an unsupported local timestamp.");
            }

            timestamps.Add(DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified));
            timestampSessions.Add(currentSessionIndex);
            var values = new string[channelCount];
            for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
            {
                string raw = row[channelIndex + 1].Trim();
                values[channelIndex] = raw;
                channelValues[channelIndex].Add(raw);
            }

            samples.Add(new GpuzLogSample(currentSessionIndex, timestampRaw, values));
        }

        if (samples.Count == 0)
        {
            throw new GpuzLogException("The GPU-Z log contains no samples.");
        }

        var warnings = new List<string>
        {
            "GPU-Z is an external software reference; this report does not identify the underlying NVIDIA interface or physical sensor.",
            "GPU-Z timestamps have no UTC offset; they are preserved as local wall-clock values.",
        };
        if (Enumerable.Range(1, timestamps.Count - 1).Any(index =>
                timestampSessions[index] == timestampSessions[index - 1] &&
                timestamps[index] <= timestamps[index - 1]))
        {
            warnings.Add("One or more timestamps are duplicated or out of order.");
        }

        if (channelHeaders.Any(channel => channel.Name == "PerfCap Reason"))
        {
            warnings.Add("PerfCap Reason is preserved as the raw code written by GPU-Z; no label is inferred.");
        }

        if (channelHeaders.Any(channel => ClassifyScope(channel.Name) == "host_system"))
        {
            warnings.Add("Host-system channels are separated from GPU/board channels.");
        }

        if (repeatedHeaderCount > 0)
        {
            warnings.Add(
                $"The log contains {repeatedHeaderCount + 1} appended GPU-Z sessions with identical channel layouts.");
        }

        var channels = new List<GpuzChannelAnalysis>(channelCount);
        for (int index = 0; index < channelCount; index++)
        {
            (string name, string unit) = channelHeaders[index];
            channels.Add(AnalyzeChannel(index, name, unit, channelValues[index]));
        }

        byte[] hash = SHA256.HashData(content);
        var artifact = new GpuzLogArtifact(
            NormalizeFileName(originalFileName),
            content.Length,
            Convert.ToHexString(hash).ToLowerInvariant(),
            encoding);

        return new GpuzLogAnalysis(
            SchemaVersion,
            SourceKind,
            artifact,
            samples.Count,
            repeatedHeaderCount + 1,
            samples[0].TimestampLocal,
            samples[^1].TimestampLocal,
            MedianIntervalMilliseconds(timestamps, timestampSessions),
            TimestampsHaveTimezone: false,
            channels,
            samples,
            warnings);
    }

    private static void ValidateRepeatedHeader(
        IReadOnlyList<string> row,
        int expectedFieldCount,
        IReadOnlyList<(string Name, string Unit)> expectedChannels,
        int rowNumber)
    {
        if (row.Count != expectedFieldCount)
        {
            throw new GpuzLogException(
                $"Repeated GPU-Z header at row {rowNumber} has {row.Count} fields; " +
                $"expected {expectedFieldCount}.");
        }

        for (int index = 1; index < row.Count; index++)
        {
            (string name, string unit) = ParseHeader(row[index]);
            (string expectedName, string expectedUnit) = expectedChannels[index - 1];
            if (!string.Equals(name, expectedName, StringComparison.Ordinal) ||
                !string.Equals(unit, expectedUnit, StringComparison.Ordinal))
            {
                throw new GpuzLogException(
                    $"Repeated GPU-Z header at row {rowNumber} changes channel {index} " +
                    $"from '{expectedName} [{expectedUnit}]' to '{name} [{unit}]'.");
            }
        }
    }

    private static GpuzChannelAnalysis AnalyzeChannel(
        int index,
        string name,
        string unit,
        IReadOnlyList<string> values)
    {
        string[] present = values.Where(value => value.Length > 0).ToArray();
        string representation = name == "PerfCap Reason" ? "raw_code" : "numeric";
        var numeric = new List<double>(present.Length);
        foreach (string value in present)
        {
            if (!double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double parsed) ||
                !double.IsFinite(parsed))
            {
                representation = "text";
                numeric.Clear();
                break;
            }

            numeric.Add(parsed);
        }

        GpuzNumericStatistics? statistics = representation == "text" || numeric.Count == 0
            ? null
            : CalculateStatistics(numeric);
        string[] distinct = present
            .Distinct(StringComparer.Ordinal)
            .Take(128)
            .ToArray();

        return new GpuzChannelAnalysis(
            index,
            name,
            unit,
            ClassifyScope(name),
            ClassifyCategory(name),
            representation,
            present.Length,
            values.Count - present.Length,
            statistics,
            values[^1],
            distinct);
    }

    private static GpuzNumericStatistics CalculateStatistics(IReadOnlyList<double> values)
    {
        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        double mean = 0;
        double m2 = 0;
        for (int index = 0; index < values.Count; index++)
        {
            double value = values[index];
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            double delta = value - mean;
            mean += delta / (index + 1);
            m2 += delta * (value - mean);
        }

        return new GpuzNumericStatistics(
            values.Count,
            minimum,
            maximum,
            mean,
            Math.Sqrt(m2 / values.Count),
            values[^1]);
    }

    private static double? MedianIntervalMilliseconds(
        IReadOnlyList<DateTime> timestamps,
        IReadOnlyList<int> sessions)
    {
        if (timestamps.Count < 2)
        {
            return null;
        }

        double[] intervals = Enumerable.Range(1, timestamps.Count - 1)
            .Where(index => sessions[index] == sessions[index - 1])
            .Select(index => (timestamps[index] - timestamps[index - 1]).TotalMilliseconds)
            .OrderBy(value => value)
            .ToArray();
        if (intervals.Length == 0)
        {
            return null;
        }
        int middle = intervals.Length / 2;
        return intervals.Length % 2 == 0
            ? (intervals[middle - 1] + intervals[middle]) / 2
            : intervals[middle];
    }

    private static (string Text, string Encoding) DecodeText(ReadOnlySpan<byte> content)
    {
        byte[] bytes = content.ToArray();
        try
        {
            var utf8 = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);
            string text = utf8.GetString(bytes);
            return (text.TrimStart('\uFEFF'), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), "iso-8859-1-fallback");
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseCsv(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    CompleteRow(rows, row, field);
                    break;
                case '\n':
                    CompleteRow(rows, row, field);
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (quoted)
        {
            throw new GpuzLogException("The GPU-Z log contains an unterminated CSV quote.");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            CompleteRow(rows, row, field);
        }

        return rows;
    }

    private static void CompleteRow(
        ICollection<IReadOnlyList<string>> rows,
        ICollection<string> row,
        StringBuilder field)
    {
        row.Add(field.ToString());
        field.Clear();
        rows.Add(row.ToArray());
        row.Clear();
    }

    private static IReadOnlyList<string> TrimTrailingEmptyField(IReadOnlyList<string> row)
    {
        if (row.Count > 0 && string.IsNullOrWhiteSpace(row[^1]))
        {
            return row.Take(row.Count - 1).ToArray();
        }

        return row;
    }

    private static (string Name, string Unit) ParseHeader(string rawHeader)
    {
        string header = rawHeader.Trim();
        int unitStart = header.LastIndexOf(" [", StringComparison.Ordinal);
        if (unitStart <= 0 || !header.EndsWith(']'))
        {
            throw new GpuzLogException(
                $"GPU-Z channel header '{header}' does not contain a trailing unit.");
        }

        string name = header[..unitStart].Trim();
        string unit = header[(unitStart + 2)..^1].Trim();
        if (name.Length == 0)
        {
            throw new GpuzLogException("A GPU-Z channel has an empty name.");
        }

        return (name, unit);
    }

    private static string ClassifyScope(string name)
    {
        if (name is "CPU Temperature" or "System Memory Used")
        {
            return "host_system";
        }

        return KnownGpuBoardChannels.Contains(name) ? "gpu_board" : "unknown";
    }

    private static string ClassifyCategory(string name)
    {
        if (name.Contains("Temperature", StringComparison.Ordinal) || name == "Hot Spot")
        {
            return "temperature";
        }

        if (name.Contains("Clock", StringComparison.Ordinal))
        {
            return "clock";
        }

        if (name.StartsWith("Fan ", StringComparison.Ordinal))
        {
            return "fan";
        }

        if (name.Contains("Voltage", StringComparison.Ordinal))
        {
            return "voltage";
        }

        if (name.Contains("Power", StringComparison.Ordinal))
        {
            return "power";
        }

        if (name.Contains("Load", StringComparison.Ordinal))
        {
            return "utilization";
        }

        if (name.Contains("Memory", StringComparison.Ordinal))
        {
            return "memory";
        }

        if (name == "PerfCap Reason")
        {
            return "performance_limiter";
        }

        return "other";
    }

    private static string NormalizeFileName(string value)
    {
        string fileName = Path.GetFileName(value ?? string.Empty).Normalize(NormalizationForm.FormC);
        if (fileName.Length == 0 || fileName.Length > 255 ||
            fileName.Any(character => char.IsControl(character)))
        {
            throw new GpuzLogException("The original GPU-Z log file name is invalid.");
        }

        return fileName;
    }
}
