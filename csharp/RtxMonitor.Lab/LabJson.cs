using System.Text;
using System.Text.Json;

namespace RtxMonitor.Lab;

public static class LabJson
{
    public static string SerializeExperimentManifest(string manifestJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        return manifestJson;
    }

    public static string SerializeExperimentAnalysis(ExperimentAnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(
            report,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            });
    }

    public static string SerializeVoltageStatusCorrelation(VoltageStatusCorrelationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
    }

    public static string SerializeVoltageStatusCorrelationV2(
        VoltageStatusCorrelationReportV2 report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
    }

    public static string SerializeThermChannelCorrelationV2(
        ThermChannelCorrelationReportV2 report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
    }

    public static byte[] SerializeManifestUtf8(
        LabPackageManifest manifest,
        bool appendNewLine = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteManifest(writer, manifest);
        }

        if (appendNewLine)
        {
            buffer.WriteByte((byte)'\n');
        }

        return buffer.ToArray();
    }

    public static string SerializeResult(string operation, string status, LabPackageResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(result);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", operation);
            writer.WriteString("status", status);
            writer.WriteString("package_path", result.PackagePath);
            writer.WriteString("manifest_sha256", result.ManifestSha256);
            writer.WritePropertyName("manifest");
            WriteManifest(writer, result.Manifest);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string SerializeError(
        string? operation,
        string errorCode,
        string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", operation);
            writer.WriteString("status", "error");
            writer.WriteString("error_code", errorCode);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string SerializeGpuzLogAnalysis(GpuzLogAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", analysis.SchemaVersion);
            writer.WriteString("source_kind", analysis.SourceKind);
            writer.WritePropertyName("artifact");
            writer.WriteStartObject();
            writer.WriteString("original_file_name", analysis.Artifact.OriginalFileName);
            writer.WriteNumber("size_bytes", analysis.Artifact.SizeBytes);
            writer.WriteString("sha256", analysis.Artifact.Sha256);
            writer.WriteString("text_encoding", analysis.Artifact.TextEncoding);
            writer.WriteEndObject();
            writer.WriteNumber("sample_count", analysis.SampleCount);
            writer.WriteNumber("session_count", analysis.SessionCount);
            writer.WriteString("first_timestamp_local", analysis.FirstTimestampLocal);
            writer.WriteString("last_timestamp_local", analysis.LastTimestampLocal);
            if (analysis.MedianIntervalMs is double medianIntervalMs)
            {
                writer.WriteNumber("median_interval_ms", medianIntervalMs);
            }
            else
            {
                writer.WriteNull("median_interval_ms");
            }

            writer.WriteBoolean("timestamps_have_timezone", analysis.TimestampsHaveTimezone);
            writer.WritePropertyName("channels");
            writer.WriteStartArray();
            foreach (GpuzChannelAnalysis channel in analysis.Channels)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", channel.Index);
                writer.WriteString("name", channel.Name);
                writer.WriteString("unit", channel.Unit);
                writer.WriteString("source_scope", channel.SourceScope);
                writer.WriteString("category", channel.Category);
                writer.WriteString("representation", channel.Representation);
                writer.WriteNumber("sample_count", channel.SampleCount);
                writer.WriteNumber("missing_count", channel.MissingCount);
                writer.WritePropertyName("numeric_statistics");
                if (channel.NumericStatistics is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("sample_count", channel.NumericStatistics.SampleCount);
                    writer.WriteNumber("minimum", channel.NumericStatistics.Minimum);
                    writer.WriteNumber("maximum", channel.NumericStatistics.Maximum);
                    writer.WriteNumber("mean", channel.NumericStatistics.Mean);
                    writer.WriteNumber(
                        "standard_deviation",
                        channel.NumericStatistics.StandardDeviation);
                    writer.WriteNumber("latest", channel.NumericStatistics.Latest);
                    writer.WriteEndObject();
                }

                writer.WriteString("latest_raw", channel.LatestRaw);
                writer.WritePropertyName("distinct_raw_values");
                writer.WriteStartArray();
                foreach (string value in channel.DistinctRawValues)
                {
                    writer.WriteStringValue(value);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("samples");
            writer.WriteStartArray();
            foreach (GpuzLogSample sample in analysis.Samples)
            {
                writer.WriteStartObject();
                writer.WriteNumber("session_index", sample.SessionIndex);
                writer.WriteString("timestamp_local", sample.TimestampLocal);
                writer.WritePropertyName("values");
                writer.WriteStartArray();
                foreach (string value in sample.Values)
                {
                    writer.WriteStringValue(value);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("warnings");
            writer.WriteStartArray();
            foreach (string warning in analysis.Warnings)
            {
                writer.WriteStringValue(warning);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string SerializeExperimentMarker(ExperimentMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", marker.SchemaVersion);
            writer.WriteString("scenario_id", marker.ScenarioId);
            writer.WriteString("phase", marker.Phase);
            writer.WriteNumber("utc_unix_ms", marker.UtcUnixMs);
            writer.WriteNumber("monotonic_ns", marker.MonotonicNs);
            writer.WriteNumber("monotonic_frequency_hz", marker.MonotonicFrequencyHz);
            if (marker.Note is null)
            {
                writer.WriteNull("note");
            }
            else
            {
                writer.WriteString("note", marker.Note);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string SerializeGpuzCorrelation(GpuzCorrelationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", report.SchemaVersion);
            writer.WriteString("source_kind", report.SourceKind);
            writer.WriteString("artifact_sha256", report.ArtifactSha256);
            writer.WriteString("reference_channel", report.ReferenceChannel);
            writer.WriteString("reference_unit", report.ReferenceUnit);
            writer.WriteNumber("sample_count", report.SampleCount);
            writer.WriteNumber("session_count", report.SessionCount);
            if (report.SelectedSessionIndex is int sessionIndex)
            {
                writer.WriteNumber("selected_session_index", sessionIndex);
            }
            else
            {
                writer.WriteNull("selected_session_index");
            }

            writer.WriteString("method", report.Method);
            writer.WritePropertyName("pairs");
            writer.WriteStartArray();
            foreach (GpuzCorrelationPair pair in report.Pairs)
            {
                writer.WriteStartObject();
                writer.WriteNumber("channel_index", pair.ChannelIndex);
                writer.WriteString("channel", pair.Channel);
                writer.WriteString("unit", pair.Unit);
                writer.WriteString("source_scope", pair.SourceScope);
                writer.WriteNumber("sample_count", pair.SampleCount);
                if (pair.Coefficient is double coefficient)
                {
                    writer.WriteNumber("coefficient", coefficient);
                }
                else
                {
                    writer.WriteNull("coefficient");
                }

                writer.WriteString("status", pair.Status);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("warnings");
            writer.WriteStartArray();
            foreach (string warning in report.Warnings)
            {
                writer.WriteStringValue(warning);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string SerializeThermChannelCorrelation(
        ThermChannelCorrelationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", report.SchemaVersion);
            writer.WriteString("source_kind", report.SourceKind);
            writer.WriteString(
                "therm_observation_sha256",
                report.ThermObservationSha256);
            writer.WriteString(
                "gpuz_log_prefix_sha256",
                report.GpuzLogPrefixSha256);
            writer.WriteNumber(
                "gpuz_log_prefix_size_bytes",
                report.GpuzLogPrefixSizeBytes);
            writer.WriteString("gpuz_sha256", report.GpuzSha256);
            writer.WriteString("nvapi_module_sha256", report.NvapiModuleSha256);
            writer.WriteString("interface_id", report.InterfaceId);
            writer.WriteString("function_rva", report.FunctionRva);
            writer.WriteString("structure_version", report.StructureVersion);
            writer.WriteNumber(
                "selected_session_index",
                report.SelectedSessionIndex);
            writer.WriteString(
                "window_first_timestamp_local",
                report.WindowFirstTimestampLocal);
            writer.WriteString(
                "window_last_timestamp_local",
                report.WindowLastTimestampLocal);
            writer.WriteNumber("tolerance_celsius", report.ToleranceCelsius);
            writer.WriteNumber(
                "combined_mean_absolute_error_celsius",
                report.CombinedMeanAbsoluteErrorCelsius);
            writer.WriteNumber(
                "alternative_combined_mean_absolute_error_celsius",
                report.AlternativeCombinedMeanAbsoluteErrorCelsius);
            writer.WriteString("mapping_status", report.MappingStatus);
            writer.WritePropertyName("mappings");
            writer.WriteStartArray();
            foreach (ThermChannelReferenceMapping mapping in report.Mappings)
            {
                writer.WriteStartObject();
                writer.WriteNumber("channel_index", mapping.ChannelIndex);
                writer.WriteString("semantic_channel", mapping.SemanticChannel);
                writer.WriteString("reference_channel", mapping.ReferenceChannel);
                writer.WriteNumber("sample_count", mapping.SampleCount);
                writer.WriteNumber(
                    "mean_absolute_error_celsius",
                    mapping.MeanAbsoluteErrorCelsius);
                writer.WriteNumber(
                    "maximum_absolute_error_celsius",
                    mapping.MaximumAbsoluteErrorCelsius);
                writer.WriteString("status", mapping.Status);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("warnings");
            writer.WriteStartArray();
            foreach (string warning in report.Warnings)
            {
                writer.WriteStringValue(warning);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string SerializeNvapiInterfaceClassification(
        NvapiInterfaceClassificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", report.SchemaVersion);
            writer.WriteString("source_kind", report.SourceKind);
            writer.WriteString(
                "observation_artifact_sha256",
                report.ObservationArtifactSha256);
            writer.WriteString(
                "interface_table_artifact_sha256",
                report.InterfaceTableArtifactSha256);
            writer.WriteString("gpuz_sha256", report.GpuzSha256);
            writer.WriteString("captured_utc", report.CapturedUtc);
            writer.WriteNumber("observation_count", report.ObservationCount);
            writer.WriteNumber(
                "observed_unique_interface_count",
                report.ObservedUniqueInterfaceCount);
            writer.WriteNumber(
                "public_catalog_match_count",
                report.PublicCatalogMatchCount);
            writer.WriteNumber(
                "not_in_public_catalog_count",
                report.NotInPublicCatalogCount);
            writer.WritePropertyName("interfaces");
            writer.WriteStartArray();
            foreach (NvapiInterfaceClassificationEntry entry in report.Interfaces)
            {
                writer.WriteStartObject();
                writer.WriteString("interface_id", entry.InterfaceId);
                writer.WriteNumber("call_count", entry.CallCount);
                writer.WriteString("classification", entry.Classification);
                if (entry.PublicFunction is null)
                {
                    writer.WriteNull("public_function");
                }
                else
                {
                    writer.WriteString("public_function", entry.PublicFunction);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("warnings");
            writer.WriteStartArray();
            foreach (string warning in report.Warnings)
            {
                writer.WriteStringValue(warning);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string SerializeNvapiCandidateInventory(
        NvapiCandidateInventoryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", report.SchemaVersion);
            writer.WriteString("source_kind", report.SourceKind);
            writer.WriteString(
                "classification_artifact_sha256",
                report.ClassificationArtifactSha256);
            writer.WriteString("call_artifact_sha256", report.CallArtifactSha256);
            writer.WriteString("gpuz_sha256", report.GpuzSha256);
            writer.WriteString("captured_utc", report.CapturedUtc);
            writer.WriteNumber("candidate_count", report.CandidateCount);
            writer.WriteNumber("executed_candidate_count", report.ExecutedCandidateCount);
            writer.WriteNumber(
                "executed_public_catalog_count",
                report.ExecutedPublicCatalogCount);
            writer.WriteNumber(
                "executed_not_in_public_catalog_count",
                report.ExecutedNotInPublicCatalogCount);
            writer.WriteNumber(
                "resolved_not_observed_count",
                report.ResolvedNotObservedCount);
            writer.WritePropertyName("candidates");
            writer.WriteStartArray();
            foreach (NvapiCandidateInventoryEntry entry in report.Candidates)
            {
                writer.WriteStartObject();
                writer.WriteString("interface_id", entry.InterfaceId);
                writer.WriteString("catalog_status", entry.CatalogStatus);
                if (entry.PublicFunction is null)
                {
                    writer.WriteNull("public_function");
                }
                else
                {
                    writer.WriteString("public_function", entry.PublicFunction);
                }

                writer.WriteString("module_name", entry.ModuleName);
                writer.WriteString("module_sha256", entry.ModuleSha256);
                writer.WriteString("rva", entry.Rva);
                writer.WriteNumber("query_count", entry.QueryCount);
                writer.WriteNumber("observed_call_count", entry.ObservedCallCount);
                writer.WriteString("execution_status", entry.ExecutionStatus);
                writer.WriteString("semantic_status", entry.SemanticStatus);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("warnings");
            writer.WriteStartArray();
            foreach (string warning in report.Warnings)
            {
                writer.WriteStringValue(warning);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string SerializeWindowsHandleIdentity(
        WindowsHandleIdentityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", report.SchemaVersion);
            writer.WriteString("source_kind", report.SourceKind);
            writer.WriteString("captured_utc", report.CapturedUtc);
            writer.WriteNumber("process_id", report.ProcessId);
            writer.WriteString("process_image_name", report.ProcessImageName);
            writer.WriteString("process_image_sha256", report.ProcessImageSha256);
            writer.WriteString("handle", report.Handle);
            writer.WriteString("object_type", report.ObjectType);
            if (report.ObjectName is null)
            {
                writer.WriteNull("object_name");
            }
            else
            {
                writer.WriteString("object_name", report.ObjectName);
            }

            if (report.DosDeviceAlias is null)
            {
                writer.WriteNull("dos_device_alias");
            }
            else
            {
                writer.WriteString("dos_device_alias", report.DosDeviceAlias);
            }

            writer.WriteString("warning", report.Warning);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static void WriteManifest(Utf8JsonWriter writer, LabPackageManifest manifest)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", manifest.SchemaVersion);
        writer.WriteString("source_kind", manifest.SourceKind);

        writer.WritePropertyName("artifact");
        writer.WriteStartObject();
        writer.WriteString("relative_path", manifest.Artifact.RelativePath);
        writer.WriteString("original_file_name", manifest.Artifact.OriginalFileName);
        writer.WriteNumber("size_bytes", manifest.Artifact.SizeBytes);
        writer.WriteString("sha256", manifest.Artifact.Sha256);
        writer.WriteEndObject();

        writer.WritePropertyName("device");
        writer.WriteStartObject();
        writer.WriteString("gpu", manifest.Device.Gpu);
        writer.WriteString("driver_version", manifest.Device.DriverVersion);
        writer.WriteString("vbios_version", manifest.Device.VbiosVersion);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
