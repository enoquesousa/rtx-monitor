# v0.11 — correção do contrato bruto e validação do pacote

Continuação após a exigência de uma entrega estável e independente: [estabilização e diagnóstico de horários](2026-09-06-v011-stabilization.md). Os resultados abaixo pertencem às capturas anteriores e permanecem preservados.

Estado: **correções de software e pacote verificadas; aceite térmico pendente, v0.11 ainda não encerrada**. Produto 0.11.0, ABI 8. Este registro continua a [validação anterior](2026-09-05-v011-implementation-validation.md), preservando todos os ensaios que falharam. A instalação 0.6.0 permanece separada e saudável.

## Correções verificáveis

- O export aditivo `rtxmon_read_private_thermal_channels_v2` entrega os inteiros signed fixed8 realmente adquiridos, além dos milésimos legados. O export anterior e sua estrutura de 40 bytes permanecem; a nova estrutura possui 72 bytes. Não mudaram os canais, máscaras, offsets, módulos permitidos ou quantidade de chamadas ao driver.
- O serviço usa o payload térmico v2, cujo valor é `raw_value / 256`, sem reconstruir o bruto a partir de um decimal arredondado. A leitura preserva o início UTC e os limites monotônicos da aquisição do par térmico. Esses limites não afirmam simultaneidade entre os canais ou com GPU-Z/HWiNFO.
- Diagnóstico de perfil, abertura do monitor e encerramento nativo passaram para um filho supervisionado, com prazo total de dez segundos, saída limitada e encerramento confirmado. A falha encerra a sessão; não há reinício automático para buscar sucesso. O modo desativado não inicia esse diagnóstico nem filhos de sensores.
- Relatório experimental v2 e evento v7 distinguem o novo bruto. Os contratos anteriores v1/v6 permanecem para históricos; o evento padrão continua v5 e banco/evidência v1. A exportação mantém a representação JSON gravada, inclusive escapes antigos.
- O manifesto foi auditado para ABI 8 mantendo a revisão 2 do catálogo: identidade, pins e permissões do perfil não mudaram. A revisão não representa uma nova calibração física.
- A espera do worker passou a considerar o tempo de diagnóstico/coleta dentro do período configurado entre inícios. Ciclos lentos não provocam leituras em sequência para compensar atraso. Regressões do worker hospedado verificam duração variável, falhas, ausência de GPU e cancelamento com relógio controlado.

As regressões incluem layout/canário da ABI, vetores positivos e negativos, bruto inconsistente, prazo exatamente no limite, relógio inválido, identidade alterada/revogação, processo travado/cancelado, ausência de reinício e preservação de bruto/horário/proveniência na API, SSE, SQLite e exportação.

## Diagnóstico térmico e critério do ensaio

A análise offline independente reproduziu os quatro comparativos anteriores. Não encontrou erro de coluna, sessão, horário ou cálculo. O erro térmico assinado variou entre −2,381 e +0,669 °C; isso não sustenta um deslocamento fixo. As próprias referências divergiram até 2,2 °C durante mudanças rápidas de temperatura. A amostragem assíncrona é uma hipótese plausível, não uma causa comprovada para cada par.

A tolerância de 0,051 °C vem da equivalência de arredondamento observada entre retornos do próprio GPU-Z e seu log. O correlacionador a denomina `RoundingToleranceCelsius`. Essa evidência valida canal/conversão no perfil ancorado; não estabelece um limite universal entre leitores independentes sob qualquer carga. Uma repetição histórica também excedeu esse limiar. Fontes: [pesquisa térmica](2026-08-25-gpuz-runtime-paths.md), [validação histórica](../VALIDATION.md), `ThermChannelCorrelationV2.cs`.

O novo protocolo foi escrito antes da captura: uma janela fixa de preflight de 30 segundos e uma janela imediatamente seguinte de 60 segundos. Todas as amostras e os prefixos das referências são preservados; não se deslocam timestamps, ampliam tolerâncias ou selecionam subjanela favorável. O período final só conta para aceite se o preflight atender aos critérios previamente definidos. O usuário informou ter encerrado sua tarefa na GPU antes do ensaio.

## Evidência desta continuação

Diretório local: `evidence/v011-correction-20260906/`. Inclui diagnóstico independente, protocolo prospectivo, logs das verificações, integridade do pacote e capturas. As tentativas de CI interrompidas também são preservadas: a primeira identificou uma expectativa antiga de ABI 7 no teste público de fan; a segunda identificou finais de linha incompatíveis com `.editorconfig`. Ambos foram corrigidos antes da rodada final.

A verificação Linux desta continuação foi **não executada**: não havia toolchain disponível nem Docker Engine ativo. Não foram instaladas dependências ou iniciados ambientes para substituir esse resultado. O histórico Linux da v0.10 não comprova a ABI 8 atual.

