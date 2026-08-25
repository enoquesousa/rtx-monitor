# Validação

## Critérios de aceite

Uma entrega é considerada válida quando:

1. C e C++ compilam com `/W4 /WX` no MSVC;
2. C# compila com warnings tratados como erro;
3. os testes de tamanho/layout da ABI passam;
4. C, C++ e C# retornam uma temperatura plausível para o mesmo UUID;
5. o backend informado é a API NVML moderna ou o fallback explicitamente rotulado;
6. uma consulta independente do `nvidia-smi` fica dentro de 5 °C das leituras sequenciais;
7. os modos watch produzem a quantidade solicitada de amostras;
8. C++ e C# retornam a mesma identidade PCI/VBIOS e o mesmo conjunto de capabilities;
9. o campo público de temperatura da memória sempre gera um registro, inclusive quando não suportado;
10. o JSON Schema de capabilities declara `schema_version = 2`;
11. os JSON Schemas v1 e v2 permanecem disponíveis para históricos, enquanto o v3 declara os cinco tipos e os relatórios enriquecidos;
12. testes sem GPU reproduzem gap, backoff limitado, recuperação e mudança de índice para o mesmo UUID;
13. o buffer circular descarta somente os eventos mais antigos ao atingir sua capacidade;
14. C++ e C# selecionam o mesmo UUID e emitem envelopes v3 equivalentes em um stream saudável;
15. `--alert-threshold` fora de `--watch` e `--alert-hysteresis 0` sem limiar são rejeitados sem tocar a GPU;
16. um limiar de alerta de 0 °C dispara `alert_raised` na primeira amostra em C++ e C#, com o mesmo UUID e o mesmo limiar no envelope;
17. manter a leitura exatamente no limiar não encerra nem dispara repetidamente o alerta;
18. amostras, lacunas, recuperações e alertas compartilham uma sequência global crescente no stream do CLI;
19. os artefatos C/C++ e .NET declaram a mesma versão do projeto;
20. o schema de evidências declara versões 1 para o envelope e para o banco e aceita eventos v2 e v3;
21. migrations, reabertura, retry idempotente, conflito de sequência, filtros, retenção e writers concorrentes passam sem GPU;
22. arquivo inválido e schema futuro são recusados sem sobrescrita, e uma consulta não cria um banco ausente;
23. uma execução física persiste, consulta e exporta o stream mantendo UUID, run, versão, PCI, VBIOS e profile key;
24. o serviço cria no máximo um coletor ativo para cada UUID mesmo após múltiplos ciclos de discovery;
25. saúde, GPUs, capabilities, telemetria, histórico e SSE possuem contratos HTTP testados sem GPU;
26. um cliente SSE lento não bloqueia o produtor, recebe uma lacuna explícita e pode recuperar pelo `event_id` persistido;
27. SQLite indisponível mantém o host diagnóstico ativo e a coleta só recomeça depois da recuperação do banco;
28. desligamento gracioso confirma `completed_at` e `completion_reason=service_stopped` para cada run;
29. uma execução física do serviço publica somente em loopback e preserva UUID, profile key e versão 0.7.0 no histórico;
30. a instalação real no Windows já confirma configuração do SCM, ações de recuperação, ciclo stop/start, encerramento persistido do run e stream SSE crescente;
31. C++ e C# expõem a mesma ordem, proveniência, IDs, unidades e estados para pelo menos 31 campos semânticos públicos;
32. um campo indisponível mantém todos os valores nulos, enquanto um zero disponível continua sendo zero legítimo;
33. cada relatório contém exatamente quatro métricas com fórmula, unidade, janela, amostras, entradas e origem `computed`;
34. temperatura, potência e memória total aplicáveis são comparadas com uma consulta independente do `nvidia-smi`;
35. eventos persistidos v3 contêm entradas brutas e métricas suficientes para reprodução posterior.

Execute:

```powershell
.\scripts\verify.ps1 -Configuration Release
```

Para a verificação independente de hardware usada no CI:

```powershell
.\scripts\verify-ci.ps1 -Configuration Release
```

`verify-ci.ps1` compila com avisos como erros, executa os testes de ABI, sampler, alertas, armazenamento SQLite e serviço local, verifica a formatação C# e analisa schemas e OpenAPI publicados. `verify.ps1` acrescenta as leituras reais da GPU, a comparação independente com `nvidia-smi` e ciclos físicos do CLI persistente e do serviço HTTP.

## Snapshot local inicial

Em 2026-08-24, a validação inicial foi executada em:

- GPU: NVIDIA GeForce RTX 3060;
- driver: 610.88;
- NVML: 13.610.88;
- biblioteca: `C:\Windows\System32\nvml.dll`;
- backend selecionado: `nvmlDeviceGetTemperatureV`;
- primeira leitura C/C++/C#: 48 °C nas três camadas;
- CMake/MSVC: build Release sem avisos;
- .NET: build Release sem avisos;
- CTest: teste de ABI aprovado.

A temperatura é dinâmica; o snapshot comprova o caminho de leitura, não fixa 48 °C como valor esperado.

