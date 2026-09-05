# Validação

## Validação da v0.9 para a Galax RTX 3060 de 12 GB

Marco concluído em 2026-09-05 para a unidade e configuração registradas no [fechamento](research/2026-09-05-v09-completion.md). Produto 0.9.0, ABI 7. O [manifesto do perfil](profiles/rtx3060-galax-12gb.json) ancora catálogo, fontes e fixtures; ambos os scripts de CI executam a auditoria offline e seus testes negativos. A validação Linux cobre portabilidade sem GPU. O smoke físico Windows usa a placa alvo e uma instância temporária do serviço com banco isolado.

## Diagnóstico do perfil experimental

### Reproduzir a validação do checkout

No Windows x64, com Visual Studio 2022/C++, CMake 3.25+, .NET 8 e Python 3.10+ disponíveis:

```powershell
.\scripts\verify-ci.ps1 -Configuration Release
```

No Linux x64, com GCC/G++, CMake 3.25+, Ninja, .NET 8, Python 3.10+ e `timeout`:

```bash
bash scripts/verify-ci-linux.sh Release
```

Esses comandos não exigem GPU. O job Linux usa restore normal do NuGet; o feed local usado na validação inicial em contêiner não é configuração do repositório. O CI verifica o snapshot compilado contra as fontes/fixtures ancoradas e testa alterações inválidas. O Lab Linux verifica a recusa antecipada da plataforma, sem repetir a suíte exclusiva do Windows.

Depois de um build aprovado, o teste físico da placa alvo é separado:

```powershell
.\scripts\verify.ps1 -Configuration Release -SkipBuild
```

Ele usa uma instância temporária do serviço, porta livre e banco isolado. Aprovação do CI sem GPU, aprovação de revisão de PR, merge e implantação do serviço são etapas distintas. Os resultados históricos completos e seus limites permanecem no registro de fechamento.

### Consultar a compatibilidade

```powershell
dotnet .\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.dll --profile-status --json
```

A saída segue [private-profile-status-v2.schema.json](schema/private-profile-status-v2.schema.json): sete verificações de identidade, revisão/estado do perfil, motivo de revogação, estado e limites de cada operação. `compatible` significa elegível para tentar uma leitura, sujeita à janela dinâmica de taxa; `acquisition_performed` permanece falso, `returned_payload_state` permanece `not_evaluated` e GSP permanece `not_observed`. O diagnóstico não chama as funções privadas. Uma GPU ausente selecionada por índice também produz relatório inelegível; seleção por UUID inexistente continua sendo erro de seleção. O schema v1 permanece histórico.

Os testes de aquisição usam relógio simulado para os limites de 100/2000 ms, compartilhamento entre contextos, lock ocupado, gates lentos e retornos tardios. Após timeout, os leitores ficam bloqueados no processo. A suíte `RtxMonitor.Console.Tests` valida o supervisor com filhos simulados: handshake, aquisição, espera ociosa, cancelamento, timeout, protocolo inválido e encerramento por PID. Os modos reais `--thermal-watch` e `--voltage-watch` preservam seus schemas de amostra e agora executam no worker. Referência: [ADR 0012](adr/0012-private-acquisition-budgets-and-worker.md).

CTest inclui variantes isoladas de perfil ativo, perfil revogado, térmico revogado e tensão revogada, sem setters na biblioteca de produção. Mocks contam chamadas e verificam identidade ausente/divergente, módulo ausente, associação ambígua, falha de consulta parcial, versão de retorno incorreta, erro após escrita do valor e limpeza de saídas anteriores. O CI também valida P/Invoke, JSON, rejeição de elegibilidade contraditória e opções de coleta incompatíveis com o diagnóstico. O smoke físico valida o relatório sem exigir que a máquina suporte o perfil privado. Resultados da retomada estão no [registro de 2026-09-05](research/2026-09-05-v09-profile-policy-validation.md).

## Leitura direta de die e hotspot

Depois do build, valide a aquisição sem GPU-Z:

```powershell
dotnet .\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.dll `
  --thermal-watch --count 3 --interval 500 --json
