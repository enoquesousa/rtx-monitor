# RTX Monitor

> Temperatura de GPUs NVIDIA lida diretamente do driver — sem depender de GPU-Z, analisar texto ou inventar valores.

O **RTX Monitor** é um monitor de baixo nível cujo acesso à GPU é somente leitura. Ele mostra a temperatura atual do chip gráfico, inventaria os canais publicados pelo driver e calcula tendências que podem ser refeitas a partir do histórico. A v0.8 também mantém um laboratório separado e opt-in para analisar artefatos offline e observar, de forma passiva, caminhos já executados por ferramentas assinadas.

Com ele, você pode responder duas perguntas de forma objetiva:

1. Qual é a temperatura do chip da GPU agora?
2. Quais sensores térmicos esta combinação de placa, firmware e driver realmente publica?

## O que o projeto mede

| Canal | Como é obtido | Comportamento |
| --- | --- | --- |
| **GPU die** | Sensor `NVML_TEMPERATURE_GPU` da NVML | É a leitura principal exibida pelo monitor |
| **Memória** | Campo `NVML_FI_DEV_MEMORY_TEMP` | Aparece somente quando o driver oferece suporte |
| **Canais térmicos adicionais** | Inventário público da NVML e, no Windows, da NVAPI | Mantém o nome e a origem informados pelo driver |
| **Hotspot** | Canal térmico direto da NVAPI no Windows | Provedor detectado em tempo de execução; não depende do GPU-Z |
| **VRM** | Somente se uma interface validada identificar esse alvo | Nunca é deduzido a partir de outro sensor |
| **Valores estimados** | Não são usados | O projeto não interpola nem fabrica temperaturas |

> **Importante:** `not_supported` não significa que o componente físico não existe. Significa apenas que a API pública não entregou essa leitura para a placa e o driver em uso. Esse resultado é mais seguro do que exibir um número com o nome errado.

## Início rápido

### 1. Instale os requisitos

Para compilar no Windows, você precisa de:

- Windows 10 ou 11, x64;
- uma GPU NVIDIA compatível com o driver instalado;
- Visual Studio 2022 Build Tools com o componente de C/C++;
- CMake 3.25 ou superior;
- .NET SDK 8 ou superior.

### 2. Baixe e compile

Abra o PowerShell e execute:

```powershell
git clone https://github.com/enoquesousa/rtx-monitor.git
cd rtx-monitor
.\scripts\build.ps1 -Configuration Release
```

O script compila os componentes em C, C++ e C#.

### 3. Leia a temperatura uma vez

```powershell
.\build\windows-x64\bin\Release\rtxmon.exe --once
```

Exemplo de saída:

```text
2026-08-24T23:06:22.680Z | GPU 0 NVIDIA GeForce RTX 3060 | die 33 C | NVML nvmlDeviceGetTemperatureV
```

A temperatura do exemplo é apenas ilustrativa. O valor real depende da GPU, da carga e do ambiente.

### 4. Monitore continuamente

Pelo executável C++:

```powershell
.\build\windows-x64\bin\Release\rtxmon.exe --watch --interval 1000
```

Ou pelo aplicativo C#:

```powershell
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe
```

Os dois comandos atualizam a leitura a cada segundo. No modo contínuo, o índice inicial é convertido para o UUID da GPU. Se o driver reiniciar ou o índice mudar, o monitor procura novamente o mesmo UUID em vez de trocar silenciosamente de placa. Pressione `Ctrl+C` para encerrar.

### Monitore die e hotspot sem GPU-Z

O aplicativo C# consulta os dois canais diretamente na NVAPI instalada com o driver NVIDIA:

```powershell
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe `
  --thermal-watch `
  --interval 1000
```

Exemplo de saída real:

```text
2026-08-26 20:20:35.850 +00:00 | GPU Die 35.00 °C | Hotspot 45.59 °C | Delta 10.59 °C | nvapi_thermal_channel
```

Use `--count 1` para uma única leitura ou `--json` para JSON Lines. O provedor resolve, em tempo de execução, a interface térmica `0x65fe3aad`, usa a estrutura v2 de 168 bytes e falha explicitamente se o driver não oferecer essa ABI. O GPU-Z foi usado somente na pesquisa de validação e não participa da execução deste comando.

Para receber também lacunas e recuperações como eventos JSON Lines:

```powershell
.\build\windows-x64\bin\Release\rtxmon.exe --watch --events
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe --watch --events
```

| Evento | Significado |
| --- | --- |
| `sample` | Uma leitura nova e válida foi obtida |
| `gap` | Não há leitura atual; o evento informa erro e tempo até a próxima tentativa |
| `recovered` | O mesmo UUID foi reencontrado após uma ou mais falhas |
| `alert_raised` | A temperatura do die atingiu o limiar configurado |
| `alert_cleared` | A temperatura do die caiu do limiar menos a histerese configurada |

Durante uma lacuna, a última temperatura nunca é reapresentada como atual. O contrato atual está em [telemetry-event-v3.schema.json](docs/schema/telemetry-event-v3.schema.json); os schemas v1 e v2 permanecem publicados para históricos anteriores.

## Alerte quando a temperatura cruzar um limiar

`--alert-threshold` liga um alerta durante `--watch`. Ele dispara `alert_raised` na primeira amostra que atinge o limiar. Com a histerese padrão de zero, o alerta permanece ativo enquanto a leitura estiver exatamente no limiar e só encerra quando ela cair abaixo dele. Com uma histerese positiva, `alert_cleared` ocorre ao atingir `limiar - histerese`:

```powershell
.\build\windows-x64\bin\Release\rtxmon.exe --watch --alert-threshold 80 --alert-hysteresis 5
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe --watch --alert-threshold 80 --alert-hysteresis 5
```