## Snapshot do inventário público

Em 2026-08-24, após a ABI v2, o teste completo confirmou:

- perfil da placa: `10de:2504/10de:1536@94.06.25.00.fc`;
- PCI: `00000000:01:00.0`;
- NVML thermal settings: um alvo `gpu`, controlador `gpu_internal`, disponível;
- NVML field 82: alvo `memory`, explicitamente `not_supported` nessa RTX 3060;
- NVAPI thermal settings: um alvo `gpu`, controlador `gpu_internal`, disponível;
- C / C++ / C# / `nvidia-smi`: leituras entre 32 e 33 °C, com dispersão máxima de 1 °C na execução final registrada;
- C++ e C#: mesma identidade e os mesmos três registros de capability;
- build Release: zero avisos; CTest e verificação integrada aprovados.

Esses resultados descrevem esta combinação de placa, VBIOS e driver. Eles não demonstram que toda RTX 3060 — nem toda placa com o mesmo chip — publica o mesmo conjunto de sensores.

## Snapshot do monitoramento resiliente v0.3.0

Em 2026-08-24, a validação Release confirmou:

- ABI C preservada na versão 2;
- dois testes CTest aprovados: ABI e sampler C++;
- testes gerenciados do sampler C# aprovados sem acesso à GPU;
- perda, backoff, recuperação, mudança de índice e buffer limitado exercitados por simulação;
- C++ e C# serializaram `gap` para um UUID ausente com temperatura nula e backoff inicial de 250 ms;
- seleção do UUID `GPU-fca3647e-8390-15a8-f23b-d0f870c9accd` aceita também em letras minúsculas;
- C++ e C# emitiram dois eventos `sample` válidos e equivalentes;
- um ensaio contínuo adicional produziu 100 amostras e sequências contíguas em cada CLI, sem lacunas;
- C, C++, C# e `nvidia-smi` reportaram 33 °C na execução registrada;
- builds MSVC Release e Debug concluídos sem avisos;
- build adicional com Clang 22.1.8 e os dois testes CTest aprovados.

O UUID identifica apenas a GPU usada nesta validação e não é um valor esperado em outras máquinas.

## Snapshot dos alertas de limiar v0.4.0

Em 2026-08-25, após a revisão de engenharia, as validações Release e Debug confirmaram:

- builds MSVC Release e Debug sem avisos, incluindo o novo alvo de teste `rtxmon_alerts`;
- três testes CTest aprovados: ABI, sampler C++ e o avaliador de alertas C++;
- testes C++ e C# aprovados sem GPU para limiar exato, histerese e opções inválidas;
- `--alert-threshold` fora de `--watch` e `--alert-hysteresis 0` sem limiar rejeitados por ambos os CLIs antes de carregar NVML;
- seis leituras estáveis exatamente em 31 °C produziram somente um `alert_raised`, sem alternância, em C++ e C#;
- o stream com alerta preservou sequências globais contíguas em ambas as implementações;
- C, C++, C# e `nvidia-smi` reportaram 31 °C para o mesmo UUID na execução registrada;
- `telemetry-event-v1.schema.json` permaneceu byte a byte igual ao contrato anterior, enquanto o v2 foi validado pelas verificações do projeto;
- CMake, projetos .NET e assemblies gerados declararam a versão 0.4.0;
- `dotnet format --verify-no-changes` aprovado nos três projetos C# após a adição do `AlertEvaluator`.

O JSON Schema de eventos avançou para `telemetry-event-v2.schema.json`; o limiar usado nesta validação (0 °C) existe apenas para disparar o alerta de forma determinística e não é uma recomendação de configuração.

## Snapshot da persistência de evidências v0.5.0

Em 2026-08-25, a validação Release confirmou:

- C, C++ e os cinco projetos C# compilaram com avisos tratados como erros;
- três testes CTest, testes do sampler/alertas C# e a nova suíte `RtxMonitor.Storage.Tests` foram aprovados;
- schema SQLite 1 criado por migration e preservado após fechar e reabrir o processo;
- retry do mesmo evento retornou o mesmo `event_id`, enquanto conteúdo diferente na mesma sequência foi recusado;
- filtros por run, UUID, tipo, intervalo e sequência foram exercitados;
- retenção removeu somente evento antigo, snapshot órfão e run fora da janela;
- 48 writers concorrentes confirmaram todas as sequências sem perda;
- arquivo não SQLite, schema futuro e caminho ausente falharam sem sobrescrever ou criar dados;
- `dotnet list package --vulnerable --include-transitive` não encontrou pacote vulnerável nas fontes consultadas;
- o teste físico persistiu duas amostras da RTX 3060 e as recuperou por `--history` e `--export`;
- os evidence records preservaram `run_id`, versão 0.5.0, o mesmo UUID e o perfil `10de:2504/10de:1536@94.06.25.00.fc`;
- C, C++, C# e `nvidia-smi` reportaram 33 °C na execução registrada;
- CMake, projetos .NET e assemblies gerados declararam a versão 0.5.0;
- `dotnet format --verify-no-changes` foi aprovado nos cinco projetos C#.

