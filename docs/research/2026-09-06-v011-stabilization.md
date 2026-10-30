# v0.11 — estabilização com aquisição independente

Estado: **em estabilização; entrega final ainda não aprovada**. O proprietário substituiu o aceite de uma entrega experimental pela exigência de verificar e corrigir as leituras. Autorizou qualquer fonte direta, inclusive interfaces privadas, desde que GPU-Z, HWiNFO ou outro programa não sirvam como ponte do produto. Esses programas podem ser usados apenas para pesquisa e validação.

## Independência do produto

O serviço e o worker próprio consultam diretamente a implementação NVIDIA fixada pelo perfil. O caminho térmico usa a mesma implementação nativa para o export legado e o export ABI 8 que preserva fixed8. Os scripts de observação do GPU-Z ficam no laboratório e não alimentam o serviço, a API ou o histórico. As referências da evidência no JSON são metadados, não entradas de aquisição.

A consulta pública atual `nvidia-smi -q -d VOLTAGE,TEMPERATURE` informou temperatura da GPU, mas não trouxe hotspot ou tensão nessa consulta. A documentação pública consultada distingue a temperatura do die e os alvos térmicos; não estabelece nesses contratos a equivalência com o hotspot privado utilizado neste perfil. Fontes: [sensores NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceEnumvs.html), [alvos térmicos NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceStructs.html), [consultas NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceQueries.html) e [API térmica NVAPI](https://docs.nvidia.com/nvapi/group__gputhermal.html). Isso delimita a consulta, sem afirmar que toda fonte futura esteja esgotada.

## Correção da instrumentação

A observação térmica v2 não tinha horário individual por retorno. Seu correlacionador histórico usa `bounded_session_order_linear_resampling`; esse resultado não comprova sincronização entre leitores independentes.

O coletor recebeu a opção `-RecordReturnClocks`. Ela observa somente o ponto pós-chamada já permitido, acrescentando `.time -h 0` por retorno. A observação v3 registra UTC, precisão realmente impressa, uptime do sistema e associação exata ao buffer de 42 DWORDs já validado. Regressão de relógio, ausência/duplicação de horário, retorno fora da supervisão, sequência ou bruto divergente causam rejeição. O instante é o da parada pós-chamada do depurador; não representa início da aquisição nem simultaneidade entre programas.

O [schema v3](../schema/nvapi-therm-channel-v2-observation-v3.schema.json) exige esses campos e aceita um prefixo de referência completo de até 64 MiB. O v2 preserva seu limite de 16 MiB e seu contrato original. O aumento limita apenas o tamanho do arquivo de referência, hoje com cerca de 39 MB; não altera duração, taxa de leitura, campos, offsets, chamadas ou tolerância térmica. O [sidecar de relógios](../schema/thermal-return-clocks-v1.schema.json) ancora observação e transcript por SHA-256.

## Tentativas preservadas

Evidências locais em `evidence/v011-stabilization-20260906/`:

- Tentativa 1: falha antes da criação dos artefatos de observação; o wrapper antigo não capturou stderr nativo. A revisão encontrou uma checagem residual de 16 MiB no preflight e corrigiu também a captura de stderr.
- Tentativa 2: adquiriu retornos brutos e relógios, mas o relatório foi rejeitado pelo limite de 16 MiB do schema v2. Transcript, prefixo e falha permanecem preservados. Não foi promovida a captura aceita.
- Tentativa 3: executada após criar e testar o formato v3, em nova sessão dos processos após a pausa. Captura de dez segundos aceita, com 18 retornos, nove por canal, hashes e schemas verificados. Desconexão do depurador confirmada; nenhum depurador permaneceu anexado.

Uma falha inicial de CI detectou a expectativa antiga de limite no teste estático. O teste passou a exigir exatamente `64MB` somente com relógios v3 e `16MB` no modo anterior; não foi removida a verificação de limite. Passaram 29 verificações sintéticas novas e a verificação completa Windows: 45 CTest, 14 testes de auditoria, suítes .NET, schemas e formatação. Os testes não atribuem a dados sintéticos uma validação física.

## Diagnóstico com horários reais

O protocolo foi escrito antes da captura. Um processo próprio executou 200 leituras térmicas diretas com intervalo configurado de 100 ms, usando o supervisor e o backend existentes. A observação passiva da referência ocorreu durante essa coleta. Todas as amostras foram preservadas, inclusive as que ficaram fora da janela de referência. Não houve deslocamento de horário, ajuste de valor, suavização ou escolha de subjanela.

Cada retorno da referência foi comparado à leitura independente de horário mais próximo; empates usam o maior erro, e separações superiores a 250 ms seriam sinalizadas. O resultado é diagnóstico e não substitui o aceite original:

| Canal | Pares | Maior separação de horários | Maior diferença | Pares acima de 0,051 °C |
| --- | ---: | ---: | ---: | ---: |
| Die | 9 | 57 ms | 0,0625 °C | 2 |
| Hotspot | 9 | 43 ms | 0,37525 °C | 5 |

Em uma análise posterior, os 18 retornos da referência foram associados à única linha do próprio log no segundo local que contém o horário observado. Os 18 ficaram dentro de 0,051 °C, sem procurar deslocamento ou valor favorável. Essa verificação confirma a equivalência de arredondamento na referência nesta captura. **Não aprova a aquisição independente por transferência nem prova que toda divergência entre leitores seja causada apenas pelo tempo.**

Fontes: `timing-diagnosis-protocol.md`, `timing-diagnosis-launch.json`, `timing-diagnosis.json`, `reference-own-rounding.json`, `independent-thermal-watch.jsonl` e `reference-clock-attempt-3/`. A leitura independente do Console usa milésimos legados; os limites desse arredondamento permanecem distintos do fixed8 registrado na referência.

## Pendência objetiva

O código preserva a leitura direta e as conversões verificadas, mas não foi encontrada uma correção numérica sustentada que elimine a divergência. O limiar de 0,051 °C corresponde ao arredondamento de uma mesma leitura; os novos horários comprovam que os pares independentes representam aquisições em momentos diferentes. Isso limita a interpretação do comparativo, sem apagar suas reprovações.

A v0.11 não foi renomeada nem declarada estável para contornar esse resultado. O fechamento requer uma validação aplicável às aquisições independentes que demonstre o requisito de concordância, ou uma revisão explícita desse requisito. A orientação de entregar um produto independente está registrada e respeitada; ela não foi tratada como dispensa automática do aceite térmico.

O ZIP anterior continua candidato experimental, sem instalação ou publicação. A pausa iniciou outra sessão dos processos: nesta continuação a referência de preservação é o serviço 0.6.0/PID 9144/início `1788697783846`, GPU-Z 24380 e HWiNFO 25136. O registro anterior de PID 8860 pertence à sessão anterior, não deve ser apresentado como estado atual.

## Critério para o provedor estável — autorizado em 2026-09-06

Esta proposta corrige a distinção entre fidelidade ao retorno do driver e comparação entre aquisições diferentes. O usuário autorizou sua aplicação com “autorizo, pode seguir”, após a apresentação desta proposta. Essa autorização substitui o critério de concordância assíncrona pelo critério de fidelidade à fonte abaixo:

| Critério proposto | Comprovação exigida |
| --- | --- |
| Fonte e significado no perfil exato | Mesmo canal/estrutura do driver observado na referência; arredondamento da mesma aquisição dentro de 0,051 °C, sem interpolação de retornos |
| Fidelidade do produto | Bruto realmente retornado pelo driver, conversão exata `raw / 256`, preservação integral em API, SSE, banco e exportação |
| Independência | Produto consulta somente seu próprio backend e o driver; executáveis, logs e IPC de referências não fornecem os valores |
| Comportamento estável | Testes de falha, identidade, revogação, timeout, reinício e persistência; ensaio isolado de continuidade em cadência declarada, com todas as falhas preservadas |
| Limite de suporte | Somente GPU/VBIOS/driver/módulo do perfil validado; qualquer desvio bloqueia leituras, sem aproximação |

O comparativo entre aquisições independentes em horários diferentes permanece publicado como diagnóstico, com sua diferença e separação temporal; não recebe o rótulo de erro de arredondamento da mesma aquisição. Isso não introduz um offset, não aumenta 0,051 °C e não promete acurácia física que os dados não demonstram. A integração e o ensaio final do canal promovido são registrados separadamente em [validação estável](2026-09-06-v011-stable-validation.md). A aprovação deste critério não transforma os comparativos anteriores em resultados aprovados.
