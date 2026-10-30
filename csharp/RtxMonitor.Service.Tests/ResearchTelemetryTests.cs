using System.Text.Json;
using RtxMonitor.Service;

namespace RtxMonitor.Service.Tests;

internal static partial class Program
{
    private static void TestResearchTelemetryPublication()
    {
        var state = new ResearchTelemetryState();
        Check(state.GetSnapshot().GetProperty("state").GetString() == "disabled",
            "pesquisa deve começar sem números ou dados históricos");
        using (JsonDocument original = JsonDocument.Parse(ResearchFixture(DateTimeOffset.UtcNow)))
            state.Record(original.RootElement);
        JsonElement current = state.GetSnapshot();
        Check(current.GetProperty("collection").GetProperty("readings")[0].GetProperty("value").GetDouble() == .9,
            "publicação deve conservar o valor da aquisição sintética válida após descarte do documento original");
        string cached = current.GetRawText();
        Check(state.GetSnapshot().GetRawText() == cached,
            "consultas ao snapshot não podem renovar o relógio de aquisição");

        string valid = ResearchFixture(DateTimeOffset.UtcNow);
        string[] invalid =
        [
            valid.Replace("\"schema_version\":1", "\"schema_version\":\"1\"", StringComparison.Ordinal),
            valid.Replace("\"schema_version\":1", "\"schema_version\":9999999999999999999999", StringComparison.Ordinal),
            valid.Replace("\"settings_writes_requested\":false", "\"settings_writes_requested\":true", StringComparison.Ordinal),
            valid.Replace("\"updated_at\":\"", "\"wrong_timestamp_key\":\"", StringComparison.Ordinal),
            ResearchFixture(DateTimeOffset.UtcNow.AddMinutes(-5)),
            ResearchFixture(DateTimeOffset.UtcNow.AddMinutes(5)),
            "[]",
        ];
        foreach (string text in invalid)
        {
            using JsonDocument report = JsonDocument.Parse(text);
            bool rejected = false;
            try { state.Record(report.RootElement); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "relatório malformado/obsoleto deve falhar pelo contrato de dados, sem encerrar o host");
        }

        state.Unavailable("worker_unavailable", "synthetic offline worker failure");
        JsonElement unavailable = state.GetSnapshot();
        Check(unavailable.GetProperty("collection").ValueKind == JsonValueKind.Null &&
              unavailable.GetProperty("observed_at").ValueKind == JsonValueKind.Null,
            "falha do coletor deve retirar o payload e horário das leituras anteriores");
        Check(unavailable.GetProperty("state").GetString() == "worker_unavailable",
            "motivo de falha deve permanecer consultável sem valores antigos");
        TestResearchTelemetryCompactSummary();
    }