O banco usado no teste físico foi criado em um diretório temporário exclusivo e removido após a validação. A v0.5.0 permanece headless: ela adiciona evidência persistente, não serviço nem interface gráfica.

## Snapshot do serviço local v0.6.0

Em 2026-08-25, as validações Debug e Release confirmaram:

- C/C++, os projetos C# existentes e os novos projetos do serviço compilaram sem avisos;
- três testes CTest e as suítes gerenciadas de sampler, alertas, SQLite e serviço foram aprovados;
- o host real respondeu em loopback aos contratos de saúde, GPUs, capabilities, histórico e SSE;
- um cliente SSE lento não bloqueou o produtor, recebeu `stream_gap` e preservou o `event_id` necessário para recuperar o intervalo pelo histórico;
- múltiplos ciclos de discovery mantiveram apenas um coletor ativo por UUID;
- banco inválido deixou o serviço `unavailable` sem encerrar o host e a coleta retomou automaticamente depois da recuperação do arquivo;
- desligamento gracioso persistiu `completed_at` e `completion_reason=service_stopped`;
- schemas do SSE e o contrato OpenAPI v1 foram analisados durante a verificação;
- CMake, projetos .NET, assemblies e respostas HTTP declararam a versão 0.6.0;
- na RTX 3060 física, o serviço foi iniciado em `127.0.0.1:14286`, descobriu o mesmo UUID e preservou o perfil `10de:2504/10de:1536@94.06.25.00.fc` no histórico;
- C, C++, C#, serviço local e `nvidia-smi` reportaram 33 °C na execução registrada, usando o backend `nvmlDeviceGetTemperatureV`;
- `dotnet format --verify-no-changes` foi aprovado em todos os projetos C#.

O banco físico dessa verificação também foi criado em diretório temporário exclusivo e removido ao final. A porta 14286 foi escolhida somente para o ensaio; a configuração padrão do serviço continua documentada em `docs/SERVICE.md`.

### Instalação física no Windows

No mesmo equipamento, o pacote publicado foi instalado e validado como serviço real do Windows:

- serviço `RtxMonitorService` em execução, com inicialização automática e conta `LocalSystem`;
- executável instalado em `C:\Program Files\RtxMonitor\0.6.0\RtxMonitor.Service.exe`;
- endpoint saudável em `http://127.0.0.1:5136`, sem listener exposto fora do loopback;
- banco persistente em `C:\ProgramData\RtxMonitor\telemetry.db`;
- ciclo stop/start fechou a porta durante a parada, criou um novo run na retomada e marcou o run anterior com `completion_reason=service_stopped`;
- SCM configurado para reiniciar após 5, 15 e 60 segundos, com período de reset de 24 horas;
- SHA-256 do assembly implantado idêntico ao pacote publicado;
- stream SSE real entregou quatro eventos consecutivos, de `event_id` 162 a 165, com sequência estritamente crescente;
- saúde, versão 0.6.0, identidade da RTX 3060 e leitura de 33 °C foram confirmadas após a instalação.

Ao final da validação, o serviço permaneceu instalado e em execução para uso local.

## Snapshot da telemetria pública v0.7.0

Em 2026-08-25, a validação Release confirmou:

- ABI C 3 e versões CMake/.NET/assemblies em 0.7.0;
- build C/C++ com `/W4 /WX`, build C# sem avisos e formatação verificada;
- quatro testes CTest aprovados: ABI, sampler, alertas e métricas;
- suítes gerenciadas de sampler/métricas, SQLite e serviço aprovadas sem depender da GPU;
- C++ e C# produziram o mesmo catálogo, na mesma ordem, com 32 registros para 31 campos semânticos;
- 27 registros ficaram `available` e 5 `not_supported`; nenhum campo ausente foi convertido em zero;
- `memory_temperature_c` preservou campo NVML 82, código nativo e estado `not_supported`;
- dois registros `fan_speed_percent` preservaram os índices físicos 0 e 1;
- as quatro métricas carregaram fórmula, unidade, janela, amostras, entradas e origem `computed`;
- eventos C++ e C# usaram `telemetry-event-v3`, com catálogo e métricas dentro de cada `sample`;
- SQLite persistiu e exportou as entradas brutas e os cálculos sem migration estrutural do banco;
- C, C++, C#, serviço local e `nvidia-smi` reportaram 33 °C para o mesmo UUID;
- potência instantânea e memória total passaram pela comparação tolerada com `nvidia-smi`;
- o serviço transitório respondeu em `127.0.0.1:4210` a saúde, GPUs, capabilities, telemetria e histórico;
- o endpoint `/telemetry` preservou o perfil `10de:2504/10de:1536@94.06.25.00.fc`, cobertura e quatro métricas.

A porta 4210 foi escolhida automaticamente para esse ensaio e o banco temporário foi removido ao final. A instalação permanente da v0.6.0 não foi substituída durante esta validação da branch v0.7.0.
