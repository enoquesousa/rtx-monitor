using System.Globalization;
using System.Text.Json;

namespace RtxMonitor.Lab;

public sealed class ExperimentSeriesAnalysisException : Exception
{
    public ExperimentSeriesAnalysisException(string message)
        : base(message)
    {
    }

    public ExperimentSeriesAnalysisException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public sealed record AnalysisLocator(
    string OperationId,
    long OffsetBytes,
    int WidthBytes,
    string Endianness,
    bool Signed,
    double Scale,
    double Addend);

public sealed record AnalysisStatistics(
    int SampleCount,
    double Minimum,
    double Maximum,
    double Mean,
    double StandardDeviation,
    double UpdatePeriodMs,
    double MinimumDelta,
    double MaximumDelta,
    double MeanDelta);

public sealed record AnalysisCorrelation(
    string Against,
    string Method,
    double Coefficient,
    double LagMs,
    int SampleCount);

public sealed record AnalysisCandidate(
    string CandidateId,
    string Stage,
    string InputPackageManifestSha256,
    string SourceKind,
    string ValueUnit,
    AnalysisLocator? Locator,
    string? Hypothesis,
    string? PhysicalName,
    AnalysisStatistics Statistics,
    IReadOnlyList<AnalysisCorrelation> Correlations,
    object? ExternalValidation,
    IReadOnlyList<string> AlternativeHypotheses,
    IReadOnlyList<string> Limitations);

public sealed record AnalysisTool(
    string Name,
    string Version,
    IReadOnlyDictionary<string, object?> Parameters);

public sealed record ExperimentAnalysisReport(
    int SchemaVersion,
    string AnalysisId,
    string ExperimentId,
    string CreatedAtUtc,
    string Status,
    string InputExperimentManifestSha256,
    IReadOnlyList<string> InputPackageManifestSha256s,
    AnalysisTool Analyzer,
    IReadOnlyList<AnalysisCandidate> Candidates,
    IReadOnlyList<string> Warnings);

public static class ExperimentSeriesAnalyzer
{
    public const int SchemaVersion = 1;
    public const string AnalyzerName = "rtxmon-lab-series-analyzer";
    public const string AnalyzerVersion = "0.8.0";
    private const long MaximumSeriesSizeBytes = 64L * 1024 * 1024;
    private const long MaximumCorrelationPairEvaluations = 10_000_000;
    private static readonly string[] CandidateSourceKinds =
    [
        "pci_config",
        "bar0_mmio",
        "private_interface",
        "vbios_offline",
        "public_telemetry",
        "external_reference",
    ];

    public static ExperimentAnalysisReport Analyze(
        string manifestPath,
        string expectedManifestSha256,
        string packageRoot,
        string seriesPackageRelativePath,
        int maximumLagSamples,
        Guid analysisId,
        DateTimeOffset createdAtUtc) =>
        AnalyzeCore(
            manifestPath,
            expectedManifestSha256,
            packageRoot,
            seriesPackageRelativePath,
            maximumLagSamples,
            analysisId,
            createdAtUtc,
            afterVerifiedPayloadRead: null);

    internal static ExperimentAnalysisReport AnalyzeForTesting(
        string manifestPath,
        string expectedManifestSha256,
        string packageRoot,
        string seriesPackageRelativePath,
        int maximumLagSamples,
        Guid analysisId,
        DateTimeOffset createdAtUtc,
        Action afterVerifiedPayloadRead)
    {
        ArgumentNullException.ThrowIfNull(afterVerifiedPayloadRead);
        return AnalyzeCore(
            manifestPath,
            expectedManifestSha256,
            packageRoot,
            seriesPackageRelativePath,
            maximumLagSamples,
            analysisId,
            createdAtUtc,
            afterVerifiedPayloadRead);
    }