## Primeiro pacote ABI 8: preservação aprovada, contagem e hotspot reprovados

O pacote `artifacts/v0.11.0-service-abi8-local.zip` foi extraído e teve todos os 41 arquivos de conteúdo, manifesto e CRC conferidos. No modo padrão, retornou saúde 0.11.0, cinco registros v5, campos experimentais desativados e nenhum filho experimental.

A janela fixa produziu 89 eventos SSE e 89 consultas à API. Todos os relatórios da API foram encontrados no histórico; todos os eventos SSE coincidiram com histórico/exportação. Os 133 registros exportados preservaram **os bytes do JSON armazenado**, e todos os relatórios/eventos/exportações passaram nos schemas. Bruto, fórmula e limites de aquisição térmica foram conferidos; SQLite retornou `integrity_check=ok`.

O preflight teve 25 capturas únicas válidas. Na medição de 60 segundos, houve 50 capturas únicas, abaixo das 58 exigidas pelo protocolo: a espera de um segundo vinha depois do diagnóstico, produzindo intervalos de aproximadamente 1,2 segundo. Isso motivou a correção concreta do agendamento e uma segunda execução identificada; não autoriza selecionar uma janela térmica favorável.

Nos 50 pares da medição, hotspot/GPU-Z teve 46 violações e diferença máxima de 0,60625 °C. A tensão ficou dentro dos limites GPU-Z/HWiNFO em 50/50 pares, com diferença máxima de 0,00025 V. Esse resultado numérico de tensão não elimina a insuficiência de contagem do ensaio. HWiNFO/hotspot teve diferença máxima de 0,80625 °C, sem tolerância de aceite estabelecida para essa referência.

O diagnóstico independente encontrou aquisições térmicas de 0–2 ms, erros de ambos os sinais e média assinada de apenas +0,005 °C contra GPU-Z. GPU-Z e HWiNFO também discordaram acima de 0,051 °C em 26/30 pares entre si, chegando a 0,6 °C. Não foi identificado um deslocamento numérico justificável. Fontes: `prospective-comparison.json`, `transport-validation.json`, `actual-schema-validation.json`, `offline/abi8-thermal-addendum.md`.

## Pacote com cadência corrigida

Evidências da segunda execução separadas em `evidence/v011-cadence-20260906/`. Seu protocolo foi escrito antes da captura, mantendo tolerâncias, janelas, contagem mínima, horários originais e regras de preservação. O motivo da nova execução foi verificar a correção concreta da cadência; a janela térmica anterior não foi substituída.

O preflight teve 30 capturas únicas e passou pelos critérios de prontidão. A medição teve **60 capturas únicas elegíveis**, com abrangência de 59,389 segundos para hotspot e 59,388 segundos para tensão; o maior intervalo entre capturas foi 1.014 ms. A insuficiência de contagem da primeira execução foi resolvida.

| Campo / referência | Pares da medição | Maior diferença absoluta | Violações / resultado |
| --- | ---: | ---: | --- |
| Hotspot / GPU-Z | 60 | 0,6375 °C | 50/60 acima de 0,051 °C |
| Hotspot / HWiNFO | 60 | 0,625 °C | Informativo; sem tolerância de aceite estabelecida |
| Tensão / GPU-Z | 60 | 0,00025 V | 0/60 acima de 0,001 V |
| Tensão / HWiNFO | 60 | 0,00025 V | 0/60 acima de 0,002 V |

Assim, a cadência passou e a tensão ficou dentro das tolerâncias de ambas as referências em toda a medição, mas **hotspot continuou reprovado**. A aprovação de contagem não equivale à aprovação térmica. Nenhum limiar foi ampliado e nenhum par foi removido.

A execução preservou 90 eventos SSE e 90 respostas da API. Todos os eventos SSE coincidiram com histórico e exportação; os 131 registros exportados preservaram os bytes do JSON armazenado. Os schemas passaram para os 90 relatórios experimentais, 90 respostas da API, 90 eventos SSE e 131 registros exportados; bruto, fórmula, tempos e `integrity_check=ok` também foram confirmados.

Porém, **somente 86/90 respostas da API foram encontradas no histórico**. Quatro respostas expuseram uma aquisição que ainda podia ser substituída no snapshot antes de o coletor público persisti-la. Por isso, `transport-validation.json` registra `passed=false`: preservação SSE/SQLite/exportação e concordância numérica não encerravam o requisito de publicação histórica da API. Fontes: `prospective-comparison.json`, `transport-validation.json` e `actual-schema-validation.json` dessa execução.

## Correção da publicação para a API

