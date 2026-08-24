# Arquitetura de engenharia

## Objetivo e fronteira da verdade

O sistema deve observar, com baixa sobrecarga e sem alterar o estado da placa, a temperatura corrente que o driver NVIDIA associa ao sensor do die da GPU.

O dado autoritativo do software é:

```text
NVML device handle + NVML_TEMPERATURE_GPU -> temperatura inteira em °C
```

O programa não afirma acesso elétrico direto ao sensor. Registradores térmicos privados, MMIO, SMBus e detalhes do microcontrolador da placa não têm contrato público estável e poderiam comprometer segurança, portabilidade e precisão. A calibração permanece sob controle do firmware/driver NVIDIA.

## Componentes

| Componente | Linguagem | Responsabilidade |
|---|---|---|
| `rtxmon_native` | C11 | Localizar NVML, resolver símbolos, inicializar/finalizar a biblioteca, enumerar GPUs e ler o sensor do die. |
| `rtxmon.h` | ABI C | Contrato binário versionado compartilhado por qualquer consumidor. |
| `rtxmon_core` | C++20 | RAII, exceções tipadas, modelos de GPU/amostra e conversão de tempo. |
| `rtxmon` | C++20 | CLI de uma amostra, inventário, watch e JSON Lines. |
| `RtxMonitor.Managed` | C#/.NET 8 | P/Invoke, `SafeHandle`, verificação de ABI e API gerenciada reutilizável. |
| `RtxMonitor.Console` | C#/.NET 8 | Dashboard de terminal, estatísticas da sessão e saída JSON. |

## Fluxo de uma leitura

1. O consumidor cria um contexto.
2. A camada C carrega a biblioteca NVML por caminho confiável.
3. `nvmlInit_v2` estabelece a sessão com o driver.
4. O índice solicitado é confrontado com `nvmlDeviceGetCount_v2`.
5. `nvmlDeviceGetHandleByIndex_v2` obtém um handle atual do dispositivo.
6. A camada tenta `nvmlDeviceGetTemperatureV` com uma estrutura NVML v1 e sensor `NVML_TEMPERATURE_GPU`.
7. Se o símbolo/API moderna não existir, usa `nvmlDeviceGetTemperature` como fallback explícito.
8. O timestamp UTC é capturado imediatamente após o sucesso.
9. C++ ou C# apresentam o valor sem interpolar ou acrescentar precisão fictícia.

Buscar o handle em cada amostra evita manter um handle opaco além de um reset do driver. A frequência padrão é 1 Hz; 100 ms é o mínimo exposto para evitar polling acidentalmente agressivo.

## ABI C

A fronteira nativa é C, não C++, porque nomes, exceptions e layouts C++ não formam uma ABI estável entre compiladores. Os contratos usam tipos de largura fixa e estruturas com `struct_size`:

- `rtxmon_gpu_info_t`: índice, nome, UUID e versões de driver/NVML;
- `rtxmon_temperature_sample_t`: índice, °C, tipo de sensor, backend e timestamp Unix em ms;
- `rtxmon_status_t`: erros de argumento, backend, driver, permissão, suporte, GPU perdida e incompatibilidade de ABI.

O chamador inicializa `struct_size`; a DLL rejeita um layout menor. A ABI atual é `RTXMON_ABI_VERSION = 1`.

## Concorrência e ciclo de vida

NVML é documentada como thread-safe. A tabela de símbolos no contexto é imutável depois da inicialização, e cada leitura usa apenas estado local. Diagnósticos ficam em armazenamento thread-local.

Regras do contrato:

- um contexto pode atender leituras concorrentes;
- o contexto não pode ser destruído enquanto houver uma chamada em andamento;
- C++ controla o contexto por RAII;
- C# controla o ponteiro por `SafeHandle`;
- toda criação bem-sucedida corresponde a um `nvmlShutdown`.

## Segurança

O projeto não carrega `nvml.dll` do diretório atual nem de um `PATH` arbitrário. No Windows, tenta:

1. `%SystemRoot%\System32\nvml.dll` para drivers DCH;
2. `%ProgramW6432%\NVIDIA Corporation\NVSMI\nvml.dll` para instalação padrão.

Não há API para escrever clocks, fan, tensão, power limit ou configuração do driver. Não há execução de shell na biblioteca ou nos aplicativos. `nvidia-smi` aparece apenas no script de verificação como referência externa independente.

## Modelo de falhas

Falhas não são convertidas em zero grau nem em dados antigos. Cada camada propaga um status explícito:

- biblioteca ausente;
- símbolo requerido ausente;
- driver não carregado;
- acesso negado;
- índice de GPU inexistente;
- sensor não suportado;
- GPU perdida;
- ABI incompatível;
- erro NVML preservado com texto e código.

O modo watch termina com erro se a leitura falhar. Uma futura política de reconexão deve ser adicionada acima da camada C, com backoff e sinalização de lacuna; ela não deve ocultar amostras perdidas.

## Dados e observabilidade

Cada JSON contém:

```json
{
  "schema_version": 1,
  "gpu_index": 0,
  "gpu_name": "NVIDIA GeForce RTX ...",
  "gpu_uuid": "GPU-...",
  "temperature_c": 48,
  "sensor": "gpu_die",
  "backend": "NVML nvmlDeviceGetTemperatureV",
  "timestamp_unix_ms": 1787589572574
}
```

`temperature_c` permanece inteiro porque essa é a resolução do contrato NVML utilizado. A média exibida pelo dashboard é uma estatística de várias amostras, não uma leitura com maior precisão física.

## Extensões seguras

Próximas camadas podem ser adicionadas sem alterar o coletor:

- buffer circular e persistência SQLite/Parquet;
- serviço Windows separado do frontend;
- endpoint local HTTP/SSE ou OpenTelemetry;
- alertas baseados em thresholds configuráveis ou thresholds consultados do driver;
- seleção persistente por UUID em vez de índice;
- frontend desktop, mantendo `RtxMonitor.Managed` como fronteira.

Qualquer threshold deve ser rotulado como política ou limite fornecido pelo driver; não deve ser apresentado como propriedade universal de toda RTX.
