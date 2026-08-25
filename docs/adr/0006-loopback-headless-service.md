# ADR 0006: serviço local headless em loopback

- Status: aceito
- Data: 2026-08-25

## Contexto

A v0.5.0 tornou o stream persistente, mas a coleta ainda pertence ao processo de terminal. Cada consumidor que inicia seu próprio monitor pode duplicar polling, criar runs concorrentes para a mesma GPU e receber estados diferentes durante uma recuperação do driver.

A próxima camada precisa manter a aquisição ativa sem interface gráfica, oferecer um ponto local único para clientes e continuar funcionando quando nenhum cliente estiver conectado. Ela também precisa representar indisponibilidade de driver, GPU ou SQLite sem transformar um erro em leitura atual.

## Decisão

Criar `RtxMonitor.Service`, um host ASP.NET Core executável como console ou Windows Service. Ele usa `BackgroundService` para supervisionar dependências e Kestrel para expor HTTP/1.1 exclusivamente em `127.0.0.1`.

O serviço possui:

- um supervisor de SQLite e descoberta de GPUs;
- no máximo um coletor ativo por UUID;
- um `ResilientSampler` independente por GPU;
- o mesmo `RtxMonitor.Storage` da v0.5.0;
- snapshots imutáveis para saúde, GPUs e capabilities;
- um broker SSE com uma fila limitada por cliente.

O SQLite é obrigatório para o serviço. Um evento é confirmado no banco antes de ser publicado ao SSE. Se o armazenamento falhar, os coletores são encerrados e o supervisor tenta reabrir o banco; o serviço HTTP permanece ativo para informar o diagnóstico em `/health`.

## API v1

| Endpoint | Responsabilidade |
| --- | --- |
| `GET /health` | Estado do serviço, SQLite, discovery, coletores e clientes SSE |
| `GET /api/v1/gpus` | GPUs conhecidas, presença e último estado do coletor |
| `GET /api/v1/gpus/{uuid}/capabilities` | Último inventário público NVML/NVAPI |
| `GET /api/v1/events` | Eventos persistidos ao vivo por SSE |
| `GET /api/v1/history` | Consulta SQLite limitada e filtrável |

O contrato HTTP está em `docs/openapi/service-v1.openapi.json`. O envelope de telemetria ao vivo está em `docs/schema/live-telemetry-v1.schema.json`.

`/api/v1/gpus` chama a última leitura válida de `last_sample_temperature_c` e informa seu timestamp. Durante um `gap`, o serviço preserva esse histórico, mas não o apresenta como temperatura atual.

## Backpressure e recuperação

Cada cliente SSE possui seu próprio `Channel<T>` limitado. O coletor usa `TryWrite`; portanto, uma conexão lenta nunca bloqueia aquisição nem persistência.

Quando a fila de um cliente fica cheia:

1. o broker descarta somente a entrega ao cliente lento;
2. o evento permanece confirmado no SQLite;
3. novas entregas para esse cliente também entram na mesma lacuna até o aviso ser consumido, preservando a ordem do stream;
4. o stream envia `event: stream_gap` assim que volta a escrever;
5. o payload informa o último `event_id` entregue e o maior descartado;
6. o cliente recupera o intervalo em `/api/v1/history?order=asc&after_event_id=...`, mantendo o filtro por GPU quando houver.

O `id` do SSE é o `event_id` persistido, não um contador volátil separado. A quantidade de clientes, capacidade da fila e tamanho máximo de consulta são limitados por configuração.

## Concorrência e ciclo de vida

O dicionário do supervisor é indexado por UUID e impede dois coletores simultâneos para a mesma GPU. Novas descobertas atualizam identidade e capabilities, mas não iniciam outro sampler enquanto o anterior estiver ativo.

No desligamento:

1. o host cancela o supervisor;
2. o supervisor cancela todos os coletores;
3. cada coletor encerra seu sampler;
4. o run recebe `completed_at` e `completion_reason=service_stopped`;
5. somente então o processo termina.

Falhas de driver são registradas no estado de discovery e tentadas novamente. Falhas recuperáveis durante uma coleta continuam usando os eventos `gap` e `recovered` do sampler. Falhas do SQLite interrompem a sessão persistente inteira e iniciam uma nova tentativa controlada.

## Segurança

- Kestrel usa um endpoint criado em código para `127.0.0.1`; `--urls` e variáveis `ASPNETCORE_URLS` não ampliam o bind.
- Não há CORS, proxy reverso, autenticação remota ou endpoint de escrita.
- Todas as rotas da v1 são `GET`.
- O serviço não adiciona acesso MMIO, I2C, firmware ou operações de configuração da GPU.
- Instalação e remoção do Windows Service exigem um PowerShell elevado; remover o serviço não remove o banco.

HTTP sem TLS é aceito apenas porque a conexão não sai do host. Exposição remota futura exigirá outro ADR, autenticação e TLS; alterar somente o endereço de bind não será considerado configuração suportada.

## Operação

`Microsoft.Extensions.Hosting.WindowsServices` integra o mesmo executável ao Service Control Manager. O diretório de publicação permanece uma unidade porque contém configuração e bibliotecas nativas. O banco padrão fica em `%ProgramData%\RtxMonitor\telemetry.db`.

Os scripts de publicação e instalação verificam a presença do executável, `rtxmon_native.dll` e `appsettings.json`. A recuperação do Windows Service reinicia o processo após falhas fatais; indisponibilidades esperadas de banco ou driver são tratadas dentro do próprio supervisor.

## Consequências

- Clientes deixam de abrir uma sessão nativa própria para acompanhar dados ao vivo.
- SQLite passa a ser uma dependência obrigatória apenas do serviço; os CLIs continuam funcionando sem banco.
- Um cliente SSE pode perder entrega ao vivo, mas recebe uma lacuna explícita e pode recuperar todos os eventos persistidos.
- A v0.6.0 não adiciona GUI, acesso remoto, controle da GPU nem sensores experimentais.

## Alternativas rejeitadas

### Um sampler por cliente HTTP

Rejeitado porque duplica polling e torna o estado de recuperação dependente da quantidade de consumidores.

### Fila SSE ilimitada

Rejeitada porque um cliente parado poderia consumir memória indefinidamente.

### Bloquear o produtor quando um cliente fica lento

Rejeitado porque entrega de rede não pode atrasar a confirmação de evidências nem a próxima amostra.

### Escutar em todas as interfaces por padrão

Rejeitado porque a API ainda não possui autenticação nem TLS e foi projetada como fronteira local entre aquisição e futuros clientes.

## Referências

- [Microsoft: Windows Service com `BackgroundService`](https://learn.microsoft.com/dotnet/core/extensions/windows-service)
- [Microsoft: endpoints Kestrel em código](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-8.0)
- [Microsoft: canais limitados e backpressure](https://learn.microsoft.com/dotnet/core/extensions/channels)