    private static ExperimentAnalysisReport AnalyzeCore(
        string manifestPath,
        string expectedManifestSha256,
        string packageRoot,
        string seriesPackageRelativePath,
        int maximumLagSamples,
        Guid analysisId,
        DateTimeOffset createdAtUtc,
        Action? afterVerifiedPayloadRead)
    {
        if (maximumLagSamples is < 0 or > 1000)
        {
            throw new ExperimentSeriesAnalysisException(
                "Maximum lag samples must be between 0 and 1000.");
        }

        ValidatedExperimentManifest manifest;
        try
        {
            manifest = ExperimentManifestProducer.ReadAndValidateFile(
                manifestPath,
                packageRoot,
                expectedManifestSha256);
        }
        catch (ExperimentManifestException error)
        {
            throw new ExperimentSeriesAnalysisException(
                $"Experiment manifest validation failed: {error.Message}",
                error);
        }

        string normalizedPackage = ValidateRelativePath(seriesPackageRelativePath);
        ExperimentArtifactPackage package = manifest.ArtifactPackages.SingleOrDefault(
                candidate => string.Equals(
                    candidate.RelativePath,
                    normalizedPackage,
                    StringComparison.Ordinal))
            ?? throw new ExperimentSeriesAnalysisException(
                "The requested series package is not referenced by the experiment manifest.");
        string scenarioId = package.ScenarioId
            ?? throw new ExperimentSeriesAnalysisException(
                "The requested series package must reference a scenario_id.");
        if (!manifest.ScenarioWindows.TryGetValue(
                scenarioId,
                out ExperimentScenarioWindow? scenarioWindow))
        {
            throw new ExperimentSeriesAnalysisException(
                "The requested series package references an unknown scenario_id.");
        }

        if (scenarioWindow.BeginMonotonicNs is not long scenarioBegin ||
            scenarioWindow.EndMonotonicNs is not long scenarioEnd)
        {
            throw new ExperimentSeriesAnalysisException(
                "The requested series scenario must have begin and end markers.");
        }

        VerifiedLabPayload verified;
        try
        {
            verified = LabPackage.VerifyAndReadPayload(
                package.ResolvedPath,
                package.ManifestSha256,
                MaximumSeriesSizeBytes);
        }
        catch (LabPackageException error)
        {
            throw new ExperimentSeriesAnalysisException(
                $"Series package verification failed: {error.Message}",
                error);
        }

        afterVerifiedPayloadRead?.Invoke();
        NumericSeriesDocument series = ReadSeries(verified.PayloadBytes);
        ValidateSeriesWindow(series.Samples, scenarioId, scenarioBegin, scenarioEnd);
        AnalysisStatistics statistics = CalculateStatistics(series.Samples);
        IReadOnlyList<AnalysisCorrelation> correlations = CalculateCorrelations(
            series,
            statistics.UpdatePeriodMs,
            maximumLagSamples);

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["maximum_lag_samples"] = maximumLagSamples,
            ["maximum_pair_evaluations"] = MaximumCorrelationPairEvaluations,
            ["scenario_id"] = scenarioId,
            ["timebase"] = "monotonic_ns",
            ["statistics"] = "population_standard_deviation",
            ["update_period"] = "median_adjacent_interval",
        };
        string[] warnings = series.Reference is null
            ?
            [
                "No reference series was supplied; the report preserves the candidate as raw_unknown.",
                "Statistics and deltas do not identify a physical sensor or authorize a provider.",
            ]
            :
            [
                "Cross-correlation is descriptive evidence only; this report does not promote the candidate beyond raw_unknown.",
                "A physical name still requires independent repetitions, alternative-hypothesis review, and an appropriate external reference.",
            ];
        var candidate = new AnalysisCandidate(
            series.CandidateId,
            "raw_unknown",
            verified.Package.ManifestSha256,
            series.SourceKind,
            series.ValueUnit,
            series.Locator,
            series.Hypothesis,
            null,
            statistics,
            correlations,
            null,
            series.AlternativeHypotheses,
            series.Limitations);
        return new ExperimentAnalysisReport(
            SchemaVersion,
            analysisId.ToString("D", CultureInfo.InvariantCulture),
            manifest.ExperimentId,
            createdAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            "complete",
            manifest.Sha256,
            [verified.Package.ManifestSha256],
            new AnalysisTool(AnalyzerName, AnalyzerVersion, parameters),
            [candidate],
            warnings);
    }