O alerta reage somente a amostras reais — uma lacuna nunca dispara nem encerra um alerta. Sem `--events`, as transições aparecem como uma linha de diagnóstico em `stderr`, preservando o schema de amostra v1 em `--json`. Com `--events`, elas entram no mesmo stream JSON Lines das amostras, lacunas e recuperações. O limiar é uma política escolhida por quem monitora, não um limite reportado pelo driver.

## Grave e consulte o histórico

O aplicativo C# pode guardar todos os eventos em um banco SQLite local. Informe o caminho explicitamente:

```powershell
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe `
  --watch `
  --database .\rtx-monitor.db `
  --retention-days 30
```

O banco registra amostras, lacunas, recuperações e alertas. Cada execução recebe um `run_id`; GPU, PCI, VBIOS, driver, NVML e versão do aplicativo são preservados como proveniência quando estiverem disponíveis. A retenção é aplicada no início de cada execução persistente.

Consulte os 100 eventos mais recentes:

```powershell
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe `
  --history `
  --database .\rtx-monitor.db `
  --limit 100
```

Filtros podem ser combinados:

```powershell
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe `
  --history `
  --database .\rtx-monitor.db `
  --gpu-uuid GPU-... `
  --event-type gap `
  --from-unix-ms 1787600000000 `
  --json
```

`--history --json` produz registros de evidência JSON Lines com limite. Para exportar todo o recorte encontrado, use:

```powershell
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe `
  --export `
  --database .\rtx-monitor.db > evidence.jsonl
```

O formato está em [evidence-record-v1.schema.json](docs/schema/evidence-record-v1.schema.json). Consultar um caminho inexistente falha sem criar um banco vazio; um arquivo inválido não é sobrescrito.

## Execute como serviço local

`RtxMonitor.Service` mantém a coleta ativa sem terminal e oferece uma API somente no próprio computador. Ele cria um coletor por UUID, grava cada evento no SQLite e só então o entrega aos clientes ao vivo.

Depois de compilar, execute em modo console:

```powershell
.\csharp\RtxMonitor.Service\bin\Release\net8.0-windows\win-x64\RtxMonitor.Service.exe `
  --RtxMonitor:DatabasePath .\rtx-monitor-service.db `
  --RtxMonitor:Port 5136
```

Consulte os endpoints:

```powershell
Invoke-RestMethod http://127.0.0.1:5136/health
Invoke-RestMethod http://127.0.0.1:5136/api/v1/gpus
Invoke-RestMethod http://127.0.0.1:5136/api/v1/gpus/GPU-.../telemetry
Invoke-RestMethod 'http://127.0.0.1:5136/api/v1/history?limit=100'
curl.exe -N http://127.0.0.1:5136/api/v1/events
```

| Endpoint | Retorno |
| --- | --- |
| `GET /health` | Saúde do processo, SQLite, descoberta de GPUs, coletores e SSE |
| `GET /api/v1/gpus` | GPUs conhecidas e último estado de cada coletor |
| `GET /api/v1/gpus/{uuid}/capabilities` | Último inventário térmico público |
| `GET /api/v1/gpus/{uuid}/telemetry` | Último catálogo documentado, cobertura e métricas calculadas |
| `GET /api/v1/events` | Eventos persistidos ao vivo por Server-Sent Events |
| `GET /api/v1/history` | Histórico limitado com filtros equivalentes ao CLI |

O endereço é fixado em `127.0.0.1`; `--urls` não amplia a exposição. A API de GPUs chama uma leitura anterior de `last_sample_temperature_c` e informa seu horário — durante uma lacuna, ela não é apresentada como temperatura atual.

Cada cliente SSE possui uma fila limitada. Se um cliente ficar lento, a aquisição continua e o stream envia `stream_gap`; os eventos ausentes permanecem no SQLite e podem ser recuperados pelo endpoint indicado no próprio aviso.

Para publicar a pasta e instalar o mesmo executável como Windows Service:

```powershell
.\scripts\publish-service.ps1 -Configuration Release

# Abra outro PowerShell como Administrador:
.\scripts\install-service.ps1 -Start
Get-Service RtxMonitorService
```

Essa publicação é dependente do framework: a máquina de destino precisa do **ASP.NET Core Runtime 8 x64** (o SDK .NET 8 já o inclui no ambiente de desenvolvimento).

O banco padrão do Windows Service fica em `%ProgramData%\RtxMonitor\telemetry.db`. Configurações podem ser ajustadas no `appsettings.json` publicado antes da instalação. Para remover apenas o registro do serviço, preservando binários e banco:

```powershell
.\scripts\uninstall-service.ps1
```

O contrato completo está em [service-v1.openapi.json](docs/openapi/service-v1.openapi.json), e a decisão de engenharia está no [ADR 0006](docs/adr/0006-loopback-headless-service.md).

## Descubra quais sensores estão disponíveis

Use o inventário térmico antes de assumir que uma GPU oferece memória, hotspot ou VRM:

```powershell
.\build\windows-x64\bin\Release\rtxmon.exe --capabilities
```

Para obter uma saída estável, adequada a scripts e outras aplicações:

```powershell
.\build\windows-x64\bin\Release\rtxmon.exe --capabilities --json
```

Cada fonte consultada recebe um estado explícito:

| Estado | Significado |
| --- | --- |
| `available` | A leitura está disponível |
| `not_supported` | A API respondeu, mas não oferece esse canal |
| `provider_unavailable` | A biblioteca ou a função necessária não está disponível |
| `query_failed` | A fonte existe, mas a consulta falhou |
| `unknown` | Ainda não há informação suficiente para classificar o resultado |

Em uma RTX 3060 usada durante o desenvolvimento, por exemplo, o driver publicou o **GPU die**, mas retornou `not_supported` para a temperatura da memória. Hotspot e VRM não foram renomeados nem estimados. Esse exemplo não é uma promessa de compatibilidade para outros modelos.

