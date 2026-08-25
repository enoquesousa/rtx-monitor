# ADR 0004: alertas de limiar acima do sampler resiliente

- Status: aceito
- Data: 2026-08-24

## Contexto

O ADR 0003 deu ao monitor um stream contínuo de amostras, lacunas e recuperações, mas nenhuma forma de sinalizar quando a temperatura do die cruza um valor que importa ao operador. Sem isso, notar uma GPU quente exige acompanhar `--watch` manualmente ou filtrar o stream `--events` fora do processo.

Um limiar de alerta não é um fato físico: é uma política definida por quem está monitorando. O projeto já registra essa distinção em `docs/ARCHITECTURE.md` — "Qualquer threshold deve ser rotulado como política ou limite fornecido pelo driver; não deve ser apresentado como propriedade universal de toda RTX." Qualquer mecanismo de alerta precisa preservar essa distinção e continuar sem inventar leituras durante uma lacuna.

## Decisão

Adicionar um avaliador de alertas pequeno e determinístico — `AlertEvaluator` em C++ e C# — que consome a temperatura de cada evento `sample` e produz transições `alert_raised` / `alert_cleared`. O avaliador:

1. não conhece sessão, GPU, thread ou relógio: recebe um inteiro e devolve uma transição opcional;
2. dispara `alert_raised` na primeira amostra com `temperature_c >= threshold_c`;
3. com histerese zero, dispara `alert_cleared` somente abaixo do limiar; com histerese positiva, dispara na primeira amostra com `temperature_c <= threshold_c - hysteresis_c`;
4. ignora eventos `gap` e `recovered` — o alerta só reage a uma leitura real, nunca à ausência de uma;
5. é reutilizável fora do CLI, com testes que não tocam GPU nem `ResilientSampler`.

O CLI aceita `--alert-threshold C` (exige `--watch`, intervalo 0-500) e `--alert-hysteresis C` (exige `--alert-threshold`, entre 0 e o limiar). Os eventos de alerta reaproveitam o envelope `TelemetryEvent` existente — kind, GPU, amostra que disparou a transição — em vez de um contrato paralelo, e carregam dois campos novos: `alert_threshold_c` e `alert_hysteresis_c`. Como o envelope ganhou campos obrigatórios, o schema de telemetria avança para `docs/schema/telemetry-event-v2.schema.json` (`schema_version: 2`, `event_type` com `alert_raised`/`alert_cleared`, os dois campos novos nulos fora desses tipos).

Um alerta é impresso mesmo sem `--events`: como `gap`/`recovered`, ele vai para `stderr` em texto, preservando o contrato de `--json` em watch mode (somente amostras no `stdout`). Com `--events`, o alerta entra no mesmo stream JSON Lines. O console C# também reflete o estado do alerta a cada quadro do dashboard interativo.

O `ResilientSampler` mantém sua sequência interna e seu buffer sem conhecer alertas. Na fronteira de saída, o CLI atribui novamente uma única sequência crescente a amostras, lacunas, recuperações e alertas. Isso preserva a separação entre as políticas e oferece identidade e ordem não ambíguas para persistência, deduplicação e retomada de consumidores, inclusive quando uma amostra e seu alerta compartilham o mesmo `observed_at_unix_ms`.

## Motivos

- Um limiar configurável pelo operador é a forma mais simples de alerta que não inventa nem interpreta dado do driver.
- Reagir somente a `sample` impede que uma lacuna prolongada seja lida como "esfriou" ou "esquentou".
- Histerese evita alternância de estado quando a temperatura oscila em torno do limiar.
- Reaproveitar `TelemetryEvent` mantém um único stream ordenável por tempo, em vez de um segundo formato de evento.
- Manter `AlertEvaluator` fora de `ResilientSampler` preserva o sampler como uma política só de conectividade/backoff — a mesma separação de responsabilidades do ADR 0003.
- C++ e C# implementam a mesma máquina de estados, testável sem GPU em ambos.

## Consequências

- `docs/schema/telemetry-event-v1.schema.json` permanece imutável para validar eventos históricos; novos streams `--events` usam `telemetry-event-v2.schema.json` e consumidores precisam migrar para receber os tipos de alerta.
- `--alert-threshold`/`--alert-hysteresis` não têm efeito fora de `--watch`; usá-los com `--once` é erro de uso.
- O buffer circular do `ResilientSampler` não retém eventos de alerta — eles não aparecem em `recent_events()`/`GetRecentEvents()`, só no stream impresso pelo CLI.
- O stream emitido pelo CLI possui sequência estritamente crescente entre todos os tipos de evento; a sequência interna do buffer do sampler continua limitada aos eventos que ele produz.
- Persistência de histórico de alertas continua fora de escopo, como já registrado em "Extensões seguras" no `ARCHITECTURE.md`.

## Alternativas rejeitadas

### Derivar o limiar dos valores padrão do driver

`capabilities-v2` já expõe `default_min_temperature_c`/`default_max_temperature_c` quando a NVML ou a NVAPI os publicam. Usá-los como limiar automático foi rejeitado nesta versão: esses valores descrevem o intervalo operacional default do driver, não necessariamente uma política de alerta desejada, e misturá-los com o limiar definido pelo usuário arriscava confundir "fato reportado pelo driver" com "política escolhida por quem monitora". Uma versão futura pode oferecer isso como um modo explícito e claramente rotulado.

### Múltiplos níveis de severidade (aviso/crítico)

Rejeitado por complexidade desnecessária na primeira versão do mecanismo. Um único par limiar/histerese já é suficiente; quem precisar de múltiplos níveis pode rodar mais de um processo `--watch` com limiares diferentes.

### Persistir o histórico de alertas em disco

Mantém a mesma decisão do ADR 0003 de reter estado apenas em memória. Persistência (SQLite/Parquet) continua listada como extensão futura, não como parte deste alerta.
