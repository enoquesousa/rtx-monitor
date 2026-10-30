namespace RtxMonitor.Managed;

public sealed class ComputedMetricsEngine : IDisposable
{
    private readonly SafeMetricsContext context;
    private bool disposed;

    public ComputedMetricsEngine(ComputedMetricOptions? options = null)
    {
        ComputedMetricOptions selected = options ?? new ComputedMetricOptions();
        Validate(selected);
        NativeComputedMetricOptions nativeOptions = NativeComputedMetricOptions.Create(selected);
        NativeStatus status = NativeMethods.rtxmon_metrics_context_create(
            in nativeOptions,
            out IntPtr nativeContext);
        if (status != NativeStatus.Ok)
        {
            Throw(status, "Não foi possível criar a janela de métricas calculadas");
        }

        context = new SafeMetricsContext(nativeContext);
    }

    public ComputedMetricsReport Observe(PublicTelemetryReport telemetry)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(telemetry);

        NativePublicTelemetryReport nativeTelemetry = ToNative(telemetry);
        NativeComputedMetricsReport nativeReport = NativeComputedMetricsReport.Create();
        NativeStatus status = NativeMethods.rtxmon_metrics_observe(
            context,
            in nativeTelemetry,
            ref nativeReport);
        if (status != NativeStatus.Ok)
        {
            Throw(status, "Não foi possível calcular as métricas da telemetria");
        }
        if (nativeReport.MetricCount > NativeMethods.MaxComputedMetrics)
        {
            throw new InvalidOperationException(
                $"Relatório nativo excedeu o limite de métricas: {nativeReport.MetricCount}.");
        }

        var metrics = new List<ComputedMetric>(checked((int)nativeReport.MetricCount));
        for (int index = 0; index < nativeReport.MetricCount; index++)
        {
            NativeComputedMetric metric = nativeReport.Metrics[index];
            if (metric.InputCount > NativeMethods.MaxMetricInputs)
            {
                throw new InvalidOperationException(
                    $"Métrica nativa excedeu o limite de entradas: {metric.InputCount}.");
            }

            var inputs = new List<PublicTelemetryField>(checked((int)metric.InputCount));
            var inputNames = new List<string>(checked((int)metric.InputCount));
            for (int inputIndex = 0; inputIndex < metric.InputCount; inputIndex++)
            {
                uint field = metric.InputFields[inputIndex];
                inputs.Add((PublicTelemetryField)field);
                inputNames.Add(NativeMethods.PublicFieldString(field));
            }

            bool isThresholdMetric =
                metric.Metric == (uint)ComputedMetricKind.GpuTemperatureTimeAboveThreshold;
            metrics.Add(new ComputedMetric(
                (ComputedMetricKind)metric.Metric,
                NativeMethods.ComputedMetricString(metric.Metric),
                (ComputedMetricState)metric.State,
                NativeMethods.MetricStateString(metric.State),
                (DataOrigin)metric.Origin,
                NativeMethods.DataOriginString(metric.Origin),
                (TelemetryUnit)metric.Unit,
                NativeMethods.UnitString(metric.Unit),
                NativeMethods.ComputedMetricFormula(metric.Metric),
                metric.State == (uint)ComputedMetricState.Available ? metric.Value : null,
                metric.TimestampUnixMilliseconds,
                metric.WindowMilliseconds,
                metric.SampleCount,
                isThresholdMetric ? metric.TemperatureThresholdC : null,
                inputs,
                inputNames));
        }

        return new ComputedMetricsReport(
            nativeReport.GpuIndex,
            nativeReport.TimestampUnixMilliseconds,
            metrics);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        NativeMethods.rtxmon_metrics_context_reset(context);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        context.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private static NativePublicTelemetryReport ToNative(PublicTelemetryReport telemetry)
    {
        if (telemetry.Fields.Count > NativeMethods.MaxPublicFields)
        {
            throw new ArgumentException(
                $"O relatório possui {telemetry.Fields.Count} campos; o limite é " +
                $"{NativeMethods.MaxPublicFields}.",
                nameof(telemetry));
        }

        NativePublicTelemetryReport native = NativePublicTelemetryReport.Create();
        native.GpuIndex = telemetry.GpuIndex;
        native.FieldCount = checked((uint)telemetry.Fields.Count);
        native.TimestampUnixMilliseconds = telemetry.TimestampUnixMilliseconds;
        for (int index = 0; index < telemetry.Fields.Count; index++)
        {
            PublicTelemetryValue source = telemetry.Fields[index];
            native.Fields[index] = new NativePublicTelemetryValue
            {
                Field = (uint)source.Field,
                Provider = (uint)source.Provider,
                State = (uint)source.State,
                Origin = (uint)source.Origin,
                ValueType = (uint)source.ValueType,
                Unit = (uint)source.Unit,
                NativeStatus = source.NativeStatus,
                ProviderNativeId = source.ProviderNativeId,
                ValueU64 = source.UnsignedValue ?? 0,
                ValueI64 = source.SignedValue ?? 0,
                ValueF64 = source.DoubleValue ?? 0,
                TimestampUnixMilliseconds = source.TimestampUnixMilliseconds,
            };
        }

        return native;
    }

    private static void Validate(ComputedMetricOptions options)
    {
        if (options.WindowMilliseconds is < 100 or > 3_600_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "A janela deve estar entre 100 e 3.600.000 ms.");
        }
        if (options.TemperatureThresholdC is < 0 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "O limiar térmico deve estar entre 0 e 500 °C.");
        }
        if (options.MaximumSamples is < 2 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "O limite de amostras deve estar entre 2 e 65.536.");
        }
    }

    private static void Throw(NativeStatus status, string operation)
    {
        string diagnostic = NativeMethods.LastError();
        string message = $"{operation}: {NativeMethods.StatusString(status)}";
        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            message += $" ({diagnostic})";
        }

        throw new RtxMonitorException(status, message);
    }
}
