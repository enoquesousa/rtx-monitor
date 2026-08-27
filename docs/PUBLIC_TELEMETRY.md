# Catálogo de telemetria pública

Este documento descreve exatamente o que a v0.7.0 consulta, como cada valor é classificado e quais cálculos podem ser refeitos a partir do histórico.

O catálogo usa somente funções e IDs conhecidos das APIs públicas do driver. Ele não faz varredura de IDs, não lê registradores privados e não converte uma ausência em zero.

## Como consultar

Os CLIs C++ e C# oferecem o mesmo modo:

```powershell
.\build\windows-x64\bin\Release\rtxmon.exe --telemetry --json
.\csharp\RtxMonitor.Console\bin\Release\net8.0\RtxMonitor.Console.exe --telemetry --json
```

Durante `--watch --events`, cada evento `sample` também contém `public_telemetry` e `computed_metrics`. O serviço expõe o último relatório confirmado em:

```text
GET /api/v1/gpus/{uuid}/telemetry
```

## Como ler um campo

Cada registro guarda:

- `field`: nome estável do RTX Monitor;
- `provider`: função NVIDIA realmente utilizada;
- `provider_native_id`: ID documentado, seletor da função ou índice do fan;
- `state`: resultado daquela consulta;
- `origin`: `driver_reported` para valores devolvidos pelo driver e `computed` para razões derivadas;
- `value_type` e `unit`: tipo bruto e unidade, sem conversão oculta;
- um entre `value_u64`, `value_i64` e `value_f64` quando disponível;
- `native_status`: código devolvido pela NVML;
- `timestamp_unix_ms`: horário da observação.

Os estados possíveis são:

| Estado | Significado |
| --- | --- |
| `available` | O driver devolveu um valor válido. Zero pode ser um valor real. |
| `not_supported` | A função existe, mas esta combinação de GPU e driver não publica o campo. |
| `provider_unavailable` | A função necessária não existe na NVML carregada. |
| `query_failed` | A função existe, mas a consulta falhou. O código nativo é preservado. |

Nos três estados de ausência, o tipo fica `unknown` e os três valores ficam `null`.

## Campos consultados

### Temperatura, energia e potência por ID NVML

Estes IDs são enviados juntos a `nvmlDeviceGetFieldValues`:

| Campo RTX Monitor | ID NVML | Unidade | O que significa |
| --- | ---: | --- | --- |
| `memory_temperature_c` | 82 | °C | Temperatura da memória, somente quando publicada pelo driver. |
| `total_energy_mj` | 83 | mJ | Energia acumulada reportada pelo dispositivo. |
| `power_average_mw` | 185 | mW | Potência média no período definido pela NVML. |
| `power_instant_mw` | 186 | mW | Potência instantânea reportada pela NVML. |
| `power_limit_min_mw` | 187 | mW | Menor limite de potência permitido. |
| `power_limit_max_mw` | 188 | mW | Maior limite de potência permitido. |
| `power_limit_default_mw` | 189 | mW | Limite padrão do dispositivo. |
| `power_limit_current_mw` | 190 | mW | Limite efetivo atual. |
| `power_limit_requested_mw` | 192 | mW | Limite solicitado ao driver. |
| `temperature_shutdown_c` | 193 | °C | Limite térmico de desligamento; não é um sensor. |
| `temperature_slowdown_c` | 194 | °C | Limite térmico de redução; não é um sensor. |
| `temperature_memory_max_c` | 195 | °C | Limite máximo de memória; não é a temperatura atual. |
| `temperature_gpu_max_c` | 196 | °C | Limite máximo do GPU; não é a temperatura atual. |

### Funções NVML

| Campo RTX Monitor | Provedor | ID/seletor | Unidade |
| --- | --- | ---: | --- |
| `gpu_die_temperature_c` | `nvmlDeviceGetTemperatureV`, com fallback legado explícito | GPU = 0 | °C |
| `clock_graphics_mhz` | `nvmlDeviceGetClockInfo` | graphics = 0 | MHz |
| `clock_sm_mhz` | `nvmlDeviceGetClockInfo` | SM = 1 | MHz |
| `clock_memory_mhz` | `nvmlDeviceGetClockInfo` | memory = 2 | MHz |
| `clock_video_mhz` | `nvmlDeviceGetClockInfo` | video = 3 | MHz |
| `utilization_gpu_percent` | `nvmlDeviceGetUtilizationRates` | GPU = 0 | % |
| `utilization_memory_percent` | `nvmlDeviceGetUtilizationRates` | memory = 1 | % |
| `memory_total_bytes` | `nvmlDeviceGetMemoryInfo` | total = 0 | bytes |
| `memory_free_bytes` | `nvmlDeviceGetMemoryInfo` | free = 1 | bytes |
| `memory_used_bytes` | `nvmlDeviceGetMemoryInfo` | used = 2 | bytes |
| `fan_speed_percent` | `nvmlDeviceGetFanSpeed_v2`, com fallback legado | índice físico do fan | % |
| `performance_state` | `nvmlDeviceGetPerformanceState` | 0 | P-state |
| `clock_event_reasons_current` | `nvmlDeviceGetCurrentClocksEventReasons`, com fallback legado | 0 | bitmask |
| `clock_event_reasons_supported` | `nvmlDeviceGetSupportedClocksEventReasons`, com fallback legado | 0 | bitmask |
| `encoder_utilization_percent` | `nvmlDeviceGetEncoderUtilization` | utilization = 0 | % |
| `encoder_sampling_period_us` | `nvmlDeviceGetEncoderUtilization` | period = 1 | µs |
| `decoder_utilization_percent` | `nvmlDeviceGetDecoderUtilization` | utilization = 0 | % |
| `decoder_sampling_period_us` | `nvmlDeviceGetDecoderUtilization` | period = 1 | µs |
| `temperature_gpu_limit_c` | `nvmlDeviceGetTemperatureThreshold` | GPU max = 3 | °C |

