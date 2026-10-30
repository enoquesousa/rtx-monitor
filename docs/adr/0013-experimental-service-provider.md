# ADR 0013 — provedor experimental no serviço

Data: 2026-09-05; atualizada em 2026-09-06. Escopo: v0.11, exclusivamente o perfil compilado da Galax RTX 3060 de 12 GB do proprietário. Cadência e publicação corrigidas foram confirmadas no pacote; o aceite térmico permanece pendente e o marco ainda não está encerrado.

## Decisão

O serviço oferece hotspot e tensão do núcleo em `/api/v1/experimental/gpus/{gpuUuid}/telemetry`, ativados por `RtxMonitor:ExperimentalTelemetryEnabled=true`. A configuração padrão é `false`. O endpoint lê um snapshot; uma requisição HTTP não dispara aquisição.

No modo desativado, o canal não consulta o diagnóstico de perfil nem cria filhos privados. No modo elegível, mantém dois filhos supervisionados para os sensores e executa cada diagnóstico em um terceiro filho transitório, `--private-profile-worker`, com uma única resposta e encerramento confirmado. Assim, a abertura do contexto e os gates nativos do diagnóstico também ficam sujeitos a prazo e cancelamento fora do processo principal.

As operações fixas em user mode, a identidade, revisão, revogação, driver/VBIOS/módulo e limites compilados continuam obrigatórios. Os filhos herdam a identidade do serviço, sem helper privilegiado ou pedido de elevação. Timeout, cancelamento ou falha terminal encerram os processos afetados; a operação ou sessão invalidada não reinicia automaticamente para procurar sucesso.

## Bruto e limites temporais

A ABI 8 acrescenta `rtxmon_read_private_thermal_channels_v2`, com estrutura de 72 bytes. O export original de 40 bytes permanece inalterado. Ambos reutilizam a mesma implementação, gates, máscaras, índices e duas chamadas térmicas; o novo export conserva os inteiros fixed8 reais e os milésimos arredondados legados, sem reconstruir bruto a partir desses milésimos.

O relatório experimental v2 publica hotspot com `raw_encoding=signed_fixed8`, `raw_value=wire_raw_value` e `value=raw_value / 256`. Tensão mantém o inteiro em microvolts e divisão por 1.000.000. Origem `experimental`, estado e proveniência acompanham o valor; falhas deixam valor e bruto nulos.

O campo térmico inclui `acquisition` com início UTC e início/fim monotônicos nativos em milissegundos. Seu `captured_at` preserva o fim UTC da aquisição. O intervalo tem escopo `thermal_pair`, inclui lock e gates, e cobre duas consultas sequenciais. Não representa simultaneidade dos canais nem das referências externas. Relógios inválidos, intervalo incompatível ou par incompleto invalidam os dados e seus limites temporais.

## Eventos e persistência

O relatório experimental v2 é anexado aos eventos de amostra ou lacuna em schema 7. A coleta padrão mantém schema 5, a telemetria pública standalone mantém schema 3 e o HTTP público mantém schema 2. Eventos de alerta não herdam leituras experimentais.

SQLite e o envelope de evidência/exportação continuam no schema 1: a coluna existente `event_json` preserva o documento canônico. Cada evento conserva sua versão e a versão máxima do run passa a 7 quando necessário. Histórico, exportação e SSE mantêm o mesmo relatório, sem recalcular fórmulas ou atualizar timestamps retrospectivamente. O relatório v1/evento v6 do candidato anterior e os eventos v1–v5 permanecem legíveis; seus valores em milésimos não são reinterpretados como fixed8.

A API usa um snapshot publicado separado do snapshot de aquisição. O coletor pode consumir uma aquisição nova, mas o relatório numérico só é promovido para a API depois do commit SQLite; a publicação SSE ocorre em seguida. Isso impede que a API ofereça números que possam desaparecer por substituição do snapshot antes de serem gravados. Falha de persistência não promove um novo relatório numérico.

