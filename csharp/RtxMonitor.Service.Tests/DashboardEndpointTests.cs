using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using RtxMonitor.Managed;
using RtxMonitor.Service;
using RtxMonitor.Storage;

namespace RtxMonitor.Service.Tests;

internal static partial class Program
{
    private static async Task TestDashboardSnapshotExportAsync()
    {
        using var temporary = new TemporaryWorkspace();
        RtxMonitorServiceOptions options = TestOptions(temporary.DatabasePath);
        var monitoring = new MonitoringState(options);
        monitoring.MarkStorageAvailable(SqliteTelemetryStore.CurrentSchemaVersion);
        DiscoveredGpu gpu = FakeDiscoveredGpu();
        monitoring.RecordDiscoverySuccess([gpu]);
        monitoring.RecordCollectorStarted(gpu.Gpu.Uuid, "synthetic-dashboard-export");
        TelemetryEvent sample = FakeTelemetryEvent(1);
        PublicTelemetryValue seed = sample.PublicTelemetry!.Fields[0];
        PublicTelemetryValue[] fields = Enumerable.Range(0, 37).Select(index => seed with
        {
            FieldName = "synthetic_public_" + index,
            ProviderName = "synthetic NVML fixture",
            State = index < 32 ? CapabilityState.Available : CapabilityState.NotSupported,
            StateName = index < 32 ? "available" : "not_supported",
            NativeStatus = index < 32 ? 0 : 3,
            ProviderNativeId = (uint)index,
            ValueType = index == 0 ? TelemetryValueType.UnsignedInteger : TelemetryValueType.SignedInteger,
            ValueTypeName = index == 0 ? "unsigned_integer" : "signed_integer",
            UnsignedValue = index == 0 ? ulong.MaxValue : null,
            SignedValue = index is > 0 and < 32 ? index == 1 ? long.MinValue : index : null,
            DoubleValue = null,
        }).ToArray();
        monitoring.RecordTelemetry(gpu.Gpu.Uuid, sample with
        {
            PublicTelemetry = new PublicTelemetryReport(gpu.Gpu.Index, sample.ObservedAt,
                sample.ObservedAtUnixMilliseconds, fields),
        });
        var windows = new DashboardCachedWindowsSource(FakeWindowsTelemetrySnapshot());
        var profile = new ExperimentalTelemetrySnapshotState(options, monitoring);
        var research = new ResearchTelemetryState();
        JsonElement originalResearch = DashboardResearchFixture(DateTimeOffset.UtcNow);
        research.Record(originalResearch);
        int nativeResolutions = 0;

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http1));
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        builder.Services.AddSingleton<IMonitoringSnapshotSource>(monitoring);
        builder.Services.AddSingleton(new TelemetryEventHub(options));
        builder.Services.AddSingleton<IWindowsTelemetrySnapshotSource>(windows);
        builder.Services.AddSingleton<IExperimentalTelemetryPublicationSource>(profile);
        builder.Services.AddSingleton(research);
        builder.Services.AddSingleton<IMonitoringBackend>(_ =>
        {
            nativeResolutions++;
            throw new InvalidOperationException("HTTP export must never resolve a native monitoring backend");
        });
        builder.Services.AddSingleton<IWindowsGpuReader>(_ =>
        {
            nativeResolutions++;
            throw new InvalidOperationException("HTTP export must never resolve DXGI/PDH acquisition");
        });
        await using WebApplication app = builder.Build();
        DashboardEndpoints.Map(app);
        await app.StartAsync().ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = GetServerAddress(app), Timeout = TimeSpan.FromSeconds(5) };
        string route = "/api/v1/snapshot?gpu_uuid=" + Uri.EscapeDataString(gpu.Gpu.Uuid);
        string? firstResearchClock = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using HttpResponseMessage response = await client.GetAsync(route).ConfigureAwait(false);
            Check(response.StatusCode == HttpStatusCode.OK, "exportação de GPU conhecida deve retornar arquivo 200");
            Check(response.Content.Headers.ContentType?.MediaType == "application/json",
                "arquivo exportado deve declarar application/json");
            Check(response.Content.Headers.ContentDisposition?.DispositionType == "attachment",
                "exportação deve usar attachment para o navegador gravar o arquivo");
            string filename = response.Content.Headers.ContentDisposition?.FileNameStar ??
                response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? string.Empty;
            Check(filename.StartsWith("rtx3060-snapshot-", StringComparison.Ordinal) && filename.EndsWith(".json", StringComparison.Ordinal),
                "anexo deve fornecer nome de arquivo JSON da RTX3060");
            Check(response.Headers.CacheControl?.NoStore == true, "snapshot deve impedir cache HTTP");
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            JsonElement root = document.RootElement;
            Check(root.GetProperty("schema").GetString() == "rtx-monitor-dashboard-snapshot-v1",
                "exportação deve declarar seu contrato versionado");
            Check(root.GetProperty("gpu").GetProperty("uuid").GetString() == gpu.Gpu.Uuid,
                "arquivo deve identificar a GPU selecionada");
            foreach (string key in new[] { "health", "public_telemetry", "windows_telemetry", "profile_telemetry", "research_telemetry", "research_summary", "capabilities" })
                Check(root.TryGetProperty(key, out _), "arquivo deve conter a seção cacheada: " + key);
            JsonElement exportedPublic = root.GetProperty("public_telemetry");
            Check(exportedPublic.GetProperty("fields").GetArrayLength() == 37 &&
                  exportedPublic.GetProperty("coverage").GetProperty("available").GetInt32() == 32,
                "arquivo deve conservar todos os 37 campos públicos sintéticos, incluindo 32 disponíveis e as lacunas");
            Check(exportedPublic.GetProperty("fields")[0].GetProperty("value_u64").GetRawText() == "18446744073709551615" &&
                  exportedPublic.GetProperty("fields")[1].GetProperty("value_i64").GetRawText() == "-9223372036854775808",
                "serialização do anexo deve preservar uint64/int64 sem passagem por double");
            Check(exportedPublic.GetProperty("fields")[32].GetProperty("value_i64").ValueKind == JsonValueKind.Null,
                "campo público sem suporte deve permanecer nulo no arquivo");
            Check(exportedPublic.GetProperty("fields")[0].GetProperty("provider").GetString() == "synthetic NVML fixture",
                "exportação deve preservar a fonte do valor público");
            JsonElement exportedResearch = root.GetProperty("research_telemetry");
            Check(CanonicalJson(exportedResearch) == CanonicalJson(originalResearch),
                "anexo deve incluir o relatório completo da pesquisa com payloads, proveniência e horários originais");
            JsonElement readings = exportedResearch.GetProperty("collection").GetProperty("readings");
            Check(readings.GetArrayLength() == 12, "arquivo deve conter as doze leituras privadas sintéticas");
            Check(readings.EnumerateArray().Count(field => field.TryGetProperty("channel_index", out JsonElement channel) &&
                      channel.GetInt32() > 1 && field.GetProperty("physical_site").ValueKind == JsonValueKind.Null) == 7,
                "os sete canais adicionais não podem ganhar localização física durante a exportação");
            Check(exportedResearch.GetProperty("provenance").GetProperty("decimal_raw").GetRawText() ==
                  "0.1234567890123456789012345678", "exportação deve preservar o lexema decimal original");
            string researchClock = exportedResearch.GetProperty("updated_at").GetString()!;
            firstResearchClock ??= researchClock;
            Check(researchClock == firstResearchClock, "download repetido não pode renovar a aquisição privada");
            Check(root.GetProperty("research_summary").GetProperty("collection").GetProperty("suites")[0]
                    .TryGetProperty("queries", out _) == false,
                "arquivo deve separar a visão compacta do relatório completo");
        }
        foreach ((string query, HttpStatusCode expected) in new[]
        {
            ("", HttpStatusCode.BadRequest),
            ("?gpu_uuid=", HttpStatusCode.BadRequest),
            ("?gpu_uuid=%20", HttpStatusCode.BadRequest),
            ("?gpu_uuid=" + new string('x', 257), HttpStatusCode.BadRequest),
            ("?gpu_uuid=" + gpu.Gpu.Uuid + "&gpu_uuid=" + gpu.Gpu.Uuid, HttpStatusCode.BadRequest),
            ("?gpu_uuid=GPU-NOT-OBSERVED", HttpStatusCode.NotFound),
        })
        {
            using HttpResponseMessage invalid = await client.GetAsync("/api/v1/snapshot" + query).ConfigureAwait(false);
            Check(invalid.StatusCode == expected, "exportação deve rejeitar UUID ausente, inválido, repetido ou desconhecido");
        }
        using (HttpResponseMessage caseInsensitive = await client.GetAsync(route.ToLowerInvariant()).ConfigureAwait(false))
            Check(caseInsensitive.StatusCode == HttpStatusCode.OK, "seleção por UUID deve ignorar maiúsculas e minúsculas");
        GpuInfo otherInfo = gpu.Gpu with { Index = 1, Uuid = "GPU-OTHER-EXPORT-TEST" };
        BoardIdentity otherBoard = gpu.Evidence.Board! with
        {
            GpuIndex = 1,
            PciBus = 2,
            PciBusId = "00000000:02:00.0",
        };
        DiscoveredGpu otherGpu = gpu with
        {
            Gpu = otherInfo,
            Evidence = gpu.Evidence with { Gpu = otherInfo, Board = otherBoard },
        };
        monitoring.RecordDiscoverySuccess([gpu, otherGpu]);
        using (HttpResponseMessage other = await client.GetAsync("/api/v1/snapshot?gpu_uuid=" + otherInfo.Uuid).ConfigureAwait(false))
        {
            using JsonDocument otherDocument = JsonDocument.Parse(await other.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            JsonElement otherResearch = otherDocument.RootElement.GetProperty("research_telemetry");
            Check(otherResearch.GetProperty("state").GetString() == "identity_mismatch" &&
                  otherResearch.GetProperty("collection").ValueKind == JsonValueKind.Null &&
                  otherDocument.RootElement.GetProperty("research_summary").GetProperty("collection").ValueKind == JsonValueKind.Null,
                "exportação de outra GPU conhecida deve retirar ambas as visões das leituras privadas da primeira GPU");
            Check(otherDocument.RootElement.GetProperty("public_telemetry").ValueKind == JsonValueKind.Null &&
                  otherDocument.RootElement.GetProperty("windows_telemetry").ValueKind == JsonValueKind.Null,
                "fontes ainda não capturadas para a outra GPU devem permanecer nulas");
        }
        using (HttpResponseMessage unchanged = await client.GetAsync(route).ConfigureAwait(false))
        {
            using JsonDocument matching = JsonDocument.Parse(await unchanged.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            Check(matching.RootElement.GetProperty("research_telemetry").GetProperty("collection").GetProperty("readings").GetArrayLength() == 12,
                "rejeitar uma exportação de outra GPU não pode alterar a publicação válida do coletor");
        }
        research.Record(DashboardResearchFixture(DateTimeOffset.UtcNow, includeIdentity: false));
        using (HttpResponseMessage missingIdentity = await client.GetAsync(route).ConfigureAwait(false))
        {
            using JsonDocument missing = JsonDocument.Parse(await missingIdentity.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            Check(missing.RootElement.GetProperty("research_telemetry").GetProperty("state").GetString() == "identity_unavailable" &&
                  missing.RootElement.GetProperty("research_telemetry").GetProperty("collection").ValueKind == JsonValueKind.Null,
                "payload privado sem identidade completa deve ser retirado da exportação");
        }
        research.Unavailable("worker_unavailable", "synthetic offline failure");
        using (HttpResponseMessage failedResearch = await client.GetAsync(route).ConfigureAwait(false))
        {
            using JsonDocument unavailable = JsonDocument.Parse(await failedResearch.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            Check(unavailable.RootElement.GetProperty("research_telemetry").GetProperty("collection").ValueKind == JsonValueKind.Null &&
                  unavailable.RootElement.GetProperty("research_summary").GetProperty("collection").ValueKind == JsonValueKind.Null,
                "arquivo após falha não pode reutilizar os números privados anteriores");
        }
        Check(nativeResolutions == 0, "GET de exportação não pode iniciar aquisição NVML/NVAPI/DXGI/PDH");
        await app.StopAsync().ConfigureAwait(false);
    }

    private static JsonElement DashboardResearchFixture(DateTimeOffset captured, bool includeIdentity = true)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("state", "available");
            writer.WriteBoolean("settings_writes_requested", false);
            writer.WriteString("updated_at", captured);
            writer.WriteString("observed_at", captured);
            writer.WriteString("fixture", "synthetic-no-hardware");
            if (includeIdentity)
            {
                writer.WriteStartObject("identity");
                writer.WriteString("gpu_uuid", FakeGpu().Uuid);
                writer.WriteString("driver_version", FakeGpu().DriverVersion);
                writer.WriteString("pci_bus_id", "00000000:01:00.0");
                writer.WriteString("device_id", "0x250410de");
                writer.WriteString("subsystem_id", "0x153610de");
                writer.WriteString("vbios_version", "TEST-VBIOS");
                writer.WriteEndObject();
            }
            else writer.WriteNull("identity");
            writer.WriteStartObject("provenance");
            writer.WriteNumber("unsigned_raw", ulong.MaxValue);
            writer.WriteNumber("signed_raw", long.MinValue);
            writer.WritePropertyName("decimal_raw");
            writer.WriteRawValue("0.1234567890123456789012345678");
            writer.WriteEndObject();
            writer.WriteStartObject("collection");
            writer.WriteNumber("expected_suites", 1);
            writer.WriteNumber("completed_suites", 1);
            writer.WriteStartArray("readings");
            string[] fields = ["gpu_core_voltage_v", "gpu_die_temperature_c", "gpu_hotspot_temperature_c",
                .. Enumerable.Range(0, 9).Select(index => $"thermal_channel_{index}_temperature_c")];
            for (int index = 0; index < fields.Length; index++)
            {
                writer.WriteStartObject();
                writer.WriteString("field", fields[index]);
                writer.WriteString("state", "available");
                writer.WriteString("captured_at", captured);
                writer.WriteNumber("value", index == 0 ? .9 : 40 + index);
                writer.WriteString("source", "synthetic private profile");
                if (index >= 3)
                {
                    writer.WriteNumber("channel_index", index - 3);
                    writer.WriteNull("physical_site");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("suites");
            writer.WriteStartObject();
            writer.WriteString("suite", "synthetic_cached_suite");
            writer.WriteString("state", "available");
            writer.WriteString("captured_at", captured);
            writer.WriteStartArray("queries");
            writer.WriteStartObject();
            writer.WriteString("id", "0x65fe3aad");
            writer.WriteNumber("status", 0);
            writer.WriteBoolean("available", true);
            writer.WriteString("raw_after_hex", new string('a', 8192));
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private sealed class DashboardCachedWindowsSource(WindowsTelemetrySnapshot snapshot)
        : IWindowsTelemetrySnapshotSource
    {
        public WindowsTelemetrySnapshot? GetSnapshot(string gpuUuid) =>
            string.Equals(gpuUuid, snapshot.Gpu.Uuid, StringComparison.OrdinalIgnoreCase) ? snapshot : null;
    }
}