```

Cada linha deve satisfazer [`private-thermal-sample-v1.schema.json`](schema/private-thermal-sample-v1.schema.json), conter leituras atuais de `GPU Die`, `Hotspot`, `Delta`, relógio monotônico e `profile_evidence_stage = matched_external_reference`. A função privada é detectada em runtime e só é chamada depois da correspondência exata de UUID, PCI/subsystem, VBIOS, driver, módulo proprietário, versão, SHA-256, RVA, estrutura e máscara. O par só é publicado depois do sucesso dos dois canais; resultado parcial, implausível ou com status NVAPI de erro permanece sem flags e sem valores.

## Leitura direta de tensão no perfil fixo

Valide separadamente a aquisição opt-in de tensão, também sem GPU-Z ou HWiNFO como dependência operacional:

```powershell
dotnet .\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.dll `
  --voltage-watch --count 3 --interval 500 --json
```

Cada linha válida deve satisfazer [`private-voltage-sample-v1.schema.json`](schema/private-voltage-sample-v1.schema.json) e preservar relógio UTC e monotônico, microvolts brutos, volts derivados, `profile_evidence_stage`, UUID físico, perfil, interface `0x465f9bcf`, estrutura `0x0001004c`, hash do módulo e RVA. Fora do perfil exato, qualquer divergência de identidade, módulo, estrutura ou faixa falha fechada sem publicar valor. A ABI 5 introduziu o export; a ABI atual é 7, e DLLs anteriores são rejeitadas na abertura. Uma resolução opcional ausente ou concorrente falha como `not_supported`, sem vazar `EntryPointNotFoundException`. Este modo não participa do coletor padrão, serviço, HTTP/SSE, SQLite ou exportação.

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
11. os JSON Schemas v1, v2 e v3 permanecem disponíveis para históricos, enquanto o v4 declara os cinco tipos, os relatórios enriquecidos e a telemetria Windows opcional;
12. testes sem GPU reproduzem gap, backoff limitado, recuperação e mudança de índice para o mesmo UUID;
13. o buffer circular descarta somente os eventos mais antigos ao atingir sua capacidade;
14. C++ e C# selecionam o mesmo UUID e emitem envelopes v4 equivalentes em um stream saudável;
15. `--alert-threshold` fora de `--watch` e `--alert-hysteresis 0` sem limiar são rejeitados sem tocar a GPU;
16. um limiar de alerta de 0 °C dispara `alert_raised` na primeira amostra em C++ e C#, com o mesmo UUID e o mesmo limiar no envelope;
17. manter a leitura exatamente no limiar não encerra nem dispara repetidamente o alerta;
18. amostras, lacunas, recuperações e alertas compartilham uma sequência global crescente no stream do CLI;
19. os artefatos C/C++ e .NET declaram a mesma versão do projeto;
20. o schema de evidências declara versões 1 para o envelope e para o banco e aceita eventos e runs v2, v3 e v4;
21. migrações, reabertura, repetição idempotente, conflito de sequência, filtros, retenção e processos de gravação concorrentes passam sem GPU;
22. arquivo inválido e schema futuro são recusados sem sobrescrita, e uma consulta não cria um banco ausente;
23. uma execução física persiste, consulta e exporta o stream mantendo UUID, run, versão, PCI, VBIOS e profile key;
24. o serviço cria no máximo um coletor ativo para cada UUID mesmo após múltiplos ciclos de descoberta;
25. saúde, GPUs, capabilities, telemetria, histórico e SSE possuem contratos HTTP testados sem GPU;
26. um cliente SSE lento não bloqueia o produtor, recebe uma lacuna explícita e pode recuperar pelo `event_id` persistido;
27. SQLite indisponível mantém o host diagnóstico ativo e a coleta só recomeça depois da recuperação do banco;
28. desligamento gracioso confirma `completed_at` e `completion_reason=service_stopped` para cada run;
29. uma execução física do serviço publica somente em loopback e preserva UUID, profile key e a versão atual no histórico;
30. a instalação real no Windows já confirma configuração do SCM, ações de recuperação, ciclo stop/start, encerramento persistido do run e stream SSE crescente;
31. C++ e C# expõem a mesma ordem, proveniência, IDs, unidades e estados para pelo menos 34 campos semânticos públicos;
32. um campo indisponível mantém todos os valores nulos, enquanto um zero disponível continua sendo zero legítimo;
33. cada relatório contém exatamente quatro métricas com fórmula, unidade, janela, amostras, entradas e origem `computed`;
34. temperatura, potência e memória total aplicáveis são comparadas com uma consulta independente do `nvidia-smi`;
35. eventos persistidos v4 contêm entradas brutas, métricas e telemetria Windows aplicável suficientes para reprodução posterior;
36. o laboratório cria e verifica um pacote de arquivo único sem sobrescrita, traversal, reparse point ou campo JSON extra;
37. no Windows, o CLI offline aceita apenas arquivos de até 16 MiB e valida ROM, PCIR e BIT sem interpretar payloads de token; em outras plataformas, falha com `unsupported_platform` antes de acessar o path;
38. o executável offline não depende de `rtxmon_native`, NVML ou NVAPI e usa somente fixtures sintéticas no CI;
39. a saída do parser e os manifestos do laboratório obedecem aos schemas v0.8 publicados;
40. a observação anexada de IOCTL valida assinaturas, mantém o processo-alvo vivo, não lê buffers de saída e produz relatórios de códigos, entradas limitadas e identidade de handle conformes aos schemas v1;
41. o coletor genérico de candidatos NVAPI preserva inventário, módulos, call sites e entradas delimitadas conforme o schema v1, e uma janela só recebe o rótulo de polling quando o log de referência cresce durante a captura;
42. o perfil térmico fixo valida hashes, versão, tamanho, máscara e layout e lê somente o buffer já fornecido pelo GPU-Z; uma captura só é promovida a `matched_external_reference` quando os erros máximos das duas associações diretas são `<= 0,051 °C` e seu erro médio combinado vence a hipótese invertida por pelo menos `1 °C`, enquanto repetições fora desse gate permanecem explicitamente ambíguas e não revogam evidência histórica ancorada;
43. o manifesto de experimento ancora um ou mais pacotes por SHA-256 externo, rejeita duplicatas, paths fora da raiz, timeline inválida e divergência entre manifesto e pacote;
44. o analisador de séries só consome pacotes ancorados, calcula estatísticas, deltas, período de atualização e correlação quando há referência compatível, preservando `raw_unknown` quando a evidência não autoriza semântica;
45. a repetição de tensão v2 fixa identidade, hashes, RVA, call site, estrutura e 19 DWORDs, separa sessões GPU-Z com layouts distintos, exige crescimento antes/meio/depois e mantém HWiNFO nulo quando não existe CSV corrente;
46. o modo direto de tensão valida o perfil completo e o módulo realmente proprietário do ponteiro antes da chamada, serializa NVAPI e rejeita versão, valor ou estrutura incompatível sem produzir amostra válida;
47. o candidato cooler/fan permanece observação passiva: o contrato v2 fixa identidade GPU/PCI/subsystem/VBIOS/driver, artefatos anteriores e módulo realmente carregado antes de preservar estrutura, contagem e palavras brutas; nenhum campo recebe unidade, índice, RPM, PWM ou semântica de controle.

