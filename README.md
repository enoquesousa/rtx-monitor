# RTX Monitor

> Temperatura de GPUs NVIDIA lida pelas APIs públicas do driver — sem usar a saída do `nvidia-smi`, analisar texto ou inventar valores.

O **RTX Monitor** é um monitor de baixo nível e somente leitura. Ele mostra a temperatura atual do chip gráfico e informa quais outros canais térmicos o driver disponibiliza para a sua placa.

Com ele, você pode responder duas perguntas de forma objetiva:

1. Qual é a temperatura do chip da GPU agora?
2. Quais sensores térmicos esta combinação de placa, firmware e driver realmente publica?

## O que o projeto mede

| Canal | Como é obtido | Comportamento |
| --- | --- | --- |
| **GPU die** | Sensor `NVML_TEMPERATURE_GPU` da NVML | É a leitura principal exibida pelo monitor |
| **Memória** | Campo `NVML_FI_DEV_MEMORY_TEMP` | Aparece somente quando o driver oferece suporte |
| **Canais térmicos adicionais** | Inventário público da NVML e, no Windows, da NVAPI | Mantém o nome e a origem informados pelo driver |
| **Hotspot e VRM** | Somente se uma API pública identificar esses alvos | Nunca são deduzidos a partir de outro sensor |
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

Durante uma lacuna, a última temperatura nunca é reapresentada como atual. O contrato está em [telemetry-event-v2.schema.json](docs/schema/telemetry-event-v2.schema.json).

## Alerte quando a temperatura cruzar um limiar

`--alert-threshold` liga um alerta durante `--watch`. Ele dispara `alert_raised` na primeira amostra que atinge o limiar. Com a histerese padrão de zero, o alerta permanece ativo enquanto a leitura estiver exatamente no limiar e só encerra quando ela cair abaixo dele. Com uma histerese positiva, `alert_cleared` ocorre ao atingir `limiar - histerese`:

```powershell
.\build\windows-x64\bin\Release\rtxmon.exe --watch --alert-threshold 80 --alert-hysteresis 5
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe --watch --alert-threshold 80 --alert-hysteresis 5
```

O alerta reage somente a amostras reais — uma lacuna nunca dispara nem encerra um alerta. Sem `--events`, as transições aparecem como uma linha de diagnóstico em `stderr`, preservando o schema de amostra v1 em `--json`. Com `--events`, elas entram no mesmo stream JSON Lines das amostras, lacunas e recuperações. O limiar é uma política escolhida por quem monitora, não um limite reportado pelo driver.

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

## Comandos principais

| Comando | Para que serve |
| --- | --- |
| `--once` | Lê uma amostra e encerra |
| `--watch` | Continua lendo até `Ctrl+C` |
| `--list` | Lista as GPUs NVIDIA encontradas |
| `--capabilities` | Mostra as fontes e os canais térmicos públicos |
| `--gpu INDEX` | Seleciona a GPU pelo índice, começando em zero |
| `--gpu-uuid UUID` | Seleciona uma GPU pela identidade persistente; não use junto com `--gpu` |
| `--interval MS` | Define o intervalo de 100 a 60000 milissegundos |
| `--count N` | Encerra o modo contínuo após `N` amostras; zero significa ilimitado |
| `--buffer N` | Mantém de 1 a 65536 eventos recentes em memória; o padrão é 256 |
| `--json` | Produz JSON; no modo contínuo, preserva o schema de amostra v1 |
| `--events` | Produz o stream completo de eventos (schema v2) como JSON Lines |
| `--alert-threshold C` | Dispara um alerta durante `--watch` ao atingir `C` °C (0-500) |
| `--alert-hysteresis C` | Define a margem de encerramento; com zero, o alerta só limpa abaixo do limiar |
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
 contrato binário versionado
      +-----+------+
      |            |
      v            v
 núcleo C++    biblioteca C#
 sampler +     sampler +
 buffer        buffer
      |            |
      v            v
 rtxmon.exe    console C#
