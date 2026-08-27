using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RtxMonitor.Lab;

public sealed record HwinfoVoltageLogArtifact(
    string OriginalFileName,
    long SizeBytes,
    string Sha256,
    string TextEncoding);

public sealed record HwinfoVoltageLogSample(
    string TimestampLocal,
    DateTime ParsedTimestampLocal,
    double VoltageVolts);

public sealed record HwinfoVoltageLogAnalysis(
    HwinfoVoltageLogArtifact Artifact,
    string ReferenceChannel,
    IReadOnlyList<HwinfoVoltageLogSample> Samples);

public sealed class HwinfoVoltageLogException : Exception
{
    public HwinfoVoltageLogException(string message) : base(message) { }
    public HwinfoVoltageLogException(string message, Exception inner) : base(message, inner) { }
}

public static class HwinfoVoltageSensorLog
{
    public const long MaximumInputSizeBytes = 64L * 1024 * 1024;
    public const int MaximumSamples = 250_000;
    public const int MaximumSessions = 1_024;
    public const int MaximumFields = 1_024;
    public const string ExpectedReferenceChannel = "GPU Core Voltage [V]";

    private static readonly string[] TimestampFormats =
    [
        "d.M.yyyy H:m:ss.FFF",
        "d.M.yyyy H:m:ss",
    ];

    public static HwinfoVoltageLogAnalysis AnalyzeFilePrefix(string inputPath, long prefixLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (prefixLength < 1 || prefixLength > MaximumInputSizeBytes)
        {
            throw new HwinfoVoltageLogException(
                $"The requested HWiNFO log prefix must be between 1 and {MaximumInputSizeBytes} bytes.");
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(inputPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new HwinfoVoltageLogException("The HWiNFO log path is invalid.", error);
        }

        FileAttributes attributes = File.GetAttributes(resolved);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new HwinfoVoltageLogException(
                "The HWiNFO log must be a regular local file and cannot be a reparse point.");
        }

        using var stream = new FileStream(
            resolved,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length < prefixLength)
        {
            throw new HwinfoVoltageLogException(
                $"The HWiNFO log is shorter than the {prefixLength}-byte captured prefix.");
        }

        byte[] bytes = new byte[checked((int)prefixLength)];
        stream.ReadExactly(bytes);
        return Analyze(bytes, Path.GetFileName(resolved));
    }