Retirar valores inválidos continua sendo imediato: desaparecimento, incompatibilidade, revogação e vencimento não esperam persistência. O estado adicional `publication_pending` retorna diagnóstico com valores, brutos e limites nulos enquanto não existe publicação confirmada segura, inclusive quando uma falha nova impede conservar números do relatório anterior. Diagnósticos nulos podem anteceder um evento gravado; a garantia de prévia persistência aplica-se às respostas numéricas.

O tempo de commit não substitui o tempo de aquisição. A validade soma a idade original no commit ao tempo monotônico decorrido desde a publicação, e a repetição da mesma aquisição conserva essa contagem. Assim, persistir novamente ou consultar a API não prolonga a validade dos números. Essa decisão de publicação está implementada e confirmada no pacote final.

## Evidência e aceite

`available` significa que a aquisição satisfez os gates e a validação do payload. A evidência histórica `matched_external_reference` pertence ao perfil e às fontes registradas. O limiar térmico de 0,051 °C deriva da equivalência de arredondamento entre observações das próprias chamadas do GPU-Z e seu log. Não é uma garantia universal para leitores assíncronos ou uma medida de acurácia física.

A comparação anterior continua com 19/20 pares GPU-Z fora desse critério; a análise offline não demonstrou que um offset, mudança de escala ou relaxamento da tolerância resolveria a divergência. O protocolo prospectivo local (`evidence/v011-correction-20260906/offline/prospective-protocol.md`, não distribuído) separa preservação de bruto/tempos/transporte, elegibilidade das referências e comparação numérica de uma janela completa predeterminada.

A segunda execução, preservada em `evidence/v011-cadence-20260906/`, confirmou 60 capturas elegíveis após corrigir o período entre inícios do worker. Hotspot/GPU-Z ainda violou 0,051 °C em 50/60 pares, diferença máxima de 0,6375 °C; HWiNFO/hotspot teve diferença máxima de 0,625 °C, apenas informativa. Tensão passou em 60/60 pares contra ambas as referências, diferença máxima de 0,00025 V, mantendo limites de 0,001 V para GPU-Z e 0,002 V para HWiNFO.

Os 90 eventos SSE coincidiram com histórico/exportação e os 131 registros exportados preservaram bytes, mas somente 86/90 respostas da API estavam no histórico. A reprovação de transporte dessa rodada motivou a promoção do snapshot somente após commit; schemas e conferência de bruto/tempos aprovados não substituem essa garantia. Os resultados anteriores permanecem válidos como registros de falha, sem seleção de outra janela ou ampliação de tolerância. [Validação completa](../research/2026-09-06-v011-correction-validation.md).

### Validação final da publicação — aprovada

O pacote final extraído passou em 30 segundos fixos: 269/269 respostas numéricas da API já constavam no SQLite quando consultadas, 30 eventos SSE coincidiram com histórico/exportação e 51 registros exportados preservaram os bytes armazenados. Schemas, bruto, fórmula e limites temporais passaram. Testes com Kestrel/SQLite reais verificaram aquisição mais rápida que o coletor público e falha de inserção; testes controlados verificaram estados nulos/`publication_pending`, reinício e conservação da idade original. O modo padrão permaneceu sem filhos experimentais. Identificação do ZIP e evidências estão na [validação completa](../research/2026-09-06-v011-correction-validation.md).

O smoke verificou transporte e publicação, sem repetir o ensaio térmico. Sua aprovação não converte as 50/60 violações térmicas em aprovação.

## Limites

Cobertura pública e canal experimental são contagens distintas. Cooler privado permanece `raw_unknown`; RPM público mantém o significado do contrato NVML. Nenhuma identidade desconhecida recebe aproximação e nenhuma configuração do operador amplia a allowlist. A instalação v0.6.0 é uma implantação separada; testes usam porta e banco isolados.