O formato JSON completo está documentado em [capabilities-v2.schema.json](docs/schema/capabilities-v2.schema.json).

## Leia toda a telemetria documentada

`--telemetry` consulta temperatura, potência, energia, clocks, utilização, memória, ventoinhas, P-state, motivos de limitação e uso de encoder/decoder. Cada campo mantém a função NVIDIA, o ID ou seletor nativo, a unidade, o código do driver e um estado explícito:

```powershell
.\build\windows-x64\bin\Release\rtxmon.exe --telemetry
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe --telemetry --json
```

O relatório também inclui quatro métricas calculadas:

- média da temperatura do die dentro da janela;
- inclinação térmica em °C/s;
- tempo acima de um limiar configurável;
- diferença entre die e memória, somente quando os dois canais existem.

Uma métrica não é um sensor. Por isso o JSON registra `origin: computed`, fórmula, unidade, janela, número de amostras e entradas. Se a temperatura da memória não estiver disponível, o delta recebe `input_unavailable` e valor `null`.

Na RTX 3060 usada na validação, o catálogo produziu 32 registros: 27 disponíveis e 5 `not_supported`. As duas ventoinhas apareceram separadamente; a temperatura da memória continuou ausente, sem ser substituída por zero.

O catálogo completo, os IDs consultados e as fórmulas estão em [PUBLIC_TELEMETRY.md](docs/PUBLIC_TELEMETRY.md).

## Comandos principais

| Comando | Para que serve |
| --- | --- |
| `--once` | Lê uma amostra e encerra |
| `--watch` | Continua lendo até `Ctrl+C` |
| `--list` | Lista as GPUs NVIDIA encontradas |
| `--capabilities` | Mostra as fontes e os canais térmicos públicos |
| `--telemetry` | Lê o catálogo documentado e as métricas calculadas |
| `--gpu INDEX` | Seleciona a GPU pelo índice, começando em zero |
| `--gpu-uuid UUID` | Seleciona uma GPU pela identidade persistente; não use junto com `--gpu` |
| `--interval MS` | Define o intervalo de 100 a 60000 milissegundos |
| `--count N` | Encerra o modo contínuo após `N` amostras; zero significa ilimitado |
| `--buffer N` | Mantém de 1 a 65536 eventos recentes em memória; o padrão é 256 |
| `--json` | Produz JSON; no modo contínuo, preserva o schema de amostra v1 |
| `--events` | Produz o stream completo de eventos (schema v3) como JSON Lines |
| `--alert-threshold C` | Dispara um alerta durante `--watch` ao atingir `C` °C (0-500) |
| `--alert-hysteresis C` | Define a margem de encerramento; com zero, o alerta só limpa abaixo do limiar |
| `--database PATH` | Persiste `--watch` em SQLite ou seleciona o banco de uma consulta |
| `--retention-days N` | Retém entre 1 e 3650 dias; o padrão é 30 |
| `--history` | Consulta um número limitado de eventos persistidos |
| `--export` | Exporta o recorte histórico completo como JSON Lines |
| `--run-id ID` | Filtra uma execução específica |
| `--event-type T` | Filtra por tipo de evento |
| `--from-unix-ms N` / `--to-unix-ms N` | Delimita o horário observado |
| `--after-sequence N` | Continua após uma sequência; exige `--run-id` |
| `--limit N` | Retorna de 1 a 10000 eventos em `--history`; o padrão é 100 |
| `--help` | Exibe a ajuda completa |

O CLI C++ usa `--once` como padrão. O aplicativo C# usa `--watch` como padrão.

Para monitorar a segunda GPU do computador:

```powershell
.\build\windows-x64\bin\Release\rtxmon.exe --watch --gpu 1
```

Também há um exemplo mínimo em C:

```powershell
.\build\windows-x64\bin\Release\rtxmon-c.exe
```

## Laboratório de engenharia reversa v0.8

A base da v0.8 trabalha **offline**. Ela não captura ROM, não abre PCI/MMIO e não instala driver próprio. Ferramentas auxiliares opt-in podem observar processos de terceiros em uma bancada Windows; elas ficam fora do monitor estável e nunca reproduzem uma interface privada apenas porque ela foi vista.

Primeiro, verifique o que a máquina permite:

```powershell
.\scripts\check-lab-access.ps1
```

O diagnóstico é somente leitura. Ele informa se o processo está elevado, se há GPU NVIDIA, se uma ferramenta de captura foi encontrada, quais pré-requisitos estáticos do WDK/KMDF estão presentes e qual autoridade será necessária nas etapas futuras. `nvidia-smi` só é executado a partir de caminho canônico, sem reparse point e com assinatura válida; quando o diagnóstico já está elevado, o subprocesso é deliberadamente ignorado. O relatório também não afirma que o toolchain está pronto sem um build de prova.

Para preservar um arquivo binário local sem sobrescrever o original:

```powershell
$created = & .\csharp\RtxMonitor.Lab\bin\Release\net8.0\rtxmon-lab.exe create `
  --input C:\evidence-local\vbios.rom `
  --output C:\evidence-local\package-001 `
  --gpu "NVIDIA GeForce RTX 3060" `
  --driver-version "<versão observada>" `
  --vbios-version "<versão observada>" |
  ConvertFrom-Json

.\csharp\RtxMonitor.Lab\bin\Release\net8.0\rtxmon-lab.exe verify `
  --package C:\evidence-local\package-001 `
  --expected-manifest-sha256 $created.manifest_sha256
