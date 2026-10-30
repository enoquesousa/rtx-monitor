> Registro histórico do candidato local, substituído pelo [provedor por perfil](PROFILE_TELEMETRY.md). As configurações, rota e pendências abaixo descrevem a etapa anterior à autorização do critério revisado.

# Telemetria experimental no serviço

A implementação da v0.11 oferece hotspot e tensão do núcleo exclusivamente para o perfil compilado e revisado desta Galax RTX 3060. O canal permanece experimental e desativado por padrão.

**Aceite térmico pendente:** a segunda execução com ABI 8 confirmou a cadência corrigida e 60 capturas elegíveis, mas hotspot excedeu 0,051 °C em 50/60 pares GPU-Z, com diferença máxima de 0,6375 °C. Tensão passou em 60/60 pares contra GPU-Z e HWiNFO, com diferença máxima de 0,00025 V. A correção adicional de publicação passou no pacote final: 269/269 respostas numéricas já estavam no SQLite. As falhas anteriores permanecem preservadas; a v0.11 ainda não está encerrada. [Resultados completos](research/2026-09-06-v011-correction-validation.md).

## Ativação em instância isolada

Execute o serviço compilado a partir de seu diretório de saída, com porta livre e banco próprio:

```powershell
.\RtxMonitor.Service.exe --RtxMonitor:Port=11883 `
  --RtxMonitor:DatabasePath=C:\caminho\isolado\experimental.db `
  --RtxMonitor:ExperimentalTelemetryEnabled=true `
  --RtxMonitor:ExperimentalIntervalMilliseconds=1000
```

Esse comando inicia um processo de console com porta e banco próprios. O intervalo aceito é de 100 a 60.000 ms e respeita também os limites compilados da aquisição. Para desativar, encerre a instância e reinicie com a opção `false`.

Com a opção `false`, o canal não inicia diagnóstico de perfil nem processos filhos privados. Quando elegível e ativado, usa dois filhos persistentes para os sensores e um filho transitório `--private-profile-worker` para cada diagnóstico. Todos são supervisionados e executam com a identidade do serviço, sem solicitar elevação. O diagnóstico realiza uma consulta e termina; timeout, cancelamento ou falha terminal impedem reinício automático da sessão afetada.

## Consumo

- API: `GET /api/v1/experimental/gpus/{gpuUuid}/telemetry`.
- SSE: `/api/v1/events`, no campo `event.experimental_telemetry` dos eventos de schema 7.
- Histórico: `/api/v1/history`, no mesmo campo dos registros persistidos.
- Exportação: os comandos existentes de exportação preservam o JSON do evento e a proveniência.

A resposta da API inclui estados de desativação, perfil incompatível, revogação e indisponibilidade. Clientes devem verificar o estado de cada campo antes de usar seu valor; zero não representa ausência. Consultar o endpoint não ativa o provedor. Leituras vencidas ou incompatíveis com a identidade atual perdem seu valor, mantendo estado explícito.

A correção de publicação separa a aquisição mais recente do relatório confirmado para consulta: números ficam disponíveis na API somente após o commit SQLite do evento correspondente. Os estados que retiram valores inválidos continuam imediatos. `publication_pending` indica que ainda não há um relatório confirmado seguro para retornar; seus valores, brutos e limites de aquisição ficam nulos. Uma resposta diagnóstica sem valores pode anteceder a persistência, enquanto uma resposta numérica deve corresponder a um evento já gravado. Essa implementação foi confirmada no pacote final.

O horário continua sendo o da aquisição original. A idade no commit é somada ao tempo monotônico decorrido; consultas, commits posteriores ou repetição da mesma aquisição não renovam sua validade.

Cada leitura identifica origem, estado, unidade, horário, perfil e revisão, bruto, fórmula e referências. No relatório experimental v2, o hotspot expõe o inteiro assinado recebido do driver: `raw_encoding=signed_fixed8`, `raw_value=wire_raw_value` e `value=raw_value / 256`. A ABI 8 fornece esse inteiro pelo novo export `rtxmon_read_private_thermal_channels_v2`; o export original e seus valores arredondados em milésimos permanecem disponíveis. A tensão conserva microvolts e `value=raw_value / 1000000`.

O objeto `acquisition` do hotspot registra o início UTC e os limites monotônicos nativos em milissegundos, com escopo `thermal_pair`. O horário do campo marca o fim nativo da aquisição. Os limites incluem espera pelo lock, diagnóstico e as duas consultas sequenciais; não tornam die, hotspot, tensão ou referências simultâneos. Em falhas, valor, bruto e intervalo ficam nulos.

`available` indica aquisição aceita pelos gates do perfil; na API corrigida, o número também exige publicação confirmada no SQLite. `profile_evidence_stage` e `tolerances` descrevem evidência histórica: 0,051 °C é o critério de equivalência com o arredondamento do GPU-Z, originalmente aplicado à observação das próprias chamadas térmicas desse programa e ao seu log. Não estabelece acurácia física nem um limite universal entre leitores assíncronos. As comparações prospectivas preservaram o critério e toda a janela, inclusive falhas; HWiNFO/hotspot permanece informativo, sem tolerância de aceite própria.

Na segunda execução, os 90 eventos SSE coincidiram com histórico/exportação e os 131 registros exportados mantiveram o JSON armazenado byte a byte. Entretanto, quatro das 90 respostas da API não estavam no histórico: a API expunha o snapshot de aquisição antes de sua persistência. O resultado global de transporte dessa rodada foi reprovado, motivando a correção de publicação descrita acima. Schemas, fórmulas e tempos passaram; isso não elimina a falha API/histórico nem a reprovação térmica.

**Publicação final aprovada:** em 30 segundos fixos, 269 respostas numéricas da API já estavam no SQLite no momento da consulta; 30 eventos SSE coincidiram com histórico/exportação e 51 registros exportados preservaram os bytes gravados. Schemas, bruto, fórmula e limites temporais passaram. Os testes controlados cobriram diagnósticos nulos, expiração sem renovar idades e falha real de inserção SQLite. O pacote padrão não iniciou filhos experimentais. Esse smoke não repetiu nem substituiu o ensaio térmico; evidências e identificação do ZIP estão no relatório acima.

O canal padrão continua emitindo eventos v5. Relatórios experimentais atuais usam v2 dentro de eventos v7; o candidato anterior v1/v6 permanece legível com sua representação original em milésimos. SQLite e exportação mantêm o contrato v1. O namespace experimental não aumenta a cobertura pública de 32/37 observada na v0.10 e não promove cooler privado.

Contratos atuais: [relatório experimental v2](schema/experimental-telemetry-v2.schema.json), [evento v7](schema/telemetry-event-v7.schema.json), [OpenAPI](openapi/service-v1.openapi.json), [decisão de arquitetura](adr/0013-experimental-service-provider.md). Contratos históricos: [relatório v1](schema/experimental-telemetry-v1.schema.json) e [evento v6](schema/telemetry-event-v6.schema.json).