    private static NumericSeriesDocument ReadSeries(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length < 1 || bytes.Length > MaximumSeriesSizeBytes)
        {
            throw new ExperimentSeriesAnalysisException(
                $"The numeric series payload size is outside the 1 to {MaximumSeriesSizeBytes} byte limit.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            JsonElement root = document.RootElement;
            RequireObject(
                root,
                "$",
                [
                    "schema_version",
                    "source_kind",
                    "candidate_id",
                    "candidate_source_kind",
                    "value_unit",
                    "locator",
                    "hypothesis",
                    "alternative_hypotheses",
                    "limitations",
                    "samples",
                    "reference",
                ]);
            RequireInteger(root, "schema_version", 1, 1, "$");
            RequireValue(root, "source_kind", "numeric_time_series", "$");
            string candidateId = RequireIdentifier(root, "candidate_id", "$");
            string sourceKind = RequireEnum(
                root,
                "candidate_source_kind",
                CandidateSourceKinds,
                "$");
            string valueUnit = RequireString(root, "value_unit", 1, 128, "$");
            AnalysisLocator? locator = ReadLocator(root.GetProperty("locator"));
            if ((sourceKind is "pci_config" or "bar0_mmio" or "private_interface") && locator is null)
            {
                throw new ExperimentSeriesAnalysisException(
                    "Low-level candidate sources require an explicit reviewed locator.");
            }

            string? hypothesis = ReadNullableString(
                root.GetProperty("hypothesis"),
                4096,
                "$.hypothesis");
            string[] alternatives = ReadStringArray(
                root.GetProperty("alternative_hypotheses"),
                0,
                128,
                4096,
                "$.alternative_hypotheses");
            string[] limitations = ReadStringArray(
                root.GetProperty("limitations"),
                1,
                128,
                4096,
                "$.limitations");
            NumericSeriesSample[] samples = ReadSamples(root.GetProperty("samples"), "$.samples");
            NumericReferenceSeries? reference = ReadReference(
                root.GetProperty("reference"),
                samples);
            return new NumericSeriesDocument(
                candidateId,
                sourceKind,
                valueUnit,
                locator,
                hypothesis,
                alternatives,
                limitations,
                samples,
                reference);
        }
        catch (ExperimentSeriesAnalysisException)
        {
            throw;
        }
        catch (Exception error) when (
            error is JsonException or InvalidOperationException or KeyNotFoundException or
            FormatException or OverflowException)
        {
            throw new ExperimentSeriesAnalysisException(
                "The numeric series JSON is malformed or incomplete.",
                error);
        }
    }

    private static AnalysisLocator? ReadLocator(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        const string path = "$.locator";
        RequireObject(
            value,
            path,
            ["operation_id", "offset_bytes", "width_bytes", "endianness", "signed", "scale", "addend"]);
        string operation = RequireIdentifier(value, "operation_id", path);
        long offset = RequireInteger(value, "offset_bytes", 0, uint.MaxValue, path);
        int width = checked((int)RequireInteger(value, "width_bytes", 1, 8, path));
        if (width is not (1 or 2 or 4 or 8) || offset % width != 0)
        {
            throw new ExperimentSeriesAnalysisException(
                "$.locator must use an aligned width of 1, 2, 4, or 8 bytes.");
        }

        string endianness = RequireEnum(value, "endianness", ["little", "big"], path);
        JsonElement signedValue = value.GetProperty("signed");
        if (signedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ExperimentSeriesAnalysisException("$.locator.signed must be boolean.");
        }

        double scale = RequireNumber(value, "scale", path);
        double addend = RequireNumber(value, "addend", path);
        return new AnalysisLocator(operation, offset, width, endianness, signedValue.GetBoolean(), scale, addend);
    }

    private static NumericReferenceSeries? ReadReference(
        JsonElement value,
        IReadOnlyList<NumericSeriesSample> candidate)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        const string path = "$.reference";
        RequireObject(value, path, ["name", "unit", "samples"]);
        string name = RequireString(value, "name", 1, 1024, path);
        string unit = RequireString(value, "unit", 1, 128, path);
        NumericSeriesSample[] samples = ReadSamples(value.GetProperty("samples"), $"{path}.samples");
        if (samples.Length != candidate.Count ||
            samples.Where((sample, index) => sample.MonotonicNs != candidate[index].MonotonicNs).Any())
        {
            throw new ExperimentSeriesAnalysisException(
                "Candidate and reference series must use identical monotonic timestamps.");
        }