```

O pacote contém somente `manifest.json` e `artifact/payload.bin`, com payload limitado a 256 MiB. O CLI de empacotamento v0.8 é restrito ao Windows para validar os detalhes do filesystem sem prometer uma proteção Unix ainda não implementada. Guarde `manifest_sha256` fora da pasta — por exemplo, no futuro manifesto do experimento. `verify` exige esse hash externo; assim, alterar o payload **e** reescrever o manifesto não transforma o pacote adulterado em evidência válida. Destino existente, layout extra, caminho inseguro, reparse point, hardlink ou divergência fazem a operação falhar. Se a validação detectar uma corrida depois da publicação do diretório, `create` não devolve sucesso nem âncora e mantém o destino como não confiável para evitar um apagamento inseguro por pathname.

Execute `rtxmon-lab` como usuário comum e use um diretório privado, não compartilhado com outro usuário ou processo não confiável. A v0.8 detecta adulteração concorrente conhecida, mas ainda usa operações de filesystem por pathname; eliminá-las por completo exigirá operações relativas a handles de diretório antes de integrar qualquer aquisição privilegiada.

Depois, analise o payload como uma possível VBIOS:

```powershell
.\build\windows-x64\bin\Release\rtxmon-vbios.exe `
  C:\evidence-local\package-001\artifact\payload.bin > vbios-analysis.json
```

No Windows, `rtxmon-vbios` aceita no máximo 16 MiB, valida a cadeia PCI ROM, as estruturas `PCIR`, o checksum legado e o cabeçalho e os tokens NVIDIA BIT 1.00. Em imagens legacy+UEFI, aplica somente o ajuste de ponteiro documentado pela NVIDIA e valida o intervalo opaco no artefato completo; ainda não interpreta um token como hotspot, VRM ou qualquer sensor. Em outras plataformas, o CLI v0.8 retorna `unsupported_platform` antes de validar ou abrir o caminho. A biblioteca de análise permanece portátil, pura e independente da NVML/NVAPI e da GPU.

Use um log do GPU-Z como referência externa para os próximos experimentos:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- analyze-gpuz-log `
  --input "C:\evidence-local\GPU-Z Sensor Log.txt" > gpuz-reference.json
```

Marque o início e o fim de cada cenário com relógios UTC e monotônico do sistema:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- mark `
  --scenario idle.baseline --phase begin >> experiment-markers.jsonl
```

Cada linha é independente e segue um contrato estável. Isso permite alinhar os logs do RTX Monitor, do GPU-Z e das ferramentas de carga sem tratar horários locais sem fuso como se fossem automaticamente equivalentes.

Calcule uma primeira linha de base estatística entre os canais numéricos do log:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- correlate-gpuz-log `
  --input "C:\evidence-local\GPU-Z Sensor Log.txt" `
  --reference "Hot Spot" --session 0 > gpuz-hotspot-correlation.json
```

Omita `--session` para analisar o arquivo inteiro. Quando o arquivo tiver sessões anexadas,
prefira compará-las individualmente para não misturar linhas de base. O resultado usa
correlação de Pearson sem defasagem. Ele serve para planejar experimentos,
mas não identifica sensores físicos nem revela a interface privada usada pelo GPU-Z.

O relatório preserva todas as amostras e canais, calcula estatísticas e cadência, registra tamanho e SHA-256 e separa dados da GPU/placa de dados do host. `Hot Spot` continua rotulado como uma **observação externa do GPU-Z**: sua presença prova que o software obteve um valor, mas não revela se ele veio de NVAPI privada, RM/GSP, registrador ou sensor físico específico. `PerfCap Reason` é mantido como código bruto, sem tradução inferida.

Uma captura controlada do GPU-Z pode registrar somente os IDs passados a `nvapi_QueryInterface`. Depois, classifique-os offline contra um snapshot oficial do catálogo NVIDIA:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- classify-nvapi-ids `
  --input C:\evidence-local\nvapi-query-report.json `
  --interface-table C:\fontes\nvapi\nvapi_interface.h > nvapi-classification.json
```

O comando não chama a NVAPI. Ele calcula os hashes dos dois arquivos e separa as correspondências públicas dos IDs ausentes no catálogo fornecido. Um ID ausente continua sem nome: pode ser privado, antigo, específico da versão ou apenas uma sondagem de capacidade. Na captura desta RTX 3060, foram observados 100 IDs distintos, com 43 correspondências públicas e 57 IDs sem correspondência no catálogo usado. O caminho e os limites estão documentados em [Caminhos de execução do GPU-Z](docs/research/2026-08-25-gpuz-runtime-paths.md).

A mesma captura normaliza os ponteiros por módulo assinado, hash e RVA e registra quais funções foram de fato executadas. Una os relatórios sem carregar a NVAPI:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- inventory-nvapi-candidates `
  --classification C:\evidence-local\nvapi-classification.json `
  --calls C:\evidence-local\nvapi-call-report.json > nvapi-candidates.json
```

Na janela de inicialização de 10 segundos, os 100 IDs resolveram para código, 33 endereços foram executados e 19 deles não estavam no catálogo público. Uma janela de controle com 30 segundos repetiu as mesmas contagens, indicando startup, não polling contínuo. Esses 19 são candidatos binários ainda sem função atribuída, não sensores identificados. O inventário [`nvapi-candidate-inventory-v1`](docs/schema/nvapi-candidate-inventory-v1.schema.json) deixa essa diferença explícita para impedir que frequência de chamada seja confundida com hotspot, VRM ou memória.

Com o GPU-Z já aberto em `Sensors` e o log comprovadamente avançando, o coletor anexado observa quais desses endereços participam do polling:

```powershell
.\scripts\capture-gpuz-nvapi-candidate-calls.ps1 `
  -GpuzProcessId 1234 `
  -CandidateInventoryPath C:\evidence-local\nvapi-candidates.json `
  -DurationSeconds 10
```