    public static HwinfoVoltageLogAnalysis Analyze(
        ReadOnlySpan<byte> content,
        string originalFileName)
    {
        if (content.Length is < 1 or > (int)MaximumInputSizeBytes)
        {
            throw new HwinfoVoltageLogException("The HWiNFO log size is outside the analysis limit.");
        }
        if (content.Contains((byte)0))
        {
            throw new HwinfoVoltageLogException("The HWiNFO log contains a NUL byte.");
        }

        (string text, string encoding) = Decode(content);
        (IReadOnlyList<IReadOnlyList<string>> Rows, int SampleRowCount) parsedCsv =
            ParseCsv(text);
        IReadOnlyList<IReadOnlyList<string>> rows = parsedCsv.Rows;
        if (rows.Count < 2)
        {
            throw new HwinfoVoltageLogException("The HWiNFO log must contain a header and samples.");
        }

        IReadOnlyList<string> header = rows[0];
        int dateIndex = SingleHeaderIndex(header, "Date");
        int timeIndex = SingleHeaderIndex(header, "Time");
        int voltageIndex = SingleHeaderIndex(header, ExpectedReferenceChannel);
        var samples = new List<HwinfoVoltageLogSample>(parsedCsv.SampleRowCount);

        for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            IReadOnlyList<string> row = rows[rowIndex];
            if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0]))
            {
                continue;
            }
            if (row.Count != header.Count)
            {
                throw new HwinfoVoltageLogException(
                    $"HWiNFO row {rowIndex + 1} has {row.Count} fields; expected {header.Count}.");
            }
            if (row[dateIndex] == "Date" && row[timeIndex] == "Time")
            {
                if (!row.SequenceEqual(header, StringComparer.Ordinal))
                {
                    throw new HwinfoVoltageLogException(
                        "An appended HWiNFO session changed the channel layout.");
                }
                continue;
            }

            string timestampText = $"{row[dateIndex]} {row[timeIndex]}";
            if (!DateTime.TryParseExact(
                    timestampText,
                    TimestampFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime timestamp))
            {
                throw new HwinfoVoltageLogException(
                    $"HWiNFO row {rowIndex + 1} has an invalid local timestamp.");
            }

            string rawVoltage = row[voltageIndex].Trim();
            if (rawVoltage is "" or "-")
            {
                continue;
            }
            if (!double.TryParse(
                    rawVoltage,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double voltage) ||
                !double.IsFinite(voltage) ||
                voltage < VoltageReferenceRange.MinimumVolts ||
                voltage > VoltageReferenceRange.MaximumVolts)
            {
                throw new HwinfoVoltageLogException(
                    $"HWiNFO row {rowIndex + 1} has a GPU core voltage outside " +
                    "the supported 0.1-2.0 V range.");
            }

            samples.Add(new HwinfoVoltageLogSample(
                timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified),
                voltage));
            if (samples.Count > MaximumSamples)
            {
                throw new HwinfoVoltageLogException(
                    $"The HWiNFO log exceeds the {MaximumSamples}-sample limit.");
            }
        }

        if (samples.Count == 0)
        {
            throw new HwinfoVoltageLogException(
                "The HWiNFO log has no numeric GPU Core Voltage samples.");
        }

        byte[] artifactBytes = content.ToArray();
        return new HwinfoVoltageLogAnalysis(
            new HwinfoVoltageLogArtifact(
                originalFileName,
                artifactBytes.LongLength,
                Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant(),
                encoding),
            ExpectedReferenceChannel,
            samples);
    }

    private static int SingleHeaderIndex(IReadOnlyList<string> header, string expected)
    {
        int[] matches = header
            .Select((value, index) => (value, index))
            .Where(item => item.value == expected)
            .Select(item => item.index)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new HwinfoVoltageLogException(
                $"The HWiNFO header must contain exactly one '{expected}' column; found {matches.Length}.");
        }
        return matches[0];
    }

    private static (string Text, string Encoding) Decode(ReadOnlySpan<byte> content)
    {
        byte[] bytes = content.ToArray();
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            return (utf8.GetString(bytes).TrimStart('\uFEFF'), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), "iso-8859-1-fallback");
        }
    }

    private static (IReadOnlyList<IReadOnlyList<string>> Rows, int SampleRowCount) ParseCsv(
        string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        int expectedFieldCount = 0;
        int dateIndex = -1;
        int timeIndex = -1;
        int sampleRowCount = 0;
        int sessionCount = 0;
        int rowNumber = 0;

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
                    AddCsvField(row, field);
                    break;
                case '\r':
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }
                    CompleteRow(
                        rows,
                        row,
                        field,
                        ref expectedFieldCount,
                        ref dateIndex,
                        ref timeIndex,
                        ref sampleRowCount,
                        ref sessionCount,
                        ref rowNumber);
                    break;
                case '\n':
                    CompleteRow(
                        rows,
                        row,
                        field,
                        ref expectedFieldCount,
                        ref dateIndex,
                        ref timeIndex,
                        ref sampleRowCount,
                        ref sessionCount,
                        ref rowNumber);
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (quoted)
        {
            throw new HwinfoVoltageLogException("The HWiNFO log contains an unterminated CSV quote.");
        }
        if (field.Length > 0 || row.Count > 0)
        {
            CompleteRow(
                rows,
                row,
                field,
                ref expectedFieldCount,
                ref dateIndex,
                ref timeIndex,
                ref sampleRowCount,
                ref sessionCount,
                ref rowNumber);
        }
        return (rows, sampleRowCount);
    }

    private static void AddCsvField(ICollection<string> row, StringBuilder field)
    {
        if (row.Count >= MaximumFields)
        {
            throw new HwinfoVoltageLogException(
                $"A HWiNFO CSV row exceeds the {MaximumFields}-field parser limit.");
        }

        row.Add(field.ToString());
        field.Clear();
    }

    private static void CompleteRow(
        IList<IReadOnlyList<string>> rows,
        List<string> row,
        StringBuilder field,
        ref int expectedFieldCount,
        ref int dateIndex,
        ref int timeIndex,
        ref int sampleRowCount,
        ref int sessionCount,
        ref int rowNumber)
    {
        rowNumber++;
        AddCsvField(row, field);
        bool blank = row.Count == 1 && string.IsNullOrWhiteSpace(row[0]);
        if (blank)
        {
            row.Clear();
            return;
        }

        if (rows.Count == 0)
        {
            expectedFieldCount = row.Count;
            dateIndex = SingleHeaderIndex(row, "Date");
            timeIndex = SingleHeaderIndex(row, "Time");
            _ = SingleHeaderIndex(row, ExpectedReferenceChannel);
            sessionCount = 1;
        }
        else
        {
            if (row.Count != expectedFieldCount)
            {
                throw new HwinfoVoltageLogException(
                    $"HWiNFO row {rowNumber} has {row.Count} fields; " +
                    $"expected {expectedFieldCount}.");
            }

            if (row[dateIndex] == "Date" && row[timeIndex] == "Time")
            {
                if (!row.SequenceEqual(rows[0], StringComparer.Ordinal))
                {
                    throw new HwinfoVoltageLogException(
                        "An appended HWiNFO session changed the channel layout.");
                }

                sessionCount++;
                if (sessionCount > MaximumSessions)
                {
                    throw new HwinfoVoltageLogException(
                        $"The HWiNFO log contains more than {MaximumSessions} sessions.");
                }
            }
            else
            {
                sampleRowCount++;
                if (sampleRowCount > MaximumSamples)
                {
                    throw new HwinfoVoltageLogException(
                        $"The HWiNFO log exceeds the {MaximumSamples}-sample-row limit.");
                }
            }
        }

        rows.Add(row.ToArray());
        row.Clear();
    }
}
