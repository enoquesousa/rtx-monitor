# Operação do serviço local

## Provedor independente da v0.11

O provedor opt-in usa acesso direto ao driver para hotspot e tensão do perfil exato. A API `/api/v1/gpus/{gpuUuid}/profile-telemetry` publica o relatório v1 após commit SQLite; SSE/histórico usam eventos v8. O modo padrão mantém v5. [Configuração, migração e limites](PROFILE_TELEMETRY.md).

O critério revisado de fidelidade à fonte foi autorizado em 2026-09-06. As divergências assíncronas anteriores permanecem documentadas e não são apresentadas como precisão física validada. [Verificação da entrega](research/2026-09-06-v011-stable-validation.md). A instalação ativa foi atualizada para 0.11.0, preservando banco, conta LocalSystem e inicialização automática. Os binários 0.6.0 permanecem para reversão. [Validação operacional](research/2026-09-06-v011-operational-validation.md). Os resultados da v0.10 abaixo são históricos.

## Visão geral

`RtxMonitor.Service` executa a aquisição, persistência e API local em um único processo headless. O mesmo binário funciona em um terminal e como Windows Service.

O produto **0.10.0, concluído localmente**, expõe `fan_speed_intended_rpm` em `rpm` por índice NVML quando disponível, preservando nulos em falhas. É velocidade pretendida pelo contrato público, sem comprovação de tacômetro físico ou PWM. O HTTP de telemetria mantém schema 2 e os eventos persistidos/SSE usam schema 5, com históricos anteriores legíveis. Na [validação de 2026-09-05](research/2026-09-05-public-fan-rpm.md), a implementação foi executada em serviço temporário; a instalação existente 0.6.0 permaneceu separada e não recebeu este incremento.

O CI passou 33 CTest no Windows e 29 CTest no snapshot Linux de portabilidade, além de 14 testes de auditoria por plataforma e suítes .NET aplicáveis; a suíte Service passou no Windows. O teste Linux não comprova execução física da GPU nem substitui a validação do Windows Service. Três ciclos públicos, smoke final do build em serviço isolado na porta 11882 e smoke do pacote passaram. A preservação final confirmou serviço 0.6.0 saudável com mesmo início/PIDs. A pesquisa privada é parcial e não adiciona seus campos brutos ao serviço.

O serviço não possui interface gráfica e não aceita conexões externas. Seu endpoint é sempre:

```text
http://127.0.0.1:<porta>
```

O padrão é `http://127.0.0.1:5136`.

A publicação atual é dependente do framework. Para executá-la em uma máquina sem o SDK, instale o **ASP.NET Core Runtime 8 x64**. O SDK .NET 8 já contém esse runtime durante o desenvolvimento.

## Fronteira de acesso e credenciais

A API confia no computador local. Ela não possui contas, senhas de aplicação, cookies de sessão, JWT, Bearer tokens ou refresh tokens. Qualquer processo ou usuário que consiga se conectar ao loopback pode consultar os mesmos dados; o serviço não diferencia usuários do Windows. Os cabeçalhos abaixo restringem requisições de navegador e encaminhamento acidental, sem transformar essa fronteira em autenticação de usuário.

| Componente | Responsabilidade |
| --- | --- |
| `ServiceApplication` | Valida a configuração de transporte antes de iniciar workers, cria o único listener HTTP/1.1 em `127.0.0.1` e instala o guard local |
| Guard HTTP local | Verifica os endereços reais do socket, `Host`, `Origin` e metadados do navegador antes de executar as rotas |
| `ServiceEndpoints` | Entrega snapshots, consultas limitadas de histórico e assinaturas SSE; todas as operações expostas são de leitura |
| `GpuMonitoringWorker` / `WindowsTelemetryWorker` | Coletam dados com a identidade do processo; a requisição HTTP não inicia uma coleta de hardware |
| `TelemetryStoreProvider` / `TelemetryEventHub` | Consultam o SQLite e distribuem eventos já confirmados, com limites de histórico e de filas |

