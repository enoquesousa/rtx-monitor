# RTX Monitor

Monitor de baixo nível, somente leitura, para a temperatura e as capacidades térmicas que o driver de uma **GPU NVIDIA RTX** realmente publica. O projeto usa três linguagens com fronteiras explícitas:

- C carrega NVML e, no Windows, NVAPI diretamente do driver e expõe uma ABI estável;
- C++ oferece RAII, modelo de domínio e CLIs de leitura e inventário;
- C# consome a ABI C por P/Invoke e entrega a mesma telemetria em uma API gerenciada.

Não há subprocesso de `nvidia-smi`, scraping de texto, estimativa ou interpolação da temperatura.

## O que significa “temperatura real”

O valor principal exibido é a leitura inteira, em graus Celsius, do sensor **GPU die** que o firmware/driver NVIDIA disponibiliza por NVML. A NVIDIA define `NVML_TEMPERATURE_GPU` como o sensor do die. Essa é a mesma infraestrutura de gerenciamento usada pelo `nvidia-smi`.

Isso não é acesso ao ADC físico, a registradores privados ou a uma sonda externa. O driver pode aplicar calibração, agregação e arredondamento antes de expor o valor. Portanto, o projeto chama a medição de **leitura real reportada pelo driver**, e não de temperatura analógica bruta.

O inventário adicional consulta apenas contratos públicos:

- `nvmlDeviceGetThermalSettings`: até três sensores e seus alvos/controladores;
- `nvmlDeviceGetFieldValues` com `NVML_FI_DEV_MEMORY_TEMP`: temperatura de memória quando suportada;
- `NvAPI_GPU_GetThermalSettings`: visão térmica pública complementar no Windows.

Cada resultado conserva fonte, alvo, controlador, estado, código nativo e confiança. Um canal não publicado aparece como `not_supported` ou `provider_unavailable`; nunca é transformado em hotspot, VRM ou temperatura estimada.

Referências oficiais:

- [Visão geral da NVML](https://docs.nvidia.com/deploy/nvml-api/nvml-api-reference.html)
- [Consultas de temperatura do dispositivo](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceQueries.html)
- [Enum do sensor do die](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceEnums.html)
- [Estruturas térmicas NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceStructs.html)
- [Consultas de campos NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlFieldValueQueries.html)
- [Sensores térmicos NVAPI](https://docs.nvidia.com/nvapi/group__gputhermal.html)

## Arquitetura

```text
sensores + firmware + driver NVIDIA
                  │
       ┌──────────┴───────────┐
       ▼                      ▼
 NVML pública            NVAPI pública
 die / thermal / field   thermal settings
       └──────────┬───────────┘
                  ▼
        rtxmon_native (C, ABI v2)
             ┌────┴──────────────┐
             ▼                   ▼
     rtxmon_core (C++)   RtxMonitor.Managed (C#)
             │                   │
         rtxmon.exe      RtxMonitor.Console.exe
```

A DLL C procura as bibliotecas NVIDIA somente em caminhos confiáveis do sistema, resolve os símbolos em runtime, prefere `nvmlDeviceGetTemperatureV` e usa a API antiga apenas como fallback de compatibilidade. NVAPI é opcional: sua ausência não impede a leitura NVML.

Leia [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) para contratos, modelo de falhas, segurança e extensões planejadas. As decisões estão registradas no [ADR 0001](docs/adr/0001-use-nvml.md) e no [ADR 0002](docs/adr/0002-public-capability-discovery.md).

Os componentes NVIDIA não são redistribuídos; consulte [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) para a origem das declarações mínimas de interoperabilidade.

## Requisitos

- Windows 10/11 x64;
- GPU NVIDIA compatível e driver instalado;
- Visual Studio 2022 Build Tools com C/C++;
- CMake 3.25 ou superior;
- .NET SDK 8 ou superior.

O código nativo também contém o caminho de carregamento Linux, mas a automação entregue nesta versão é Windows x64.

## Compilar

No PowerShell:

```powershell
.\scripts\build.ps1 -Configuration Release
```

Artefatos principais:

- `build\windows-x64\bin\Release\rtxmon_native.dll`
- `build\windows-x64\bin\Release\rtxmon-c.exe`
- `build\windows-x64\bin\Release\rtxmon.exe`
- `csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe`

## Executar

Monitor C# contínuo, com atualização a cada segundo:

```powershell
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe
```

Uma amostra pelo CLI C++:

```powershell
.\build\windows-x64\bin\Release\rtxmon.exe --once
```

Exemplo C puro:

```powershell
.\build\windows-x64\bin\Release\rtxmon-c.exe
```

Saída JSON estável para integração:

```powershell
.\build\windows-x64\bin\Release\rtxmon.exe --watch --interval 1000 --json
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe --once --json
```

Inventário térmico público, incluindo identidade PCI/VBIOS e estados negativos explícitos:

```powershell
.\build\windows-x64\bin\Release\rtxmon.exe --capabilities --json
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe --capabilities --json
```

O contrato dessa saída está em [capabilities-v2.schema.json](docs/schema/capabilities-v2.schema.json).

Para outra GPU, use `--gpu 1`. O intervalo permitido é de 100 a 60000 ms.

## Verificar de ponta a ponta

```powershell
.\scripts\verify.ps1 -Configuration Release
```

O script:

1. compila C, C++ e C# com avisos como erros;
2. executa os testes da ABI;
3. lê o sensor pelos três consumidores;
4. confirma o mesmo UUID contra `nvidia-smi`;
5. compara identidade da placa, fontes e capacidades entre C++ e C#;
6. confirma que a temperatura de memória tem um registro explícito, mesmo sem suporte;
7. tolera até 5 °C de diferença entre consultas sequenciais;
8. testa os modos contínuos C++ e C#.

## Princípios de engenharia

- somente leitura: nenhuma chamada de controle, clock, tensão, fan ou power limit;
- sem privilégio administrativo como requisito de projeto;
- DLL NVIDIA carregada por caminho absoluto para reduzir risco de DLL hijacking;
- ABI C versionada com `struct_size` em todas as estruturas de saída;
- erro nativo preservado por thread, sem estado global compartilhado de diagnóstico;
- timestamps produzidos imediatamente após a leitura do sensor;
- valor inteiro preservado: a UI não inventa casas decimais;
- multi-GPU endereçada por índice e validada por UUID;
- correlação NVML/NVAPI por identidade PCI exata, não por ordem de enumeração;
- hotspot, VRM e memória só recebem esses nomes quando o driver publica o alvo correspondente;
- nenhuma leitura I2C/DDC, SMBus, MMIO, ROM ou registrador privado nesta fase.