Execute:

```powershell
.\scripts\verify.ps1 -Configuration Release
```

Para a verificação independente de hardware usada no CI:

```powershell
.\scripts\verify-ci.ps1 -Configuration Release
```

`verify-ci.ps1` compila com avisos como erros, executa os testes de ABI, sampler, alertas, armazenamento SQLite, serviço local, pacote de evidências e parser offline, verifica a formatação C# e analisa schemas e OpenAPI publicados. `verify.ps1` acrescenta as leituras reais da GPU, a comparação independente com `nvidia-smi` e ciclos físicos do CLI persistente e do serviço HTTP.

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
- a repetição do mesmo evento retornou o mesmo `event_id`, enquanto conteúdo diferente na mesma sequência foi recusado;
- filtros por run, UUID, tipo, intervalo e sequência foram exercitados;
- retenção removeu somente evento antigo, snapshot órfão e run fora da janela;
- 48 processos de gravação concorrentes confirmaram todas as sequências sem perda;
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
- múltiplos ciclos de descoberta mantiveram apenas um coletor ativo por UUID;
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

## Validação prolongada da telemetria Windows

Em 2026-08-26, o provider DXGI/PDH foi executado por 30 minutos em um processo isolado na porta 5144, sem substituir nem interromper o Windows Service instalado. O script [`validate-windows-telemetry-long-run.ps1`](../scripts/validate-windows-telemetry-long-run.ps1) consultou `/api/v1/gpus/{uuid}/windows-telemetry` a cada dois segundos e registrou a série em `evidence/windows-telemetry-long-run-20260827-005854`.