        return new NumericReferenceSeries(name, unit, samples);
    }

    private static NumericSeriesSample[] ReadSamples(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 3 or > 1_000_000)
        {
            throw new ExperimentSeriesAnalysisException(
                $"{path} must contain between 3 and 1000000 samples.");
        }

        var samples = new NumericSeriesSample[value.GetArrayLength()];
        long previous = -1;
        int index = 0;
        foreach (JsonElement sample in value.EnumerateArray())
        {
            string samplePath = $"{path}[{index}]";
            RequireObject(sample, samplePath, ["monotonic_ns", "value"]);
            long timestamp = RequireInteger(sample, "monotonic_ns", 0, long.MaxValue, samplePath);
            if (timestamp <= previous)
            {
                throw new ExperimentSeriesAnalysisException(
                    $"{path} timestamps must increase strictly.");
            }

            previous = timestamp;
            samples[index] = new NumericSeriesSample(
                timestamp,
                RequireNumber(sample, "value", samplePath));
            index++;
        }

        return samples;
    }

    private static AnalysisStatistics CalculateStatistics(IReadOnlyList<NumericSeriesSample> samples)
    {
        double minimum = RequireFiniteDerived(
            samples.Min(sample => sample.Value),
            "minimum");
        double maximum = RequireFiniteDerived(
            samples.Max(sample => sample.Value),
            "maximum");
        double mean = RequireFiniteDerived(
            samples.Average(sample => sample.Value),
            "mean");
        double variance = RequireFiniteDerived(samples.Average(sample =>
        {
            double centered = RequireFiniteDerived(
                sample.Value - mean,
                "centered sample");
            return RequireFiniteDerived(
                centered * centered,
                "squared centered sample");
        }), "variance");
        double[] deltas = samples
            .Zip(
                samples.Skip(1),
                (left, right) => RequireFiniteDerived(
                    right.Value - left.Value,
                    "adjacent sample delta"))
            .ToArray();
        double[] intervals = samples
            .Zip(
                samples.Skip(1),
                (left, right) => RequireFiniteDerived(
                    (right.MonotonicNs - left.MonotonicNs) / 1_000_000.0,
                    "adjacent sample interval"))
            .Order()
            .ToArray();
        double standardDeviation = RequireFiniteDerived(
            Math.Sqrt(variance),
            "standard deviation");
        double updatePeriod = RequireFiniteDerived(
            Median(intervals),
            "update period");
        double minimumDelta = RequireFiniteDerived(deltas.Min(), "minimum delta");
        double maximumDelta = RequireFiniteDerived(deltas.Max(), "maximum delta");
        double meanDelta = RequireFiniteDerived(deltas.Average(), "mean delta");
        return new AnalysisStatistics(
            samples.Count,
            minimum,
            maximum,
            mean,
            standardDeviation,
            updatePeriod,
            minimumDelta,
            maximumDelta,
            meanDelta);
    }

    private static void ValidateSeriesWindow(
        IReadOnlyList<NumericSeriesSample> samples,
        string scenarioId,
        long scenarioBegin,
        long scenarioEnd)
    {
        if (samples.Any(sample =>
                sample.MonotonicNs < scenarioBegin || sample.MonotonicNs > scenarioEnd))
        {
            throw new ExperimentSeriesAnalysisException(
                $"Numeric series samples must remain inside scenario '{scenarioId}' begin/end markers.");
        }
    }

    private static IReadOnlyList<AnalysisCorrelation> CalculateCorrelations(
        NumericSeriesDocument series,
        double updatePeriodMs,
        int maximumLagSamples)
    {
        if (series.Reference is null)
        {
            return [];
        }

        int boundedLag = Math.Min(maximumLagSamples, series.Samples.Count - 3);
        long pairEvaluations = checked(
            series.Samples.Count +
            (2L *
             (((long)boundedLag * series.Samples.Count) -
              (((long)boundedLag * (boundedLag + 1L)) / 2L))));
        if (pairEvaluations > MaximumCorrelationPairEvaluations)
        {
            throw new ExperimentSeriesAnalysisException(
                $"Correlation would evaluate {pairEvaluations} sample pairs, above the " +
                $"{MaximumCorrelationPairEvaluations} defensive limit.");
        }

        CorrelationCandidate? best = null;
        for (int lag = -boundedLag; lag <= boundedLag; lag++)
        {
            var pairs = new List<(double Candidate, double Reference)>();
            for (int candidateIndex = 0; candidateIndex < series.Samples.Count; candidateIndex++)
            {
                int referenceIndex = candidateIndex + lag;
                if (referenceIndex >= 0 && referenceIndex < series.Reference.Samples.Count)
                {
                    pairs.Add(
                        (series.Samples[candidateIndex].Value,
                         series.Reference.Samples[referenceIndex].Value));
                }
            }

            double? coefficient = Pearson(pairs);
            if (coefficient is not double value)
            {
                continue;
            }

            var candidate = new CorrelationCandidate(lag, pairs.Count, value);
            if (best is null ||
                Math.Abs(candidate.Coefficient) > Math.Abs(best.Coefficient) + 1e-15 ||
                (Math.Abs(Math.Abs(candidate.Coefficient) - Math.Abs(best.Coefficient)) <= 1e-15 &&
                 Math.Abs(candidate.LagSamples) < Math.Abs(best.LagSamples)))
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            return [];
        }

        double lagMs = RequireFiniteDerived(
            best.LagSamples * updatePeriodMs,
            "correlation lag");
        return
        [
            new AnalysisCorrelation(
                series.Reference.Name,
                "cross_correlation",
                RequireFiniteDerived(best.Coefficient, "correlation coefficient"),
                lagMs,
                best.SampleCount),
        ];
    }

    private static double? Pearson(IReadOnlyList<(double Candidate, double Reference)> pairs)
    {
        if (pairs.Count < 3)
        {
            return null;
        }

        double candidateMean = RequireFiniteDerived(
            pairs.Average(pair => pair.Candidate),
            "correlation candidate mean");
        double referenceMean = RequireFiniteDerived(
            pairs.Average(pair => pair.Reference),
            "correlation reference mean");
        double covariance = 0;
        double candidateSquares = 0;
        double referenceSquares = 0;
        foreach ((double candidate, double reference) in pairs)
        {
            double centeredCandidate = RequireFiniteDerived(
                candidate - candidateMean,
                "centered correlation candidate");
            double centeredReference = RequireFiniteDerived(
                reference - referenceMean,
                "centered correlation reference");
            double covarianceTerm = RequireFiniteDerived(
                centeredCandidate * centeredReference,
                "correlation covariance term");
            double candidateSquare = RequireFiniteDerived(
                centeredCandidate * centeredCandidate,
                "correlation candidate square");
            double referenceSquare = RequireFiniteDerived(
                centeredReference * centeredReference,
                "correlation reference square");
            covariance = RequireFiniteDerived(
                covariance + covarianceTerm,
                "correlation covariance");
            candidateSquares = RequireFiniteDerived(
                candidateSquares + candidateSquare,
                "correlation candidate square sum");
            referenceSquares = RequireFiniteDerived(
                referenceSquares + referenceSquare,
                "correlation reference square sum");
        }

        if (candidateSquares == 0 || referenceSquares == 0)
        {
            return null;
        }

        double squareProduct = RequireFiniteDerived(
            candidateSquares * referenceSquares,
            "correlation square product");
        double denominator = RequireFiniteDerived(
            Math.Sqrt(squareProduct),
            "correlation denominator");
        if (denominator == 0)
        {
            return null;
        }

        double coefficient = RequireFiniteDerived(
            covariance / denominator,
            "correlation coefficient");
        return RequireFiniteDerived(
            Math.Clamp(coefficient, -1, 1),
            "clamped correlation coefficient");
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
    }

    private static string ValidateRelativePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.StartsWith("/", StringComparison.Ordinal) ||
            value.Contains("\\", StringComparison.Ordinal) ||
            value.Contains(":", StringComparison.Ordinal) ||
            value.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ExperimentSeriesAnalysisException(
                "Series package must be a normalized relative path using '/'.");
        }

        return value;
    }

    private static void RequireObject(JsonElement value, string path, IReadOnlyCollection<string> properties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ExperimentSeriesAnalysisException($"{path} must be an object.");
        }

        var expected = new HashSet<string>(properties, StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!actual.Add(property.Name) || !expected.Contains(property.Name))
            {
                throw new ExperimentSeriesAnalysisException(
                    $"{path} contains duplicate or unsupported property '{property.Name}'.");
            }
        }

        string? missing = expected.FirstOrDefault(property => !actual.Contains(property));
        if (missing is not null)
        {
            throw new ExperimentSeriesAnalysisException($"{path} is missing property '{missing}'.");
        }
    }

    private static string RequireString(
        JsonElement parent,
        string property,
        int minimum,
        int maximum,
        string path)
    {
        JsonElement value = parent.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ExperimentSeriesAnalysisException($"{path}.{property} must be a string.");
        }

        string result = value.GetString()!;
        if (result.Length < minimum || result.Length > maximum || result.Any(char.IsControl))
        {
            throw new ExperimentSeriesAnalysisException(
                $"{path}.{property} has an invalid length or control character.");
        }

        return result;
    }

    private static string? ReadNullableString(JsonElement value, int maximum, string path)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ExperimentSeriesAnalysisException($"{path} must be a string or null.");
        }

        string result = value.GetString()!;
        if (result.Length is < 1 || result.Length > maximum || result.Any(char.IsControl))
        {
            throw new ExperimentSeriesAnalysisException($"{path} has invalid text.");
        }

        return result;
    }

    private static string[] ReadStringArray(
        JsonElement value,
        int minimum,
        int maximum,
        int maximumStringLength,
        string path)
    {
        if (value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() < minimum ||
            value.GetArrayLength() > maximum)
        {
            throw new ExperimentSeriesAnalysisException(
                $"{path} must contain between {minimum} and {maximum} strings.");
        }

        var result = new string[value.GetArrayLength()];
        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            result[index] = ReadNullableString(item, maximumStringLength, $"{path}[{index}]")
                ?? throw new ExperimentSeriesAnalysisException($"{path}[{index}] cannot be null.");
            index++;
        }

        return result;
    }

    private static string RequireEnum(
        JsonElement parent,
        string property,
        IReadOnlyCollection<string> values,
        string path)
    {
        string result = RequireString(parent, property, 1, 256, path);
        if (!values.Contains(result, StringComparer.Ordinal))
        {
            throw new ExperimentSeriesAnalysisException($"{path}.{property} has an unsupported value.");
        }

        return result;
    }

    private static string RequireIdentifier(JsonElement parent, string property, string path)
    {
        string result = RequireString(parent, property, 1, 128, path);
        if (result[0] is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') ||
            result.Any(character =>
                character is not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and
                not '.' and not '_' and not '-'))
        {
            throw new ExperimentSeriesAnalysisException(
                $"{path}.{property} must match [a-z0-9][a-z0-9._-]{{0,127}}.");
        }

        return result;
    }

    private static void RequireValue(
        JsonElement parent,
        string property,
        string expected,
        string path)
    {
        if (!string.Equals(
                RequireString(parent, property, expected.Length, expected.Length, path),
                expected,
                StringComparison.Ordinal))
        {
            throw new ExperimentSeriesAnalysisException(
                $"{path}.{property} must equal '{expected}'.");
        }
    }

    private static long RequireInteger(
        JsonElement parent,
        string property,
        long minimum,
        long maximum,
        string path)
    {
        JsonElement value = parent.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long result) ||
            result < minimum ||
            result > maximum)
        {
            throw new ExperimentSeriesAnalysisException(
                $"{path}.{property} must be an integer between {minimum} and {maximum}.");
        }

        return result;
    }

    private static double RequireNumber(JsonElement parent, string property, string path)
    {
        JsonElement value = parent.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) ||
            !double.IsFinite(result))
        {
            throw new ExperimentSeriesAnalysisException($"{path}.{property} must be a finite number.");
        }

        return result;
    }

    private static double RequireFiniteDerived(double value, string description)
    {
        if (!double.IsFinite(value))
        {
            throw new ExperimentSeriesAnalysisException(
                $"Numeric analysis produced a non-finite {description}.");
        }

        return value;
    }

    private sealed record NumericSeriesSample(long MonotonicNs, double Value);

    private sealed record NumericReferenceSeries(
        string Name,
        string Unit,
        IReadOnlyList<NumericSeriesSample> Samples);

    private sealed record NumericSeriesDocument(
        string CandidateId,
        string SourceKind,
        string ValueUnit,
        AnalysisLocator? Locator,
        string? Hypothesis,
        IReadOnlyList<string> AlternativeHypotheses,
        IReadOnlyList<string> Limitations,
        IReadOnlyList<NumericSeriesSample> Samples,
        NumericReferenceSeries? Reference);

    private sealed record CorrelationCandidate(
        int LagSamples,
        int SampleCount,
        double Coefficient);
}