Na RTX 3060, 19 alvos receberam 465 chamadas nessa janela: oito públicos e 11 ausentes do catálogo. `NvAPI_GPU_GetThermalSettings`, embora usado no startup, recebeu zero chamadas durante o polling. No mesmo binário NVIDIA, o candidato privado `0x65fe3aad` (RVA `0x001ad310`) referencia diretamente `NvAPI_GPU_ThermChannelGetStatus`; `0x465f9bcf` referencia estruturas de rails e políticas de tensão; e `0x35aed5e8` referencia estruturas de fan/cooler.

Depois de uma primeira observação válida, uma repetição pode limitar-se aos candidatos privados e registrar somente `ECX`, `EDX` e quatro DWORDs da pilha, sem dereferenciar ponteiros:

```powershell
.\scripts\capture-gpuz-nvapi-candidate-calls.ps1 `
  -GpuzProcessId 1234 `
  -CandidateInventoryPath C:\evidence-local\nvapi-candidates.json `
  -TargetScope ObservedUnidentified `
  -PriorObservationPath C:\evidence-local\nvapi-candidate-call-report.json `
  -CaptureInputWords `
  -DurationSeconds 10
```

O relatório segue [`nvapi-candidate-call-observation-v1`](docs/schema/nvapi-candidate-call-observation-v1.schema.json). Cada repetição exige uma sessão nova com o log crescendo antes e durante a janela. Uma primeira tentativa interrompeu o worker de logging e foi excluída; a sessão térmica válida comprovou crescimento antes, no meio e depois do anexo.

Para o perfil térmico já demonstrado por análise estática, o coletor especializado aceita somente os hashes, RVAs, versão e tamanho conhecidos:

```powershell
.\scripts\capture-gpuz-nvapi-therm-channel-v2.ps1 `
  -GpuzProcessId 1234 `
  -CandidateInventoryPath C:\evidence-local\nvapi-candidates.json `
  -PriorObservationPath C:\evidence-local\nvapi-polling-report.json `
  -DurationSeconds 10
```

Ele não chama NVAPI nem modifica memória. O breakpoint fica no call site pós-retorno do GPU-Z e lê exatamente os 168 bytes da estrutura v2 que o próprio aplicativo já inicializou. Na captura válida, a máscara selecionou dois canais: palavra 10 (`offset 0x28`) e palavra 11 (`offset 0x2c`), ambas inteiros com sinal em ponto fixo 8, convertidos por `raw / 256` °C.

Compare a observação com o prefixo exato do log:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- `
  correlate-nvapi-therm-channel `
  --observation C:\evidence-local\nvapi-therm-channel-v2-report.json `
  --gpuz-log "C:\evidence-local\GPU-Z Sensor Log.txt"
```

No perfil testado — RTX 3060 `10de:2504`, subsystem `10de:1536`, VBIOS `94.06.25.00.fc`, driver 610.88 e binários ancorados por SHA-256 — o canal 0 correspondeu a `GPU Temperature` e o canal 1 a `Hot Spot`. Os erros máximos foram `0,04375` °C e `0,05` °C; a associação invertida errou `10,3565625` °C em média. Isso identifica o caminho usado pelo GPU-Z nesse perfil, não cria uma API pública NVIDIA nem prova um termistor físico separado.

Para observar o polling real, abra o GPU-Z normalmente na aba `Sensors`, ative o log e execute em um PowerShell elevado:

```powershell
.\scripts\capture-gpuz-device-io-control.ps1 `
  -GpuzProcessId 1234 `
  -DurationSeconds 10 `
  -ObservedApi DeviceIoControl
```

O script usa o CDB x86 assinado da Microsoft, anexa ao processo já aberto e desconecta sem fechá-lo. Ele não emite IOCTLs e nunca lê buffers de saída: registra código, tamanhos e somente entradas declaradas de 4 ou 12 bytes. Para comprovar a identidade de um handle observado:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- `
  resolve-windows-handle --process-id 1234 --handle 0x368
```

Na RTX 3060, o handle foi identificado como `\\.\GPU-Z-v8`. As camadas Win32 e nativa observaram exatamente as mesmas 130 chamadas em dez segundos. O código `0x80006040` lê o MSR Intel `IA32_THERM_STATUS`, ou seja, temperatura/estado térmico da CPU. O código `0x800060c0` lê bytes de configuração PCI da RTX. Nenhum dos dois fornece diretamente o `Hot Spot`. Com o caminho térmico já identificado, o próximo experimento prioriza a ABI do candidato `0x465f9bcf`, ligado às famílias de tensão, sem presumir que cada campo seja medido ou calculado. Detalhes, hashes e limites estão em [Caminhos de runtime do GPU-Z](docs/research/2026-08-25-gpuz-runtime-paths.md).

A investigação do snapshot oficial `open-gpu-kernel-modules-610.57.04` encontrou o protocolo RM `THERMAL_SYSTEM_EXECUTE_V2`, capaz de enumerar sensores e consultar provedor, alvo, faixa e leitura. A biblioteca pura [`rm_thermal_protocol.hpp`](cpp/include/rtxmon/lab/rm_thermal_protocol.hpp) fixa esse ABI e valida respostas, mas deliberadamente não possui transporte para o driver. Ela não é executada contra o driver Windows 610.88 enquanto versão, handles e rota WDDM não forem comprovados.

Não faça commit do binário analisado. ROMs, dumps e diretórios locais de evidência são ignorados pelo Git. O procedimento completo está em [EXPERIMENT_LAB.md](docs/EXPERIMENT_LAB.md).

## O que significa “temperatura real”

O projeto devolve o valor que o firmware e o driver NVIDIA publicam no momento da consulta. A leitura principal vem da **NVML**, biblioteca de gerenciamento que também serve de base para o `nvidia-smi`. Durante o monitoramento, os executáveis carregam a NVML diretamente em vez de iniciar esse utilitário como subprocesso.

Isso não equivale a acessar o conversor analógico do sensor, registradores privados ou uma sonda física externa. O fabricante ainda pode calibrar, agregar ou arredondar a medição antes de expô-la.

Neste projeto, portanto, **temperatura real** significa **temperatura reportada diretamente pelo driver**, com a fonte identificada e sem estimativa feita pela aplicação.

## Como a arquitetura funciona

```text
sensores e firmware da GPU
            |
            v
       driver NVIDIA
            |
      +-----+------+
      |            |
     NVML        NVAPI
   principal   complementar
      +-----+------+
            |
            v
 rtxmon_native.dll (C)
 aquisição + ABI versionada
      +-----+------+
      |            |
      v            v
 núcleo C++    biblioteca C#
 sampler +     P/Invoke + sampler
 métricas           |
 buffer             |
      |        +----+-----+
      v        |          |
 rtxmon.exe  console   serviço headless
                         |
                    SQLite + HTTP/SSE