- 893 amostras válidas, acima do mínimo de 810 definido para tolerar overhead;
- zero falhas HTTP, contratuais, de identidade e regressões de timestamp;
- LUID `0x000000000001669b` preservado nas 893 amostras;
- maior intervalo entre capturas: 2.281 ms;
- memória local disponível em todas as amostras: mínimo 568.750.080, máximo 918.908.928 e média 622.390.704 bytes;
- memória não local disponível em todas as amostras: mínimo 132.501.504, máximo 197.316.608 e média 140.226.436 bytes;
- os 893 snapshots ficaram `partial`: `3D` permaneceu `inactive` com zero realmente observado, enquanto `Copy`, `VideoDecode`, `VideoEncode`, `OFA` e `VR` ficaram `counter_unavailable` porque não houve instância ativa para produzir amostra;
- nenhum estado ausente foi convertido em zero e memória local/não local nunca foi somada como “dynamic memory”;
- o listener isolado da porta 5144 foi encerrado ao final.

O resumo possui SHA-256 `2d8ccbafc232e81d067cef82c0bc9ed59e52b81349087fa5443a04f42da6198e`; a série JSONL, com 1.215.373 bytes, possui SHA-256 `0b0ec1045293a6c457b6a3306bd9aa7408871fd0b4704f6a63eea1d4666c5931`. Este ensaio comprova estabilidade em carga ambiente; suspensão/retomada e reinicialização intencional do driver continuam gates físicos separados.

Após esse ensaio, a integração ao evento principal também foi validada em um serviço isolado na porta 5145 e SQLite temporário:

- o evento persistido 21 foi recuperado por `/api/v1/history` com schema 4 e `windows_telemetry.state=partial`;
- o histórico preservou LUID `0x000000000001669b`, seis engines e 713.728.000 bytes de memória local naquela amostra;
- o SSE entregou o evento persistido 30 com o mesmo objeto `windows_telemetry`, schema 4 e LUID;
- testes automatizados comprovam o mesmo conteúdo no JSON SQLite, em `EvidenceJson`/histórico e no envelope SSE;
- eventos de alerta mantêm `windows_telemetry=null`, evitando duplicação da telemetria bruta;
- não foi necessária migration estrutural: o SQLite já armazena integralmente o JSON versionado do evento;
- o listener isolado da porta 5145 foi encerrado ao final.

### Recuperação após reinicialização do driver e suspensão

Em 2026-08-26, os dois gates físicos restantes foram executados com elevação restrita ao comando de dispositivo e ao wake timer; o serviço instalado não foi substituído.

- `pnputil /restart-device` reiniciou somente `PCI\VEN_10DE&DEV_2504&SUBSYS_153610DE&REV_A1\4&AA66160&0&0008` e retornou código 0;
- a janela de dois minutos ao redor do restart registrou 119 amostras, zero falhas HTTP/contratuais/de identidade, zero regressões e o mesmo LUID nas 119 leituras;
- o maior intervalo de captura durante o restart foi 2.280 ms; PnP voltou como `OK`, problema 0, e o coletor permaneceu `running`;
- o sistema entrou em S3 por `Application API`; o evento Power-Troubleshooter registrou suspensão em `2026-08-27T01:58:15.718894400Z` e wake em `2026-08-27T01:59:18.039704300Z`;
- a série externa observou uma lacuna física de 56.265 ms sem falha HTTP, troca de LUID, falha contratual ou regressão de timestamp;
- a primeira leitura depois do wake preservou o LUID e retornou memória local validamente zerada enquanto o WDDM reconstruía as alocações; 2.263 ms depois, a leitura local voltou a 595.644.416 bytes;
- o analisador específico de recuperação aprovou 239 amostras e um único LUID; o resumo genérico de continuidade permaneceu `passed=false` de forma esperada porque penaliza amostras não executadas enquanto a máquina estava suspensa;
- após a retomada, o coletor principal continuou `running` e `/history` avançou até o evento 527 com `windows_telemetry`, mesmo LUID e memória local não zero;
- todos os listeners e processos isolados criados para os ensaios foram encerrados.

Evidências: o resumo do restart possui SHA-256 `9ada882dfe78f65f802940f8fe3dd61229d778c9399336a8a38d3fcdef36c325`; sua série, `ac734fc267ca126d5256eb779876c7fac9243f732dd043201ac946d89136f70c`. O resumo específico de retomada possui SHA-256 `4863b38da9a3702b251d5cebfe49995a8f525e17aa1f34f200f89c6cc4402548`; sua série, `b8517feb1a2614d5f1db30d16b233ee46510755941a7a103dd6fd6bdd6bd6e4d`.

