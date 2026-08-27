# Inventário Windows GPU Performance Counters

- Data: 2026-08-26
- GPU alvo: NVIDIA GeForce RTX 3060, PCI `10de:2504`, subsystem `10de:1536`
- Estado: inventário concluído; provider de runtime v1 implementado

## Superfícies confirmadas

Há dois adaptadores: `luid_0x00000000_0x0001669B` é a RTX 3060 (engines 3D, Copy, VideoDecode, VideoEncode, OFA e VR); `luid_0x00000000_0x00017974` é o adaptador secundário.

| Counter set | Counter | Escopo |
| --- | --- | --- |
| `GPU Local Adapter Memory` | `Local Usage` | agregado por LUID |
| `GPU Non Local Adapter Memory` | `Non Local Usage` | agregado por LUID/partition |
| `GPU Process Memory` | dedicated/shared/local/non-local/committed | por PID e LUID |
| `GPU Engine` | utilization/running time | por PID, LUID e engine físico |

Leitura real: `658640896` bytes locais e `120369152` bytes não locais. São valores instantâneos, não capacidades.

## Regras obrigatórias

- Memória do adaptador vem dos conjuntos agregados; somar processos pode duplicar alocações compartilhadas.
- Para utilização, somar processos do mesmo engine físico, limitar a 100% e então usar o máximo entre engines do mesmo tipo.
- D3D global é o máximo entre engines físicos, não a soma de 3D, Copy e vídeo.
- Local e non-local permanecem separados; “dynamic memory” não será uma soma inventada.
- Ausência/falha vira estado explícito, nunca zero.

## Gate de identidade

O provider deve enumerar DXGI e casar `AdapterLuid`, `VendorId`, `DeviceId` e `SubSysId` com a identidade NVML. Ordem, topologia de engines e uso de memória não são identidade suficiente.

## Incremento implementado

1. Reader PDH com duas coletas para métricas de taxa.
2. Correlação DXGI LUID/PCI antes de qualquer leitura publicada.
3. Schema [`windows-telemetry-v1`](../schema/windows-telemetry-v1.schema.json).
4. Endpoint `/api/v1/gpus/{uuid}/windows-telemetry` servido por snapshot do worker.
5. Testes para GPU ausente, identidade incompatível e contrato HTTP.

Validação real nesta máquina: DXGI resolveu a RTX 3060 para `0x000000000001669b`; o endpoint retornou memória local e não local separadas e utilização por tipo de engine. Valores continuam instantâneos e variam entre coletas.

O contrato sempre publica `3D`, `Copy`, `VideoDecode`, `VideoEncode`, `OFA` e `VR`. Zero observado é `inactive`; ausência de uma amostra válida é `counter_unavailable`, sem conversão para zero. Fixtures substituem DXGI e PDH para cobrir identidade ausente/incompatível/ambígua, falha durante a coleta, amostra parcial, agregação de processos por engine físico e recuperação na tentativa seguinte.
