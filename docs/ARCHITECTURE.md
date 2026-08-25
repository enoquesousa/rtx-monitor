# Arquitetura de engenharia

## Objetivo e fronteira da verdade

O sistema deve observar, com baixa sobrecarga e sem alterar o estado da placa, a temperatura corrente que o driver NVIDIA associa ao die e inventariar todos os canais térmicos que as APIs públicas realmente expõem.

O dado autoritativo do software é:

```text
NVML device handle + NVML_TEMPERATURE_GPU -> temperatura inteira em °C
```

O inventário usa um segundo contrato de verdade:

```text
placa PCI/VBIOS + provedor + índice nativo + alvo + controlador + estado -> capability observada
```

O programa não afirma acesso elétrico direto ao sensor. Registradores térmicos privados, MMIO, SMBus, I2C/DDC e detalhes do microcontrolador da placa não têm um contrato térmico público estável. A calibração permanece sob controle do firmware/driver NVIDIA.

## Componentes

| Componente | Linguagem | Responsabilidade |
|---|---|---|
| `rtxmon_native` / `rtxmon.c` | C11 | Ciclo de vida, ABI pública, identidade da GPU/placa e leitura principal do die. |
| `thermal_scan.c` | C11 | Provedores térmicos, correlação PCI NVML/NVAPI e montagem do snapshot de capabilities. |
| `rtxmon.h` | ABI C | Contrato binário versionado compartilhado por qualquer consumidor. |
| `rtxmon_core` | C++20 | RAII, exceções tipadas, modelos de GPU/amostra e conversão de tempo. |
| `sampler.cpp` / `sampler.hpp` | C++20 | Seleção por UUID, eventos, backoff, reconexão e buffer circular limitado. |
| `rtxmon` | C++20 | CLI de amostra, watch resiliente, GPUs e capabilities; JSON versionado. |
| `RtxMonitor.Managed` | C#/.NET 8 | P/Invoke, `SafeHandle`, layouts verificados e sampler resiliente equivalente. |
| `RtxMonitor.Console` | C#/.NET 8 | Dashboard de terminal, eventos de disponibilidade, estatísticas e saída JSON. |

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

## Fluxo do inventário térmico

1. NVML fornece identidade PCI e versão VBIOS da GPU selecionada.
2. `nvmlDeviceGetThermalSettings(..., ALL, ...)` retorna até três descritores de sensor.
3. `nvmlDeviceGetFieldValues` consulta especificamente `NVML_FI_DEV_MEMORY_TEMP` (campo 82).
4. No Windows, NVAPI enumera GPUs físicas e a camada C encontra a correspondência por bus, slot, device ID e subsystem ID.
5. `NvAPI_GPU_GetThermalSettings(..., ALL, ...)` retorna até três descritores públicos adicionais.
6. Cada tentativa gera um estado explícito: `available`, `not_supported`, `provider_unavailable` ou `query_failed`.
7. Alvo e controlador são normalizados, mas fonte, índice e código nativo permanecem no relatório.

Não há deduplicação semântica: duas APIs que reportam o mesmo die continuam como duas observações independentes. Isso permite comparar as fontes e evita alegar que sensores com nomes parecidos são fisicamente idênticos.

## Fluxo do monitoramento resiliente

1. O CLI resolve o índice inicial para UUID, ou recebe `--gpu-uuid` diretamente.
2. O sampler abre uma sessão curta de monitoramento e enumera as GPUs.
3. O alvo é localizado por comparação de UUID sem distinção de maiúsculas e minúsculas.
4. Uma leitura válida produz `sample` e zera o contador de falhas.
5. Uma falha recuperável produz `gap`, descarta a sessão e não conserva temperatura atual.
6. A próxima tentativa espera 250 ms; falhas sucessivas dobram o intervalo até o limite de 5 s.
7. Uma nova sessão enumera novamente as placas e pode encontrar o UUID em outro índice.
8. O primeiro sucesso após falhas produz `recovered` seguido por `sample`.
9. Cada evento recebe sequência crescente e entra em um buffer circular de capacidade fixa.

`--count` conta somente amostras bem-sucedidas. O buffer padrão mantém os 256 eventos mais recentes e pode ser configurado entre 1 e 65536. Ele não é persistência em disco.

A fábrica de sessões é injetável em C++ e C#. Os testes usam sessões simuladas para reproduzir perda da GPU, driver indisponível, mudança de índice e recuperação sem carregar NVML ou exigir hardware NVIDIA.

A saída `--watch --json` permanece compatível com o schema de amostra v1 e envia diagnósticos de lacuna para `stderr`. O modo opt-in `--watch --events` envia todos os eventos em JSON Lines.

## ABI C

A fronteira nativa é C, não C++, porque nomes, exceptions e layouts C++ não formam uma ABI estável entre compiladores. Os contratos usam tipos de largura fixa e estruturas com `struct_size`:

- `rtxmon_gpu_info_t`: índice, nome, UUID e versões de driver/NVML;
- `rtxmon_temperature_sample_t`: índice, °C, tipo de sensor, backend e timestamp Unix em ms;
- `rtxmon_board_identity_t`: IDs PCI, endereço de barramento, flags de validade e VBIOS;
- `rtxmon_thermal_provider_result_t`: disponibilidade da superfície de API e código nativo;
- `rtxmon_thermal_capability_t`: fonte, ID nativo do provedor, alvo, controlador, valores válidos, estado e confiança;
- `rtxmon_thermal_report_t`: snapshot fixo de três provedores e até oito registros;
- `rtxmon_status_t`: erros de argumento, backend, driver, permissão, suporte, GPU perdida e incompatibilidade de ABI.