## Validação física integrada do pacote v0.8.0

Em 2026-08-26, `verify.ps1 -Configuration Release -SkipBuild` validou o pacote atual na RTX 3060 depois da suíte independente completa:

- 12/12 testes CTest e todas as suítes Managed, Storage, Service e Lab aprovados;
- C, C++, C# e `nvidia-smi` reportaram 34 °C; o serviço local reportou 35 °C, dentro da tolerância de 5 °C para leituras sequenciais;
- C++ e C# emitiram o catálogo público v2 com 35 registros, 30 `available` e 5 `not_supported`, na mesma ordem e com identidade de placa preservada;
- a máscara bruta de PerfCap e sua decomposição em razões ativas foram coerentes em ambos os consumidores;
- streams resilientes e de alerta usaram `telemetry-event-v4`; o CLI manteve `windows_telemetry=null` e o serviço anexou somente snapshots Windows confirmados aos eventos `sample`;
- histórico e exportação SQLite preservaram eventos v4, telemetria pública, métricas calculadas e proveniência;
- o serviço isolado respondeu em `127.0.0.1:8670` a saúde, descoberta, histórico, capabilities, telemetria pública e telemetria Windows;
- o diretório SQLite temporário e o processo isolado foram removidos pelo script ao final.

## Fechamento físico da v0.8.0

Em 2026-08-27, a etapa experimental foi repetida na RTX 3060 com GPU-Z e HWiNFO em execução, sem substituir o serviço instalado e sem leitura PCI/MMIO/kernel pelo RTX Monitor:

- o gate final `verify-ci.ps1 -Configuration Release` recompilou C/C++ com warnings como erros, aprovou 14/14 CTest, todas as suítes Managed, Storage, Service e Lab, formatação, schemas e paridade de versão 0.8.0;
- `verify.ps1 -Configuration Release -SkipBuild` usou os mesmos binários, aprovou C/C++/C#/`nvidia-smi`/serviço em `37 °C`, cobertura pública 30/35, streams resilientes e de alerta, histórico/exportação SQLite e endpoints locais;
- na mesma rodada, oito amostras de `--thermal-watch` retornaram die entre `37,000` e `37,125 °C` e hotspot entre `47,094` e `47,719 °C`, validando o perfil completo, `nvapi64_impl.dll` versão `32.0.16.1088`, SHA-256 `df6455ccf83e43cfe68f405af1eec4e053c7f95da998bf358053b7583980c2f4` e RVA `0x001e0bc0`;
- a leitura direta `--voltage-watch` produziu oito amostras de `956250 µV` (`0,95625 V`) com o mesmo módulo/hash e RVA `0x001c9070`;
- a captura térmica passiva v2 observou 18 retornos, selou o prefixo GPU-Z que cresceu de 14.052.004 para 14.058.144 bytes e selecionou somente a sessão 2 por cobertura antes/meio/depois; as sessões históricas 0 e 1, que continham `-` nos canais térmicos exatos, foram rejeitadas e registradas por índice. A hipótese direta divergiu `0,0639 °C` em média e no máximo `0,3125 °C`, contra mais de `10 °C` na inversão; como excedeu a tolerância estrita de arredondamento, o novo relatório permaneceu `ambiguous_or_outside_tolerance` sem revogar a validação anterior e separada do perfil direto;
- a repetição passiva final de tensão observou 20 retornos, 19 DWORDs por retorno e crescimento do log GPU-Z de 14.059.372 para 14.071.652 bytes; a sessão 2 forneceu 20 pares a `0,9560 V`, com erro máximo `0,00025 V` e estado da referência `matched_rounding_tolerance`;
- como essa janela independente continha somente um patamar bruto, o estado global foi mantido prudentemente em `ambiguous_or_outside_tolerance`; a correlação multipatamar histórica continua como evidência separada, não foi fundida ao novo relatório;
- o HWiNFO permaneceu aberto, mas o CSV disponível não cresceu durante a janela e era antigo; a referência HWiNFO atual foi, portanto, registrada como `null`, sem reutilizar dado obsoleto;
- a recaptura passiva de cooler v2 observou 36 retornos, 18 em cada call site, estrutura `0x000106a8` de 1.704 bytes, 426 DWORDs e duas entradas; ela comprovou identidade exata da GPU, hashes dos artefatos anteriores e a imagem NVAPI alvo carregada por `ModLoad`, enquanto os quatro campos preservados por entrada continuam `raw_field_words` sem semântica;
- GPU-Z permaneceu vivo e responsivo depois de `qqd`; nenhum CDB ou WinDbg ficou anexado;
- a observação térmica tem SHA-256 `53bc805205fe83337cd5beaf444eadef2d6ed2e5beb93b32341668cdf6aa1bca` e sua correlação `b404c15066e88475990770aa3ef3fa0ce70c6bd4346c73fc7425725149e8ceb1`; a observação de tensão tem `1be0120bb22eca396de09eaeb8b2d230b75240c61b9a1370f98c415e481cb33d` e sua correlação `bf2f6af2789553eb6d77741076992793b70897c1a7c152019f7a2a8df058ad82`; o relatório de cooler v2 tem `7b9751f49849756c22949423d53604174a85543dc9263eebd5d9c32d99789a4b`;
- o fechamento usou 14 pacotes verificados, seis cenários e 12 marcadores em um manifesto real `experiment-manifest-v1`, ID `2a31a9be-d107-4cf2-ba6f-4826d7b35741`, SHA-256 `57bcc29e1a951bf83c115a66ad4ca7636fe1b8f8dc8e8c912cfb71c9f6e507b5`;
- o `analysis-report-v1` resultante, SHA-256 `6a52a6bffbd4940d5742c192d27060091d4462a8913e9925405afce938a18db9`, calculou estatísticas e deltas de oito amostras diretas, preservou a unidade `V` e manteve o candidato como `raw_unknown` por não haver série externa sincronizada.