Uma requisição passa pelo seguinte fluxo:

1. O cliente abre uma conexão direta com `127.0.0.1:<porta>`. O endereço remoto e o endereço local reais precisam ser loopback; endereço ausente é recusado.
2. `Host` precisa conter `localhost` ou um IP loopback canônico, com a porta efetiva do socket; somente a porta HTTP padrão 80 pode ser omitida. Um nome externo que resolva para loopback não é aceito. Isso impede que uma página de outro domínio leia a API por DNS rebinding.
3. Quando presente, `Origin` precisa ser HTTP com exatamente a mesma autoridade e porta do pedido. `Sec-Fetch-Site`, quando presente, aceita somente `same-origin` ou `none`. Clientes nativos como `curl` podem omitir esses cabeçalhos.
4. Cabeçalhos `Forwarded`, `X-Forwarded-*` e `X-Original-*` são recusados. Uma requisição que viole a política recebe `403` antes de consultar histórico ou reservar uma assinatura SSE; requisições HTTP malformadas podem ser recusadas pelo próprio Kestrel com `400`.
5. A rota autorizada pela fronteira local devolve o snapshot, consulta o banco ou acompanha o broker. Respostas usam `Cache-Control: no-store`; o SSE também mantém `no-cache`.

Não exponha essa API por proxy, túnel ou encaminhamento de porta para outros computadores. Um proxy local pode ocultar a origem remota e apresentar cabeçalhos locais válidos; o guard não autentica seus clientes. HTTP não cifra o tráfego, e os dados persistidos e logs não são cifrados pelo serviço. `/health` inclui o caminho do banco e diagnósticos; a telemetria e o histórico incluem identificadores da GPU e da execução. Proteja esses arquivos e a pasta publicada com as permissões do sistema operacional, especialmente ao executar como `LocalSystem`.

O serviço não guarda credenciais de clientes para renovar ou revogar. Sua identidade de Windows Service é gerenciada pelo Service Control Manager. Os fluxos experimentais do CLI usam um worker filho por stdio e gates de compatibilidade do hardware; não são endpoints HTTP nem um mecanismo de login. Os limites e permissões dessas operações estão nos ADRs do laboratório.

Na automação de build, `GITHUB_TOKEN` tem somente `contents: read` e os checkouts usam `persist-credentials: false`. Esse token pertence ao job do GitHub Actions; ele não é uma credencial para acessar o serviço HTTP e não é gravado no aplicativo publicado.

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

`--urls`, `ASPNETCORE_URLS` e as opções genéricas de portas não substituem o endpoint de loopback criado em código. Altere a porta por `RtxMonitor:Port`.

Configurar `Kestrel:Endpoints`, ativar `PreferHostingUrls` ou ativar o processamento automático de forwarded headers (`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`) interrompe a inicialização antes dos workers e do SQLite. O loader de endpoints é isolado, sem recarregamento, para que uma alteração posterior de `appsettings.json` não acrescente listeners. A exposição remota exige outro modelo de autenticação e transporte; não é uma opção deste serviço.

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
  -OutputDirectory 'C:\Program Files\RtxMonitor\0.10.0'
```

## Instalar no Windows

Abra o PowerShell como Administrador:

```powershell
.\scripts\install-service.ps1 `
  -PublishDirectory 'C:\Program Files\RtxMonitor\0.10.0' `
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

Use o [procedimento de atualização reversível](UPGRADING.md). O script
`update-service.ps1` valida o ZIP e apresenta o plano por padrão; somente
`-Apply`, em PowerShell elevado, prepara a pasta versionada, cria backup
com o serviço parado e altera o caminho do executável no SCM. A conta,
a inicialização, a recuperação e o banco permanecem preservados.

Evite remover e recriar o serviço para uma simples troca de versão. A pasta
anterior permite reverter o binário, após validar compatibilidade do banco.

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
