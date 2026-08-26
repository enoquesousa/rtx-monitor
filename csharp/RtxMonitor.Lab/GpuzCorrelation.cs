using System.Globalization;

namespace RtxMonitor.Lab;

public sealed record GpuzCorrelationPair(
    int ChannelIndex,
    string Channel,
    string Unit,
    string SourceScope,
    int SampleCount,
    double? Coefficient,
    string Status);

public sealed record GpuzCorrelationReport(
    int SchemaVersion,
    string SourceKind,
    string ArtifactSha256,
    string ReferenceChannel,
    string ReferenceUnit,
    int SampleCount,
    int SessionCount,
    int? SelectedSessionIndex,
    string Method,
    IReadOnlyList<GpuzCorrelationPair> Pairs,
    IReadOnlyList<string> Warnings);

public sealed class GpuzCorrelationException : Exception
{
    public GpuzCorrelationException(string message)
        : base(message)
    {
    }
}

public static class GpuzCorrelation
{
    public const int SchemaVersion = 1;
    public const string SourceKind = "gpuz_internal_correlation";
    public const string Method = "pearson_zero_lag";

    public static GpuzCorrelationReport AnalyzeFile(
        string inputPath,
        string referenceChannel,
        int? sessionIndex = null)
    {
        GpuzLogAnalysis analysis = GpuzSensorLog.AnalyzeFile(inputPath);
        return Analyze(analysis, referenceChannel, sessionIndex);
    }

    public static GpuzCorrelationReport Analyze(
        GpuzLogAnalysis analysis,
        string referenceChannel,
        int? sessionIndex = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceChannel);
        if (sessionIndex is < 0 || sessionIndex >= analysis.SessionCount)
        {
            throw new GpuzCorrelationException(
                $"Session index must be between 0 and {analysis.SessionCount - 1}.");
        }

        GpuzChannelAnalysis? reference = analysis.Channels.SingleOrDefault(
            channel => string.Equals(channel.Name, referenceChannel, StringComparison.Ordinal));
        if (reference is null)
        {
            throw new GpuzCorrelationException(
                $"Reference channel '{referenceChannel}' does not exist in the GPU-Z log.");
        }

        if (reference.Representation != "numeric")
        {
            throw new GpuzCorrelationException(
                $"Reference channel '{referenceChannel}' is not a numeric measurement.");
        }

        double?[] referenceValues = ReadNumericColumn(analysis, reference.Index, sessionIndex);
        var pairs = new List<GpuzCorrelationPair>();
        foreach (GpuzChannelAnalysis candidate in analysis.Channels)
        {
            if (candidate.Index == reference.Index || candidate.Representation != "numeric")
            {
                continue;
            }

            double?[] candidateValues = ReadNumericColumn(
                analysis,
                candidate.Index,
                sessionIndex);
            (int sampleCount, double? coefficient, string status) = CalculatePearson(
                referenceValues,
                candidateValues);
            pairs.Add(
                new GpuzCorrelationPair(
                    candidate.Index,
                    candidate.Name,
                    candidate.Unit,
                    candidate.SourceScope,
                    sampleCount,
                    coefficient,
                    status));
        }

        pairs.Sort((left, right) =>
        {
            int availabilityOrder = right.Coefficient.HasValue.CompareTo(left.Coefficient.HasValue);
            if (availabilityOrder != 0)
            {
                return availabilityOrder;
            }

            int coefficientOrder = Math.Abs(right.Coefficient ?? 0)
                .CompareTo(Math.Abs(left.Coefficient ?? 0));
            return coefficientOrder != 0
                ? coefficientOrder
                : left.ChannelIndex.CompareTo(right.ChannelIndex);
        });

        string[] warnings =
        [
            "Correlation measures co-movement inside one external GPU-Z log; it does not identify a physical sensor or the private interface used to read it.",
            "Zero-lag Pearson correlation is sensitive to shared workload, thermal inertia, short captures, and low variation.",
            "Host-system channels are retained and labeled so accidental cross-system correlation remains visible.",
        ];
        if (analysis.SessionCount > 1 && sessionIndex is null)
        {
            warnings =
            [
                .. warnings,
                $"The report combines {analysis.SessionCount} appended sessions; compare per-session results before interpreting coefficients across different baselines.",
            ];
        }

        return new GpuzCorrelationReport(
            SchemaVersion,
            SourceKind,
            analysis.Artifact.Sha256,
            reference.Name,
            reference.Unit,
            referenceValues.Length,
            analysis.SessionCount,
            sessionIndex,
            Method,
            pairs,
            warnings);
    }

    private static double?[] ReadNumericColumn(
        GpuzLogAnalysis analysis,
        int channelIndex,
        int? sessionIndex)
    {
        GpuzLogSample[] samples = analysis.Samples
            .Where(sample => sessionIndex is null || sample.SessionIndex == sessionIndex)
            .ToArray();
        var values = new double?[samples.Length];
        for (int index = 0; index < samples.Length; index++)
        {
            string raw = samples[index].Values[channelIndex];
            if (double.TryParse(
                    raw,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value) &&
                double.IsFinite(value))
            {
                values[index] = value;
            }
        }

        return values;
    }

    private static (int SampleCount, double? Coefficient, string Status) CalculatePearson(
        IReadOnlyList<double?> left,
        IReadOnlyList<double?> right)
    {
        var pairs = new List<(double Left, double Right)>(Math.Min(left.Count, right.Count));
        for (int index = 0; index < left.Count && index < right.Count; index++)
        {
            if (left[index] is double leftValue && right[index] is double rightValue)
            {
                pairs.Add((leftValue, rightValue));
            }
        }

        if (pairs.Count < 3)
        {
            return (pairs.Count, null, "insufficient_samples");
        }

        double leftMean = pairs.Average(pair => pair.Left);
        double rightMean = pairs.Average(pair => pair.Right);
        double covariance = 0;
        double leftSquares = 0;
        double rightSquares = 0;
        foreach ((double leftValue, double rightValue) in pairs)
        {
            double centeredLeft = leftValue - leftMean;
            double centeredRight = rightValue - rightMean;
            covariance += centeredLeft * centeredRight;
            leftSquares += centeredLeft * centeredLeft;
            rightSquares += centeredRight * centeredRight;
        }

        if (leftSquares == 0)
        {
            return (pairs.Count, null, "constant_reference");
        }

        if (rightSquares == 0)
        {
            return (pairs.Count, null, "constant_candidate");
        }

        double coefficient = covariance / Math.Sqrt(leftSquares * rightSquares);
        return (pairs.Count, Math.Clamp(coefficient, -1, 1), "computed");
    }
}