A implementação verificada separa o snapshot de aquisição do snapshot publicado. O coletor continua consumindo as aquisições mais recentes, mas valores numéricos ficam visíveis na API somente depois de o evento correspondente concluir seu commit SQLite. A sequência é persistir, registrar o relatório confirmado para a API e publicar o evento SSE. Uma resposta com valores disponíveis passa, assim, a se apoiar em um evento já gravado.

Os estados de indisponibilidade não aguardam esse commit para retirar números inválidos. Ausência de GPU, incompatibilidade, revogação ou vencimento continuam produzindo diagnósticos nulos imediatamente. O novo estado `publication_pending` também devolve valores, brutos e limites de aquisição nulos quando ainda não existe publicação confirmada ou quando uma nova falha impede reutilizar com segurança o relatório confirmado anterior. Esses diagnósticos podem anteceder sua persistência; não se promete um registro histórico para toda resposta sem valores.

O commit não renova a idade da leitura. A validade considera a idade original da aquisição no momento do commit mais o tempo monotônico decorrido desde então. Publicar novamente a mesma aquisição não reinicia essa contagem; `captured_at` e os limites de aquisição permanecem originais. Essa correção trata a lacuna API/histórico e não altera sensores, fórmulas, cadência ou tolerâncias térmicas.

## Validação final da publicação — aprovada no pacote

Evidências finais em `evidence/v011-final-package-20260906/`. A verificação completa Windows (`verify-ci.ps1 -Configuration Release`) passou: 45 CTest, 14 testes de auditoria, 37 verificações de PowerShell, todas as suítes .NET, 39 casos de processos Console, schemas e formatação. Versões nativa e gerenciada coincidiram em 0.11.0. O smoke público de hardware anterior desta continuação passou em `evidence/v011-correction-20260906/windows-physical-smoke.log`; a correção final de publicação não mudou o backend nativo. Linux atual continua não executado pelos pré-requisitos ausentes descritos acima.

Os testes integrados da publicação usam Kestrel e SQLite reais com aquisição mais rápida que o coletor público. Uma trigger rejeita de fato a inserção: a API não passa a servir o relatório cuja gravação falhou. Testes com relógio controlado verificam `publication_pending`, invalidação imediata, falha parcial, reinício sem cache, atraso de commit e repetição sem renovar a idade original. Esses cenários são sintéticos; a captura física abaixo não provocou falhas de identidade nem modificou o relógio.

Pacote local final: `artifacts/v0.11.0-service-abi8-publication-local.zip` (não distribuído), framework-dependent Windows x64/.NET 8. SHA-256: `3221eb89a8b026257f9ea76a644fbb32c2640525101ccb55c3ea15c83278ddd5`. Os 41 arquivos de conteúdo e o manifesto, totalizando 42 entradas, tiveram hashes, CRC e extração conferidos. Os binários testados vieram desse ZIP.

No modo padrão, o pacote extraído retornou saúde 0.11.0, cinco eventos v5, ambos os campos experimentais `disabled` e nenhum processo filho experimental. Na instância opt-in isolada, um teste de publicação com duração fixa de 30 segundos consultou a API com espera nominal de 100 ms entre consultas, independentemente do SSE:

| Verificação final | Resultado |
| --- | --- |
| Respostas numéricas da API já presentes no SQLite imediatamente após cada resposta | 269/269 |
| Relatórios numéricos distintos entre essas respostas | 31 |
| Eventos SSE idênticos ao histórico e à exportação | 30/30 |
| Registros da exportação com bytes do JSON armazenado preservados | 51/51 |
| Schemas atuais | 269 respostas API, 30 eventos SSE e 51 registros exportados aprovados |
| Bruto, fórmula e limites de aquisição | Aprovados |
| SQLite | `integrity_check=ok` |
| Serviço instalado | 0.6.0 saudável, mesmo PID 8860 e mesmo início |
| Encerramento | Nenhum processo temporário do serviço restante; GPU-Z/HWiNFO preservados |

Fontes: `package-integrity.json`, `package-default.json`, `publication-window.json`, `publication-validation.json`, `transport-validation.json`, `actual-schema-validation.json`, `preservation-after.json` e `windows-ci.log` no diretório final. A captura recebeu somente campos disponíveis; os diagnósticos nulos e a expiração foram validados nos testes controlados acima.

Esse teste final verifica publicação e transporte. **Não repetiu o ensaio térmico e não substitui suas 50/60 violações.** A entrega funcional e a tensão estão verificadas, mas o aceite original ainda exige hotspot dentro da tolerância documentada. Não há correção numérica sustentada pelos dados, nem autorização para mudar esse critério. Fechar o marco depende de evidência térmica que satisfaça o critério ou de revisão explícita do aceite; nenhum desses fatos foi presumido. Não houve instalação, commit, push, PR, tag ou publicação externa nesta continuação.
