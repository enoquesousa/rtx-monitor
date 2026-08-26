# Operação do serviço local

## Visão geral

`RtxMonitor.Service` executa a aquisição, persistência e API local em um único processo headless. O mesmo binário funciona em um terminal e como Windows Service.

O serviço não possui interface gráfica e não aceita conexões externas. Seu endpoint é sempre:

```text
http://127.0.0.1:<porta>
```

O padrão é `http://127.0.0.1:5136`.

A publicação atual é dependente do framework. Para executá-la em uma máquina sem o SDK, instale o **ASP.NET Core Runtime 8 x64**. O SDK .NET 8 já contém esse runtime durante o desenvolvimento.

## Configuração

As opções ficam na seção `RtxMonitor` de `appsettings.json`:

| Opção | Padrão | Limite |
| --- | ---: | --- |
| `Port` | `5136` | 1–65535 |
| `DatabasePath` | `%ProgramData%\RtxMonitor\telemetry.db` | Caminho local válido |
| `IntervalMilliseconds` | `1000` | 100–60000 |
| `BufferCapacity` | `256` | 1–65536 |
| `RetentionDays` | `30` | 1–3650 |
| `DiscoveryIntervalSeconds` | `15` | 1–3600 |
| `DependencyRetrySeconds` | `5` | 1–300 |
| `SseClientQueueCapacity` | `256` | 1–8192 por cliente |
| `MaximumSseClients` | `32` | 1–256 |
| `SseHeartbeatSeconds` | `15` | 1–300 |
| `HistoryMaximumLimit` | `1000` | 1–10000 |
| `AlertThresholdC` | `null` | 0–500 ou desativado |
| `AlertHysteresisC` | `0` | 0 até o limiar |
| `MetricWindowMilliseconds` | `5000` | 100–3600000 |
| `MetricTemperatureThresholdC` | `80` | 0–500 |
| `MetricMaximumSamples` | `1024` | 2–65536 |

Uma variável de ambiente usa dois sublinhados, por exemplo:

```powershell
$env:RtxMonitor__Port = '5137'
```

Um argumento de processo tem prioridade maior:

```powershell
.\RtxMonitor.Service.exe --RtxMonitor:Port=5137
```

`--urls` e `ASPNETCORE_URLS` não substituem o endpoint de loopback criado em código.

Um `DatabasePath` relativo é resolvido a partir da pasta do executável, não de `C:\Windows\System32`. Para um serviço instalado, prefira um caminho absoluto ou mantenha o padrão em `%ProgramData%`.

## Executar no terminal

```powershell
.\scripts\build.ps1 -Configuration Release

.\csharp\RtxMonitor.Service\bin\Release\net8.0-windows\win-x64\RtxMonitor.Service.exe `
  --RtxMonitor:DatabasePath=.\service.db
```

Pressione `Ctrl+C` para um desligamento gracioso. Cada run ativo será encerrado como `service_stopped`.

## Publicar

```powershell
.\scripts\publish-service.ps1 -Configuration Release
```

O padrão é `artifacts\service\win-x64`. A publicação é uma pasta indivisível: mantenha o executável, `appsettings.json`, `rtxmon_native.dll` e as dependências no mesmo diretório.

Para criar uma publicação versionada:

```powershell
.\scripts\publish-service.ps1 `
  -Configuration Release `
  -OutputDirectory 'C:\Program Files\RtxMonitor\0.8.0'
```

## Instalar no Windows

Abra o PowerShell como Administrador:

```powershell
.\scripts\install-service.ps1 `
  -PublishDirectory 'C:\Program Files\RtxMonitor\0.8.0' `
  -Start
```

O serviço usa o nome `RtxMonitorService`, inicialização automática e três tentativas progressivas de reinício após uma falha fatal.

Comandos operacionais:

```powershell
Get-Service RtxMonitorService
Stop-Service RtxMonitorService
Start-Service RtxMonitorService
Invoke-RestMethod http://127.0.0.1:5136/health
```

Por padrão, o Service Control Manager inicia o processo como `LocalSystem`. Se outra identidade for configurada manualmente, ela precisa de leitura na pasta publicada e leitura/escrita no diretório do banco.

## Atualizar

Publique a nova versão em outra pasta antes da janela de parada. Depois, em um PowerShell elevado:

```powershell
Stop-Service RtxMonitorService
.\scripts\uninstall-service.ps1 -Confirm:$false
.\scripts\install-service.ps1 `
  -PublishDirectory 'C:\Program Files\RtxMonitor\NOVA-VERSAO' `
  -Start
Invoke-RestMethod http://127.0.0.1:5136/health
```

O script de remoção não apaga a publicação nem `%ProgramData%\RtxMonitor\telemetry.db`. Assim, trocar o executável não apaga o histórico.

## Consumir eventos

Consulte o último catálogo público confirmado para uma GPU:

```powershell
Invoke-RestMethod `
  'http://127.0.0.1:5136/api/v1/gpus/GPU-.../telemetry'
```

A resposta inclui identidade de GPU/placa, cobertura, estado e proveniência de cada campo e as quatro métricas calculadas. O endpoint lê o snapshot em memória; ele não consulta a GPU sob demanda. Antes da primeira amostra válida, retorna HTTP 503.

```powershell
curl.exe -N http://127.0.0.1:5136/api/v1/events
```

Cada evento `telemetry` usa o `event_id` SQLite como `id` SSE. Para acompanhar apenas uma GPU:

```powershell
curl.exe -N 'http://127.0.0.1:5136/api/v1/events?gpu_uuid=GPU-...'
```

Se a fila privada do cliente encher, ele recebe `event: stream_gap`. Use o `recovery_endpoint` informado no payload ou consulte manualmente:

```powershell
Invoke-RestMethod `
  'http://127.0.0.1:5136/api/v1/history?order=asc&after_event_id=ULTIMO_ID'
```

Quando o stream possui `gpu_uuid`, o `recovery_endpoint` preserva o mesmo filtro. A consulta é limitada: se o intervalo exceder `limit`, repita a chamada usando o `last_event_id` da página anterior até alcançar `latest_dropped_event_id`.

O descarte ocorre apenas na entrega ao cliente lento. O evento já estava persistido antes de entrar no broker.

## Estados de saúde

| Estado | Significado |
| --- | --- |
| `starting` | SQLite ou primeira descoberta ainda não concluídos |
| `healthy` | SQLite, descoberta e todos os coletores estão normais |
| `degraded` | API e histórico funcionam, mas GPU, driver ou coletor possuem diagnóstico |
| `unavailable` | SQLite não está pronto; `/health` retorna HTTP 503 |
| `stopped` | Supervisor encerrado |

Uma falha do driver não impede consulta ao histórico. Uma falha do SQLite interrompe coletores porque o serviço nunca publica um evento sem antes confirmar sua evidência.

## Contratos

- [OpenAPI v1](openapi/service-v1.openapi.json)
- [Envelope SSE de telemetria](schema/live-telemetry-v1.schema.json)
- [Lacuna de cliente SSE](schema/stream-gap-v1.schema.json)
- [ADR 0006](adr/0006-loopback-headless-service.md)
- [Catálogo público](PUBLIC_TELEMETRY.md)
- [ADR 0007](adr/0007-public-telemetry-and-computed-metrics.md)