Esses resultados encerram os gates da v0.8.0 para o perfil registrado. Eles não generalizam a ABI privada para outra placa, VBIOS ou driver, não transformam cooler em provider e não promovem os modos opt-in à telemetria estável.

## Snapshot do laboratório offline v0.8.0

O snapshot de 2026-08-25 da v0.8 confirmou:

- versões CMake/.NET/assemblies em 0.8.0;
- build C/C++ Release com `/W4 /WX` e doze testes CTest aprovados;
- oito testes CTest específicos do laboratório: parser, protocolo térmico RM, fixtures, CLI, limite, rejeição de device path e help;
- 34 testes C# do laboratório aprovados, incluindo pacote de evidências, adulteração concorrente, GPU-Z com sessões anexadas, correlação por sessão, classificação offline, inventário de candidatos NVAPI, associação térmica die/hotspot e resolução somente leitura de handle Windows;
- `dotnet format --verify-no-changes` aprovado como gate separado para todos os projetos C#;
- criação e verificação reais passaram pelos schemas de manifesto, descritor, resultado e erro; adulterar payload e manifesto foi recusado pela âncora externa anterior;
- fixtures ROM inteiramente sintéticas cobrem imagem legacy isolada, cadeia legacy+UEFI+tail, ajuste de ponteiro NVIDIA, checksum, revisão/alinhamento PCIR e truncamentos;
- arquivo esparso de 16 MiB + 1 byte rejeitado antes da alocação com `input_too_large`;
- JSON do CLI aprovado por `vbios-analysis-v1.schema.json`, cujo contrato inclui a falha fechada `unsupported_platform` anterior ao acesso de path em sistemas não Windows;
- `rtxmon-vbios.exe` sem dependência de `rtxmon_native`, NVML ou NVAPI;
- relatório real de tracing aprovado pelos schemas de consulta, resolução e execução: 100 IDs NVAPI únicos, todos com ponteiro não nulo; 99 resolvidos em `nvapi_impl.dll` e um em `nvapi.dll`;
- classificador real aprovado por `nvapi-interface-classification-v1.schema.json`, com hashes do relatório e do cabeçalho usados e sem carregar NVAPI;
- captura de 10 segundos registrou 150 entradas em 33 alvos: 14 públicos e 19 ausentes do catálogo; o inventário real passou por `nvapi-candidate-inventory-v1.schema.json` e preservou módulo, hash, RVA e estado semântico;
- captura de controle com 30 segundos repetiu as mesmas 150 entradas e os mesmos 33 alvos, demonstrando que a coleta atual cobre o caminho de startup, ainda não o polling contínuo da aba `Sensors`;
- sintaxe dos sete scripts de bancada validada no CI;
- o diagnóstico de autoridade executado como usuário comum: `nvidia-smi` canônico com assinatura válida, GPU pública disponível, processo não elevado, NVFlash ausente e toolchain WDK/KMDF incompleto;
- Process Monitor confirmou os módulos NVAPI e o ciclo de vida do helper temporário assinado do GPU-Z; a análise estática mostrou caminhos de escrita, portanto o helper não foi adotado como backend;
- o anexo tardio com CDB x86 assinado observou 130 chamadas em 10 segundos tanto em `KernelBase!DeviceIoControl` quanto em `ntdll!NtDeviceIoControlFile`, com os mesmos dois códigos, tamanhos, handle e contagens;
- `resolve-windows-handle` comprovou que o handle `0x368` era um objeto `File` para `\Device\GPU-Z-v8`, com alias `\\.\GPU-Z-v8`, sem abrir o device por nome nem aceitar a licença de uma ferramenta adicional;
- `0x80006040` recebeu somente o seletor `0x19c` e foi ligado estaticamente a `RDMSR` (`IA32_THERM_STATUS`, CPU); `0x800060c0` foi ligado a `HalGetBusDataByOffset` e leu dez offsets de configuração PCI da RTX;
- os relatórios de código/tamanho, entrada delimitada e identidade do handle passaram pelos três schemas v1; nenhum buffer de saída foi coletado;
- o anexo tardio dos 100 candidatos NVAPI registrou 465 entradas em 19 alvos durante dez segundos de log ativo: oito públicos e 11 ausentes do catálogo; `NvAPI_GPU_GetThermalSettings` permaneceu com zero chamadas;
- o novo relatório passou por `nvapi-candidate-call-observation-v1.schema.json`; módulo, hash, RVA, threads e call sites foram preservados sem chamada ativa nem leitura de retorno;
- no binário NVIDIA fixado por hash, `0x65fe3aad`/RVA `0x001ad310` referencia diretamente `NvAPI_GPU_ThermChannelGetStatus`; o call site GPU-Z RVA `0x002225b5` demonstrou dois argumentos, estrutura v2 `0x000200a8` de 168 bytes, máscara por canal e escala `raw / 256` °C;
- o coletor térmico formal registrou 20 retornos bem-sucedidos em dez segundos, dez por canal, lendo somente os 42 DWORDs já fornecidos pelo GPU-Z; canal 0 usou palavra 10/offset `0x28`, e canal 1 usou palavra 11/offset `0x2c`;
- o log cresceu de 3.566.112 para 3.569.796 e 3.572.866 bytes antes, no midpoint e depois da captura; o GPU-Z permaneceu responsivo e não restou CDB/WinDbg anexado;
- `correlate-nvapi-therm-channel` selecionou a sessão 5, janela `22:37:41`–`22:37:49`, e passou por `nvapi-therm-channel-correlation-v1.schema.json`;
- canal 0 → `GPU Temperature` teve erro máximo `0,04375` °C; canal 1 → `Hot Spot`, `0,05` °C; a associação invertida teve erro absoluto médio combinado de `10,3565625` °C;
- o primeiro detach histórico que interrompeu o log e a captura seguinte sem novas amostras continuam registrados como tentativas inválidas e não participam dessa associação;
- nas capturas de startup encerradas, não restou processo GPU-Z/WinDbg, serviço `GPU-Z-v8` nem arquivo temporário do driver; nas capturas anexadas, o GPU-Z permaneceu aberto e responsivo após `qqd`, sem CDB/WinDbg anexado;
- nenhuma ROM proprietária, configuração PCI, MMIO, I2C ou VRAM foi lida pelo RTX Monitor durante essa validação.

O snapshot comprova o laboratório offline, a resolução dos 100 IDs, a redução do polling a 11 candidatos não catalogados e a identificação da ABI térmica v2: palavra 10 é a temperatura do die e palavra 11 é o hotspot no perfil testado. Também elimina os dois IOCTLs observados como fontes diretas do `Hot Spot`: um pertence à telemetria térmica da CPU e o outro à configuração PCI da GPU. Ele ainda não comprova captura direta da VBIOS, contrato público NVIDIA, construção física do sensor, generalização para outro perfil ou acesso próprio do monitor a essa interface privada.