### PerfCap traduzido e potência relativa

`clock_event_reasons_current` continua preservando a máscara bruta. As saídas JSON e HTTP também expõem `performance_limit_reasons`, com `raw_bitmask`, todos os `active_reasons` conhecidos e um `primary_reason` estável. Os bits traduzidos são `gpu_idle`, `application_clocks`, `software_power_cap`, `hardware_slowdown`, `sync_boost`, `software_thermal`, `hardware_thermal`, `hardware_power_brake` e `display_clock`.

Dois campos calculados evitam a ambiguidade de “% TDP”:

| Campo | Fórmula | Origem |
| --- | --- | --- |
| `power_consumption_default_limit_percent` | `power_instant_mw / power_limit_default_mw * 100` | `computed` |
| `power_consumption_current_limit_percent` | `power_instant_mw / power_limit_current_mw * 100` | `computed` |

Se potência ou denominador não estiverem disponíveis, o resultado preserva o estado de ausência; denominador zero resulta em `query_failed`, nunca em zero inventado.

O catálogo possui 34 campos semânticos. `fan_speed_percent` pode gerar mais de um registro porque cada fan recebe seu próprio `provider_native_id`. Por isso o relatório reserva até 48 registros.

## Papel da NVAPI

No Windows, a NVAPI continua como fonte complementar do inventário térmico de `--capabilities`. Ela é correlacionada com a GPU NVML por identidade PCI e preserva alvo, controlador, índice e estado próprios.

A v0.7.0 não duplica no stream um valor NVAPI que já representa o mesmo die sem uma identidade física adicional. Isso mantém o catálogo de amostragem sem pseudossensores. O inventário NVML/NVAPI completo permanece disponível em `--capabilities`.

## Métricas calculadas

As métricas são produzidas por um motor C++ stateful, exposto pela ABI C e reutilizado pelo C#.

| Métrica | Fórmula registrada | Unidade | Estado mínimo |
| --- | --- | --- | --- |
| `gpu_temperature_window_average` | média das temperaturas do die dentro da janela | °C | uma amostra |
| `gpu_temperature_slope` | `(última - primeira) / segundos decorridos` | °C/s | duas amostras com horários distintos |
| `gpu_temperature_time_above_threshold` | soma dos intervalos recortados cuja amostra anterior está acima do limiar | s | duas amostras |
| `gpu_memory_temperature_delta` | `gpu_die_temperature_c - memory_temperature_c` no mesmo snapshot | °C | os dois canais disponíveis |

Além do valor, cada métrica registra `formula`, `unit`, `window_ms`, `sample_count`, `temperature_threshold_c`, `inputs` e `origin=computed`.

`insufficient_data` significa que a entrada existe, mas a janela ainda não tem amostras suficientes. `input_unavailable` significa que um canal necessário não foi publicado. Nenhum desses estados produz zero. Um zero com estado `available` é um resultado matemático legítimo.

A janela padrão é 5 segundos, o limiar padrão é 80 °C e o limite padrão é 1.024 amostras. O serviço permite configurar os três valores em `appsettings.json`.

## Reprodutibilidade e versões

- ABI nativa: versão 3;
- relatório avulso de `--telemetry --json`: [`public-telemetry-v2`](schema/public-telemetry-v2.schema.json); v1 permanece preservado;
- eventos `--watch --events`: `telemetry-event-v4`; v1, v2 e v3 permanecem preservados;
- SQLite: schema 1, sem migration estrutural; o JSON v4 é armazenado integralmente;
- API HTTP local: schema 2 para a resposta de telemetria.

O evento persistido contém tanto as entradas brutas quanto a saída calculada. Assim, um consumidor pode refazer a métrica e comparar fórmula, janela, amostras e resultado. Eventos v1 e v2 continuam publicados para validar históricos anteriores.

## Referências oficiais

- [NVML field value queries](https://docs.nvidia.com/deploy/nvml-api/group__nvmlFieldValueQueries.html)
- [NVML field IDs](https://docs.nvidia.com/deploy/nvml-api/group__nvmlFieldValueEnums.html)
- [NVML device queries](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceQueries.html)
- [NVML clock event reasons](https://docs.nvidia.com/deploy/nvml-api/group__nvmlClocksEventReasons.html)
- [NVML known issues](https://docs.nvidia.com/deploy/nvml-api/known-issues.html)

As referências oficiais são a autoridade sobre o significado das funções NVIDIA. O RTX Monitor mantém sua própria lista explícita para impedir consultas acidentais a IDs desconhecidos e registra versão de driver, VBIOS e perfil da placa para tornar diferenças observáveis.