```

Cada linguagem tem uma responsabilidade clara:

| Camada | Responsabilidade |
| --- | --- |
| **C** | Carrega NVML/NVAPI, consulta o driver e expõe uma ABI — o contrato binário usado pelas outras linguagens |
| **C++** | Organiza os dados, mantém o sampler resiliente e fornece o CLI `rtxmon.exe` |
| **C#** | Consome a ABI C por P/Invoke e oferece o mesmo modelo resiliente para aplicações .NET |

A NVAPI é complementar e opcional. Se ela não estiver disponível, a leitura principal por NVML continua funcionando.

## Por onde começar no código

| Se você quer... | Comece por |
| --- | --- |
| Entender a API pública do projeto | [native/include/rtxmon/rtxmon.h](native/include/rtxmon/rtxmon.h) |
| Ver o menor exemplo possível em C | [examples/c/temperature_once.c](examples/c/temperature_once.c) |
| Entender o CLI C++ | [cpp/cli/main.cpp](cpp/cli/main.cpp) |
| Integrar com uma aplicação .NET | [csharp/RtxMonitor.Managed/NvidiaMonitor.cs](csharp/RtxMonitor.Managed/NvidiaMonitor.cs) |
| Estudar reconexão, eventos e buffer em C++ | [cpp/include/rtxmon/sampler.hpp](cpp/include/rtxmon/sampler.hpp) |
| Estudar o sampler equivalente em C# | [csharp/RtxMonitor.Managed/Sampling.cs](csharp/RtxMonitor.Managed/Sampling.cs) |
| Estudar o avaliador de alertas em C++ | [cpp/include/rtxmon/alerts.hpp](cpp/include/rtxmon/alerts.hpp) |
| Estudar o avaliador de alertas em C# | [csharp/RtxMonitor.Managed/Alerts.cs](csharp/RtxMonitor.Managed/Alerts.cs) |
| Conhecer as decisões de arquitetura | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |

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
- testa seleção por UUID, inventário de capacidades e streams contínuos.

O GitHub Actions usa `scripts/verify-ci.ps1`, que não exige GPU. A validação física continua separada em `scripts/verify.ps1`.

## Segurança e limites atuais

- O projeto é **somente leitura**.
- Não altera ventoinha, clock, tensão ou limite de energia.
- Não exige privilégio administrativo por decisão de projeto.
- Não lê I2C, DDC, SMBus, MMIO, ROM ou registradores privados.
- Não transforma um sensor desconhecido em hotspot, VRM ou memória.
- A DLL NVIDIA é carregada por um caminho confiável do sistema.
- A identidade entre NVML e NVAPI é correlacionada pelo endereço PCI, não pela ordem em que as APIs listam as placas.
- A camada nativa já possui um caminho de carregamento para Linux, mas os scripts de compilação e validação desta versão são voltados ao Windows x64.

## Documentação técnica

- [Arquitetura e contratos](docs/ARCHITECTURE.md)
- [Procedimento de validação](docs/VALIDATION.md)
- [ADR 0001 — uso da NVML](docs/adr/0001-use-nvml.md)
- [ADR 0002 — descoberta pública de capacidades](docs/adr/0002-public-capability-discovery.md)
- [ADR 0003 — monitoramento resiliente](docs/adr/0003-resilient-sampling.md)
- [ADR 0004 — alertas de limiar](docs/adr/0004-threshold-alerts.md)
- [Schema JSON de capacidades](docs/schema/capabilities-v2.schema.json)
- [Schema JSON de eventos atual — v2](docs/schema/telemetry-event-v2.schema.json)
- [Schema JSON histórico de eventos — v1](docs/schema/telemetry-event-v1.schema.json)
- [Avisos de componentes de terceiros](THIRD_PARTY_NOTICES.md)

## Referências oficiais

- [Visão geral da NVML](https://docs.nvidia.com/deploy/nvml-api/nvml-api-reference.html)
- [Consultas de dispositivo na NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceQueries.html)
- [Campos públicos da NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlFieldValueEnums.html)
- [Sensores térmicos da NVAPI](https://docs.nvidia.com/nvapi/group__gputhermal.html)