```

Cada linguagem tem uma responsabilidade clara:

| Camada | Responsabilidade |
| --- | --- |
| **C** | Carrega NVML/NVAPI, consulta o driver e expõe uma ABI — o contrato binário usado pelas outras linguagens |
| **C++** | Organiza os dados, calcula métricas, mantém o sampler resiliente e fornece o CLI `rtxmon.exe` |
| **C#** | Consome a ABI C por P/Invoke, mantém SQLite e executa o serviço local HTTP/SSE |
| **C++ de laboratório** | Biblioteca pura para bytes de VBIOS e CLI Windows offline independente, sem vínculo com a aquisição |
| **C# de laboratório** | Empacota artefatos locais, verifica hashes, importa séries externas e classifica IDs observados contra catálogos públicos offline |

A NVAPI é complementar e opcional. Se ela não estiver disponível, a leitura principal por NVML continua funcionando.

## Por onde começar no código

| Se você quer... | Comece por |
| --- | --- |
| Entender a API pública do projeto | [native/include/rtxmon/rtxmon.h](native/include/rtxmon/rtxmon.h) |
| Ver o menor exemplo possível em C | [examples/c/temperature_once.c](examples/c/temperature_once.c) |
| Entender o CLI C++ | [cpp/cli/main.cpp](cpp/cli/main.cpp) |
| Ver o catálogo documentado | [native/src/public_telemetry.c](native/src/public_telemetry.c) |
| Entender as fórmulas calculadas | [cpp/src/metrics.cpp](cpp/src/metrics.cpp) |
| Integrar com uma aplicação .NET | [csharp/RtxMonitor.Managed/NvidiaMonitor.cs](csharp/RtxMonitor.Managed/NvidiaMonitor.cs) |
| Estudar reconexão, eventos e buffer em C++ | [cpp/include/rtxmon/sampler.hpp](cpp/include/rtxmon/sampler.hpp) |
| Estudar o sampler equivalente em C# | [csharp/RtxMonitor.Managed/Sampling.cs](csharp/RtxMonitor.Managed/Sampling.cs) |
| Estudar o avaliador de alertas em C++ | [cpp/include/rtxmon/alerts.hpp](cpp/include/rtxmon/alerts.hpp) |
| Estudar o avaliador de alertas em C# | [csharp/RtxMonitor.Managed/Alerts.cs](csharp/RtxMonitor.Managed/Alerts.cs) |
| Entender o banco de evidências | [csharp/RtxMonitor.Storage/SqliteTelemetryStore.cs](csharp/RtxMonitor.Storage/SqliteTelemetryStore.cs) |
| Entender o supervisor do serviço | [csharp/RtxMonitor.Service/GpuMonitoringWorker.cs](csharp/RtxMonitor.Service/GpuMonitoringWorker.cs) |
| Entender os endpoints HTTP/SSE | [csharp/RtxMonitor.Service/ServiceEndpoints.cs](csharp/RtxMonitor.Service/ServiceEndpoints.cs) |
| Entender o pacote de evidência v0.8 | [csharp/RtxMonitor.Lab/LabPackage.cs](csharp/RtxMonitor.Lab/LabPackage.cs) |
| Importar uma referência do GPU-Z | [csharp/RtxMonitor.Lab/GpuzSensorLog.cs](csharp/RtxMonitor.Lab/GpuzSensorLog.cs) |
| Correlacionar canais térmicos privados | [csharp/RtxMonitor.Lab/ThermChannelCorrelation.cs](csharp/RtxMonitor.Lab/ThermChannelCorrelation.cs) |
| Classificar IDs NVAPI observados | [csharp/RtxMonitor.Lab/NvapiInterfaceClassification.cs](csharp/RtxMonitor.Lab/NvapiInterfaceClassification.cs) |
| Comprovar a identidade de um handle Windows | [csharp/RtxMonitor.Lab/WindowsHandleIdentity.cs](csharp/RtxMonitor.Lab/WindowsHandleIdentity.cs) |
| Observar candidatos NVAPI no polling | [scripts/capture-gpuz-nvapi-candidate-calls.ps1](scripts/capture-gpuz-nvapi-candidate-calls.ps1) |
| Capturar o perfil térmico v2 allowlisted | [scripts/capture-gpuz-nvapi-therm-channel-v2.ps1](scripts/capture-gpuz-nvapi-therm-channel-v2.ps1) |
| Observar IOCTLs existentes sem chamá-los | [scripts/capture-gpuz-device-io-control.ps1](scripts/capture-gpuz-device-io-control.ps1) |
| Estudar o protocolo térmico RM | [cpp/include/rtxmon/lab/rm_thermal_protocol.hpp](cpp/include/rtxmon/lab/rm_thermal_protocol.hpp) |
| Estudar o parser offline de VBIOS | [cpp/src/lab/vbios_parser.cpp](cpp/src/lab/vbios_parser.cpp) |
| Executar o laboratório com segurança | [docs/EXPERIMENT_LAB.md](docs/EXPERIMENT_LAB.md) |
| Conhecer as decisões de arquitetura | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Ver o caminho até a engenharia reversa | [docs/ROADMAP.md](docs/ROADMAP.md) |

## Valide antes de contribuir

Depois de alterar o código, execute:

```powershell
.\scripts\verify.ps1 -Configuration Release
```

A verificação:

- compila C, C++ e C# tratando avisos como erros;
- executa os testes do contrato binário;
- simula perda da GPU, mudança de índice, backoff e recuperação sem hardware;
- compara a identidade da GPU com o `nvidia-smi`;
- confirma que C, C++ e C# observam o mesmo dispositivo;
- testa seleção por UUID, inventário de capacidades, catálogo público e streams contínuos;
- compara campos aplicáveis com o `nvidia-smi` e confirma que ausência nunca vira zero;
- testa migrações, reinício, concorrência, retenção e arquivos SQLite inválidos sem GPU;
- testa API local, backpressure SSE, instância única por GPU, desligamento e recuperação do serviço sem GPU.
- testa pacote ancorado por hash, adulteração, traversal, reparse points e limites sem GPU; `create` emite JSON determinístico, enquanto `verify` aceita inteiros semanticamente válidos pelo schema;
- testa ROM/PCIR/BIT com fixtures sintéticas, todas as truncagens e o limite de 16 MiB sem firmware proprietário.
- testa a estrutura térmica v2, a escala `raw / 256`, a associação die/hotspot e os schemas correspondentes sem GPU.

O GitHub Actions usa `scripts/verify-ci.ps1`, que não exige GPU. A validação física continua separada em `scripts/verify.ps1`.

## Caminho até a engenharia reversa

A prioridade agora é construir evidência, não uma interface gráfica. Cada etapa prepara a seguinte:

| Versão | Foco |
| --- | --- |
| **v0.5.0** | Persistência SQLite concluída |
| **v0.6.0** | Serviço local headless, HTTP/SSE e Windows Service concluídos |
| **v0.7.0** | Telemetria pública e métricas rastreáveis concluídas |
| **v0.8.0** | Em andamento: ABI térmica v2 e canais die/hotspot identificados no perfil exato; próxima etapa é investigar tensão e potência com o mesmo rigor |
| **v0.9.0** | Expandir e repetir perfis de aquisição sem relaxar a fronteira de segurança |
| **v0.10.0** | Validar candidatos com repetição e referências independentes |
| **v0.11.0** | Publicar candidatos validados em um provedor experimental separado |
| **v1.0.0** | Estabilizar contratos, operação e governança dos perfis |

Os critérios completos estão no [roadmap de engenharia](docs/ROADMAP.md). A regra central é simples: um valor que muda com a carga ainda não é prova de que encontramos um sensor de hotspot, memória ou VRM.

## Segurança e limites atuais

- O acesso do projeto ao hardware da GPU é **somente leitura**; SQLite, pacotes de evidência e instalação do serviço escrevem apenas no host.
- Não altera ventoinha, clock, tensão ou limite de energia.
- O monitor estável e o laboratório offline não exigem privilégio administrativo.
- A v0.8 atual não captura ROM da placa nem lê I2C, DDC, SMBus, MMIO ou registradores privados. Os coletores genéricos registram apenas entradas delimitadas; o perfil térmico fixo lê somente a estrutura de 168 bytes já fornecida pelo GPU-Z, depois da chamada, sem invocar a interface ou modificar o buffer.
- No Windows, o CLI de VBIOS lê somente o caminho fornecido pelo operador, impõe limite de tamanho e não executa seu conteúdo; rejeita UNC, dispositivo, stream alternativo, drive remoto e reparse point. Em outros sistemas, a v0.8 retorna `unsupported_platform` antes de inspecionar o caminho; a biblioteca pura continua apta a analisar bytes já carregados por um chamador confiável.
- Uma futura leitura PCI/MMIO exigirá operação allowlisted, driver assinado e HVCI ativo; Administrador sozinho não basta.
- BAR1, VRAM, varredura cega e qualquer escrita permanecem fora do escopo.
- Não transforma um sensor desconhecido em hotspot, VRM ou memória.
- A DLL NVIDIA é carregada por um caminho confiável do sistema.
- A identidade entre NVML e NVAPI é correlacionada pelo endereço PCI, não pela ordem em que as APIs listam as placas.
- A camada nativa já possui um caminho de carregamento para Linux, mas os scripts de compilação e validação desta versão são voltados ao Windows x64.

A v0.8 inicia a engenharia reversa em um modo experimental separado e opt-in. A parte offline preserva e analisa arquivos fornecidos pelo operador; as ferramentas anexadas observam somente chamadas que um processo assinado já executa. A futura aquisição allowlisted será restrita a perfis exatos de placa, VBIOS e driver. O coletor estável continua usando APIs documentadas e sem privilégio administrativo. Veja os marcos e os critérios de evidência no [roadmap de engenharia](docs/ROADMAP.md).

## Documentação técnica

- [Arquitetura e contratos](docs/ARCHITECTURE.md)
- [Operação do serviço local](docs/SERVICE.md)
- [Roadmap de engenharia reversa](docs/ROADMAP.md)
- [Procedimento de validação](docs/VALIDATION.md)
- [Catálogo de telemetria pública e fórmulas](docs/PUBLIC_TELEMETRY.md)
- [Laboratório de engenharia reversa v0.8](docs/EXPERIMENT_LAB.md)
- [Threat model da aquisição experimental](docs/security/experimental-acquisition-threat-model.md)
- [Auditoria real da RTX 3060 GA106](docs/research/2026-08-25-rtx3060-ga106-surface-audit.md)
- [Captura e inventário da VBIOS RTX 3060 GA106](docs/research/2026-08-25-rtx3060-ga106-vbios-capture.md)
- [Referência de sensores observada no GPU-Z](docs/research/2026-08-25-rtx3060-gpuz-reference-log.md)
- [Caminhos de runtime e IDs NVAPI observados no GPU-Z](docs/research/2026-08-25-gpuz-runtime-paths.md)
- [Protocolo térmico RM encontrado no código aberto NVIDIA](docs/research/2026-08-25-nvidia-rm-thermal-system.md)
- [ADR 0001 — uso da NVML](docs/adr/0001-use-nvml.md)
- [ADR 0002 — descoberta pública de capacidades](docs/adr/0002-public-capability-discovery.md)
- [ADR 0003 — monitoramento resiliente](docs/adr/0003-resilient-sampling.md)
- [ADR 0004 — alertas de limiar](docs/adr/0004-threshold-alerts.md)
- [ADR 0005 — armazenamento SQLite de evidências](docs/adr/0005-sqlite-evidence-store.md)
- [ADR 0006 — serviço local headless](docs/adr/0006-loopback-headless-service.md)
- [ADR 0007 — telemetria pública e métricas calculadas](docs/adr/0007-public-telemetry-and-computed-metrics.md)
- [ADR 0008 — laboratório reproduzível e aquisição allowlisted](docs/adr/0008-reproducible-reverse-engineering-lab.md)
- [OpenAPI do serviço local — v1](docs/openapi/service-v1.openapi.json)
- [Schema JSON de capacidades](docs/schema/capabilities-v2.schema.json)
- [Schema JSON do catálogo público](docs/schema/public-telemetry-v1.schema.json)
- [Schema JSON de eventos atual — v3](docs/schema/telemetry-event-v3.schema.json)
- [Schema JSON histórico de eventos — v2](docs/schema/telemetry-event-v2.schema.json)
- [Schema JSON histórico de eventos — v1](docs/schema/telemetry-event-v1.schema.json)
- [Schema JSON de evidências — v1](docs/schema/evidence-record-v1.schema.json)
- [Schema JSON de telemetria ao vivo — v1](docs/schema/live-telemetry-v1.schema.json)
- [Schema JSON de lacuna SSE — v1](docs/schema/stream-gap-v1.schema.json)
- [Schema do manifesto de artefato — v1](docs/schema/artifact-package-manifest-v1.schema.json)
- [Schema da saída do pacote de evidência — v1](docs/schema/evidence-package-v1.schema.json)
- [Schema de erro do CLI de laboratório — v1](docs/schema/lab-command-error-v1.schema.json)
- [Schema da análise offline de VBIOS — v1](docs/schema/vbios-analysis-v1.schema.json)
- [Schema da referência de sensores GPU-Z — v1](docs/schema/gpuz-reference-analysis-v1.schema.json)
- [Schema de marcador experimental — v1](docs/schema/experiment-marker-v1.schema.json)
- [Schema de correlação interna do GPU-Z — v1](docs/schema/gpuz-correlation-v1.schema.json)
- [Schema de observação de IDs NVAPI — v1](docs/schema/nvapi-query-observation-v1.schema.json)
- [Schema de classificação de IDs NVAPI — v1](docs/schema/nvapi-interface-classification-v1.schema.json)
- [Schema de resolução de IDs NVAPI — v1](docs/schema/nvapi-interface-resolution-v1.schema.json)
- [Schema de chamadas NVAPI observadas — v1](docs/schema/nvapi-call-observation-v1.schema.json)
- [Schema do inventário de candidatos NVAPI — v1](docs/schema/nvapi-candidate-inventory-v1.schema.json)
- [Schema de chamadas anexadas dos candidatos NVAPI — v1](docs/schema/nvapi-candidate-call-observation-v1.schema.json)
- [Schema da observação térmica NVAPI v2 — v1](docs/schema/nvapi-therm-channel-v2-observation-v1.schema.json)
- [Schema da correlação die/hotspot — v1](docs/schema/nvapi-therm-channel-correlation-v1.schema.json)
- [Schema de observação de IOCTLs do GPU-Z — v1](docs/schema/gpuz-device-io-control-observation-v1.schema.json)
- [Schema de entradas limitadas de IOCTLs do GPU-Z — v1](docs/schema/gpuz-device-io-control-input-v1.schema.json)
- [Schema de identidade de handle Windows — v1](docs/schema/windows-handle-identity-v1.schema.json)
- [Avisos de componentes de terceiros](THIRD_PARTY_NOTICES.md)

## Referências oficiais

- [Visão geral da NVML](https://docs.nvidia.com/deploy/nvml-api/nvml-api-reference.html)
- [Consultas de dispositivo na NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceQueries.html)
- [Campos públicos da NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlFieldValueEnums.html)
- [Sensores térmicos da NVAPI](https://docs.nvidia.com/nvapi/group__gputhermal.html)
- [NVIDIA — especificação da BIOS Information Table](https://nvidia.github.io/open-gpu-doc/BIOS-Information-Table/BIOS-Information-Table.html)
- [Microsoft — acesso ao espaço de configuração PCI](https://learn.microsoft.com/windows-hardware/drivers/pci/accessing-pci-device-configuration-space)
- [Microsoft — definição de códigos IOCTL](https://learn.microsoft.com/windows-hardware/drivers/kernel/defining-i-o-control-codes)
- [Microsoft — `HalGetBusDataByOffset`](https://learn.microsoft.com/windows-hardware/drivers/ddi/ntddk/nf-ntddk-halgetbusdatabyoffset)
- [Microsoft — assinatura de drivers](https://learn.microsoft.com/windows-hardware/drivers/install/driver-signing)
- [Microsoft — compatibilidade com HVCI](https://learn.microsoft.com/windows-hardware/test/hlk/testref/driver-compatibility-with-device-guard)
- [Intel — Software Developer Manuals](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html)
