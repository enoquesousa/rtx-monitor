# Telemetria independente por perfil

> Nota de publicação: o identificador da GPU foi substituído por um valor sintético; os resultados históricos e os limites do perfil permanecem descritos.


A v0.11 fornece hotspot e tensão do núcleo por acesso direto ao driver NVIDIA.
O provedor é validado exclusivamente para a Galax RTX 3060 do proprietário,
UUID `GPU-11111111-2222-3333-4444-555555555555`, PCI `10de:2504`, subsystem
`10de:1536`, VBIOS `94.06.25.00.fc`, driver `610.88` e módulo NVAPI fixado
por SHA-256 no perfil. Mudanças nessa identidade bloqueiam leituras.

GPU-Z e HWiNFO serviram como referências de pesquisa. O serviço não lê seus
processos, logs, memória compartilhada ou endpoints para obter valores.
Usa seu próprio backend nativo e filhos supervisionados, sem elevação.

## Ativação

O provedor permanece desativado por padrão. Execute o pacote extraído com
porta livre e banco próprio:

```powershell
.\RtxMonitor.Service.exe --RtxMonitor:Port=11883 `
  --RtxMonitor:DatabasePath=C:\caminho\isolado\profile.db `
  --RtxMonitor:ProfileTelemetryEnabled=true `
  --RtxMonitor:ProfileIntervalMilliseconds=1000
```

Intervalos aceitos: 100 a 60.000 ms, sujeitos também aos limites nativos.
As chaves antigas `ExperimentalTelemetryEnabled` e
`ExperimentalIntervalMilliseconds` continuam aceitas quando a respectiva
chave nova está ausente. Declarar ambas causa erro explícito. O arquivo padrão
omite essas opções; os valores internos padrão são `false` e `1000`.
Reinicie com `ProfileTelemetryEnabled=false` para desativar.

## Contrato

- API: `GET /api/v1/gpus/{gpuUuid}/profile-telemetry`.
- SSE e histórico: `event.profile_telemetry`, em eventos v8.
- Relatório: `profile-telemetry` v1, origem `private_profile` e
  `runtime_source=direct_nvidia_driver`.
- Exportação: preserva os bytes do evento gravado e sua versão original.

O endpoint anterior `/api/v1/experimental/gpus/{gpuUuid}/telemetry` pertencia
ao candidato local e foi substituído. Consumidores desse candidato devem
migrar explicitamente. Históricos v1–v7 e os schemas experimentais v1/v2
permanecem legíveis sem reinterpretar os dados. O modo padrão mantém eventos
v5; os contratos públicos e a cobertura pública não foram alterados.
Os nomes internos C# `Experimental*` permanecem por compatibilidade de fonte;
o contrato público vigente está versionado separadamente.

Cada campo contém estado, bruto, fórmula, unidade, horário, perfil, revisão,
módulo, interface, estrutura e referências. Hotspot conserva o inteiro
`signed_fixed8` do driver e usa `value=raw_value / 256`; tensão conserva
microvolts e usa `value=raw_value / 1000000`. Não há ajuste ou compensação.
A ABI8 acrescenta o bruto térmico sem modificar o export nativo anterior.

O intervalo `acquisition` do hotspot delimita o par térmico no relógio nativo
monotônico e registra o início UTC. Não implica simultaneidade com tensão ou
referências externas. Em falha, valor, bruto e tempos ficam nulos.

Números só aparecem na API após commit SQLite. Diagnósticos que retiram valores
inválidos são imediatos; `publication_pending` não contém números. Consultas
e commits repetidos não renovam a idade da aquisição. Timeout, revogação,
identidade divergente, payload inválido e expiração não reutilizam valores
anteriores. As consultas HTTP não iniciam aquisição.

## Validação e limites

O critério `v011-source-fidelity-1` foi autorizado pelo proprietário em
2026-09-06. Ele exige fonte identificada no perfil, conversão exata, transporte
fiel, independência e comportamento supervisionado. Estabilidade do provedor
não significa API pública documentada nem certificação de precisão física.

A tolerância térmica de 0,051 °C descreve arredondamento da mesma aquisição
na referência, indicado por `scope=same_acquisition_reference_rounding`.
Comparações entre aquisições distintas são `diagnostic_only`; o contrato
declara `physical_accuracy_guarantee=false`. As tolerâncias de tensão retêm
o escopo de comparação registrada com GPU-Z/HWiNFO.

Os comparativos assíncronos anteriores permanecem registrados: hotspot
excedeu 0,051 °C em 50/60 pares, máximo 0,6375 °C; a captura posterior com
horários individuais excedeu em 5/9 pares, máximo 0,37525 °C. Não foram
aplicados offset, aumento de tolerância ou seleção de janela. Os 18 retornos
da própria referência ficaram dentro do arredondamento documentado.

Veja [critério e pesquisa](research/2026-09-06-v011-stabilization.md),
[validação final](research/2026-09-06-v011-stable-validation.md),
[schema do relatório](schema/profile-telemetry-v1.schema.json),
[eventos v8](schema/telemetry-event-v8.schema.json) e
[OpenAPI](openapi/service-v1.openapi.json).

## Diagnóstico de cadência

O pacote operacional inclui o evento opcional de log Debug `1101`,
`ProfileAcquisition`, na categoria `RtxMonitor.Service.ExperimentalTelemetryWorker`.
Ele registra cada coleta, os estados, os horários dos campos e o relógio nativo
monotônico térmico. Permite separar aquisição, publicação e observação HTTP.
Não altera a fonte dos valores e fica desativado no nível normal de log.
Veja a [validação operacional e instalação](research/2026-09-06-v011-operational-validation.md).