    private static void TestResearchTelemetryCompactSummary()
    {
        string captured = DateTimeOffset.UtcNow.ToUniversalTime().ToString("O");
        string report = """
            {"schema_version":1,"state":"available","settings_writes_requested":false,
             "updated_at":"__CAPTURED_AT__","observed_at":"__CAPTURED_AT__",
             "identity":{"name":"Synthetic RTX3060","driver_version":"621.01"},
             "limits":["synthetic-no-hardware","unknown physical sites remain null"],
             "provenance":{"source":"synthetic","raw_u64":18446744073709551615},
             "collection":{"expected_suites":2,"completed_suites":2,"active_suite":null,"cycle":4,
              "readings":[{"field":"synthetic_exact_decimal","state":"available",
                "captured_at":"__CAPTURED_AT__","value":0.1234567890123456789012345678,
                "raw_value":18446744073709551615,"source":"private_profile"}],
              "suites":[
               {"suite":"private-a","state":"available","captured_at":"__CAPTURED_AT__",
                "max_age_ms":600000,"age_ms":2,"captured_started_utc":"__CAPTURED_AT__",
                "queries":[
                 {"id":"0xbc8163d","status":0,"available":true,"value":0},
                 {"id":"0x0bc8163d","status":0,"available":true,"value":0},
                 {"id":197662269,"status":0,"available":true,"value":0},
                 {"id":"nvmlDeviceGetTemperature","status":0,"available":true,"value":0},
                 {"id":"0xdeadbeef","status":null,"available":false,"value":null},
                 {"id":"0x391386d1","status":0,"available":false,"value":null},
                 {"id":"0x28766157","status":-104,"available":false,"value":null}],
                "data":{"raw_blob":"__RAW_A__"}},
               {"suite":"public-b","state":"available","captured_at":"__CAPTURED_AT__",
                "max_age_ms":600000,"age_ms":3,
                "queries":[
                 {"id":"cuDeviceGetAttribute","status":0,"available":true,"value":0},
                 {"id":"0X65FE3AAD","status":0,"available":true,"value":0}],
                "data":{"raw_blob":"__RAW_B__"}}]}}
            """
            .Replace("__CAPTURED_AT__", captured, StringComparison.Ordinal)
            .Replace("__RAW_A__", new string('a', 8192), StringComparison.Ordinal)
            .Replace("__RAW_B__", new string('b', 8192), StringComparison.Ordinal);
        var state = new ResearchTelemetryState();
        using (JsonDocument source = JsonDocument.Parse(report)) state.Record(source.RootElement);
        JsonElement full = state.GetSnapshot();
        JsonElement summary = state.GetSnapshot(summary: true);
        Check(full.GetRawText() == report,
            "a leitura completa deve conservar integralmente o relatório original ao adicionar a visualização compacta");
        foreach (string key in new[] { "identity", "limits", "provenance", "updated_at", "observed_at" })
            Check(CanonicalJson(summary.GetProperty(key)) == CanonicalJson(full.GetProperty(key)),
                "resumo deve preservar identidade, limites, proveniência e relógios originais: " + key);
        JsonElement compactCollection = summary.GetProperty("collection");
        JsonElement fullCollection = full.GetProperty("collection");
        foreach (string key in new[] { "expected_suites", "completed_suites", "active_suite", "cycle", "readings" })
            Check(CanonicalJson(compactCollection.GetProperty(key)) == CanonicalJson(fullCollection.GetProperty(key)),
                "resumo deve conservar as leituras e os metadados da coleta: " + key);
        JsonElement reading = compactCollection.GetProperty("readings")[0];
        Check(reading.GetProperty("raw_value").GetRawText() == "18446744073709551615" &&
              reading.GetProperty("value").GetRawText() == "0.1234567890123456789012345678",
            "resumo não pode arredondar uint64 ou converter lexemas decimais por double");
        JsonElement suites = compactCollection.GetProperty("suites");
        Check(suites.GetArrayLength() == 2, "resumo deve conservar o inventário de suítes");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "suite", "state", "captured_at", "max_age_ms", "age_ms", "query_count", "available_count",
        };
        foreach (JsonElement suite in suites.EnumerateArray())
        {
            Check(suite.EnumerateObject().All(property => allowed.Contains(property.Name)),
                "resumo da suíte deve omitir buffers, queries, data e outros payloads de diagnóstico");
            Check(suite.GetProperty("captured_at").GetString() == captured &&
                  suite.GetProperty("max_age_ms").GetInt32() == 600000,
                "resumo da suíte deve conservar o relógio e a validade da aquisição");
        }
        Check(suites[0].GetProperty("query_count").GetInt32() == 7 &&
              suites[0].GetProperty("available_count").GetInt32() == 4 &&
              suites[1].GetProperty("query_count").GetInt32() == 2 &&
              suites[1].GetProperty("available_count").GetInt32() == 2,
            "contadores compactos devem corresponder aos retornos de cada suíte");
        JsonElement counts = compactCollection.GetProperty("counts");
        Check(counts.GetProperty("query_records").GetInt32() == 9 &&
              counts.GetProperty("available_output").GetInt32() == 6 &&
              counts.GetProperty("success_without_available_payload").GetInt32() == 1,
            "contadores globais devem separar saídas observadas, erros e sucesso sem payload");
        Check(counts.GetProperty("unique_ids_invoked").GetInt32() == 4,
            "IDs devem unificar aliases hex/número, ignorar chamadas não executadas e excluir nomes NVML/CUDA");
        Check(summary.GetRawText().Length < full.GetRawText().Length / 3,
            "resumo deve retirar payloads brutos grandes para reduzir transferência e análise no navegador");
        Check(state.GetSnapshot(summary: true).GetRawText() == summary.GetRawText(),
            "leituras compactas repetidas devem usar a mesma publicação e não renovar o horário");
        state.Unavailable("worker_unavailable", "synthetic compact publication failure");
        JsonElement failed = state.GetSnapshot(summary: true);
        Check(failed.GetProperty("collection").ValueKind == JsonValueKind.Null &&
              failed.GetProperty("observed_at").ValueKind == JsonValueKind.Null,
            "indisponibilidade deve retirar também os números da visualização compacta");
    }

    private static string ResearchFixture(DateTimeOffset updatedAt) =>
        "{\"schema_version\":1,\"state\":\"available\",\"settings_writes_requested\":false," +
        "\"updated_at\":\"" + updatedAt.ToUniversalTime().ToString("O") + "\"," +
        "\"observed_at\":\"" + updatedAt.ToUniversalTime().ToString("O") + "\"," +
        "\"identity\":{\"fixture\":\"synthetic-no-hardware\"}," +
        "\"collection\":{\"readings\":[{\"field\":\"gpu_core_voltage_v\",\"state\":\"available\",\"value\":0.9}]}}";
}