O chamador inicializa `struct_size`; a DLL rejeita um layout menor. A ABI atual é `RTXMON_ABI_VERSION = 2`. Os layouts são testados em C e novamente medidos pelo runtime .NET antes da abertura do contexto.

### Semântica de disponibilidade

`provider.state = available` significa que a função pública pôde ser chamada. Cada capability possui seu próprio estado. Assim, o provedor `nvmlDeviceGetFieldValues` pode estar disponível enquanto o campo de temperatura da memória retorna `not_supported` para uma placa específica. `capability_count` é a quantidade de registros produzidos pelo provedor, inclusive registros negativos auditáveis.

`confidence = driver_reported` significa que a definição do canal e, quando presente, seu valor vieram do contrato do driver. O projeto reserva `experimental`, mas a fase atual não produz leituras experimentais.

## Concorrência e ciclo de vida

NVML é documentada como thread-safe. A tabela de símbolos no contexto é imutável depois da inicialização, e cada leitura usa apenas estado local. Diagnósticos ficam em armazenamento thread-local. Chamadas NVAPI são serializadas no processo porque o contrato público não oferece a mesma garantia de concorrência.

Regras do contrato:

- um contexto pode atender leituras concorrentes;
- o contexto não pode ser destruído enquanto houver uma chamada em andamento;
- C++ controla o contexto por RAII;
- C# controla o ponteiro por `SafeHandle`;
- cada instância do sampler tem um único proprietário e não deve receber chamadas `poll` concorrentes;
- toda criação bem-sucedida corresponde a um `nvmlShutdown`;
- toda inicialização NVAPI bem-sucedida corresponde a `NvAPI_Unload`.

## Segurança

O projeto não carrega DLLs NVIDIA do diretório atual nem de um `PATH` arbitrário. No Windows, tenta:

1. `%SystemRoot%\System32\nvml.dll` para drivers DCH;
2. `%ProgramW6432%\NVIDIA Corporation\NVSMI\nvml.dll` para instalação padrão.

NVAPI é carregada exclusivamente como `%SystemRoot%\System32\nvapi64.dll` (ou `nvapi.dll` em 32 bits).

Não há API para escrever clocks, fan, tensão, power limit ou configuração do driver. Também não há transação I2C, mapeamento MMIO, leitura de ROM ou driver kernel próprio. Não há execução de shell na biblioteca ou nos aplicativos. `nvidia-smi` aparece apenas no script de verificação como referência externa independente.

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

Nos modos `--once` e `--capabilities`, uma falha continua encerrando o comando. No modo watch, estados recuperáveis são convertidos em lacunas e novas tentativas com backoff. Estados que indicam erro de uso, permissão, falta de memória, sensor não suportado ou ABI incompatível continuam fatais.

O sampler nunca repete uma amostra anterior durante a indisponibilidade. Uma lacuna contém status, diagnóstico, quantidade de falhas consecutivas e atraso até a próxima tentativa. A sessão nativa é descartada para que a tentativa seguinte recarregue o contexto e resolva novamente o UUID.

O inventário tem uma política diferente: uma falha individual de provedor não invalida o relatório inteiro. O snapshot retorna com o estado e o código nativo da falha para permitir diagnóstico e comparação entre máquinas.

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

O comando `--capabilities --json` usa `schema_version: 2` e separa:

- `gpu`: identidade lógica por índice/UUID e versões do driver/NVML;
- `board`: identidade física PCI, VBIOS e uma chave de perfil reproduzível;
- `providers`: estado de cada superfície pública;
- `thermal_capabilities`: observações com proveniência completa e temperaturas anuláveis.

O contrato serializado é formalizado em [`docs/schema/capabilities-v2.schema.json`](schema/capabilities-v2.schema.json).

O comando `--watch --events` usa o schema independente [`docs/schema/telemetry-event-v1.schema.json`](schema/telemetry-event-v1.schema.json). Cada envelope contém:

- `event_type`: `sample`, `gap` ou `recovered`;
- `sequence` e horário observado;
- UUID persistente e índice atual anulável;
- status normalizado, código e diagnóstico;
- contagem de falhas e próximo backoff;
- amostra anulável, presente somente em `sample`.

## Extensões seguras

Próximas camadas podem ser adicionadas sem alterar o coletor:

- persistência SQLite/Parquet alimentada pelo stream de eventos;
- serviço Windows separado do frontend;
- endpoint local HTTP/SSE ou OpenTelemetry;
- alertas baseados em thresholds configuráveis ou thresholds consultados do driver;
- frontend desktop, mantendo `RtxMonitor.Managed` como fronteira;
- base de perfis por `vendor:device/subvendor:subdevice@vbios`, sem converter correlação em fato físico;
- modo experimental separado, somente se houver documentação por placa, validação cruzada e isolamento de risco.

Qualquer threshold deve ser rotulado como política ou limite fornecido pelo driver; não deve ser apresentado como propriedade universal de toda RTX.
