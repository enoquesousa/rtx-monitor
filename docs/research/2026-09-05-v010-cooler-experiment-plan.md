# Próximo experimento de cooler — Galax RTX 3060 de 12 GB

Estado após a rodada de 2026-09-05: **plano privado interrompido e parcialmente executado; produto v0.10 concluído localmente**. A captura v3 foi implementada e dois preflights reais de 10 segundos foram válidos. A primeira janela privada de 30 segundos foi rejeitada por retorno `0xffffff9b` (−101), com causa não verificada; as seis janelas planejadas não foram concluídas e o buffer rejeitado não foi aproveitado. A análise dos preflights mantém `raw_unknown`. Os três ciclos públicos, CI, pacote e preservação final passaram separadamente. O [relatório de conclusão](2026-09-05-v010-completion.md) registra fatos, hashes e limites; a sequência abaixo preserva o plano original e suas etapas não cumpridas.

Este plano fica restrito à investigação dos campos privados e de suas hipóteses de percentual/PWM, ainda `raw_unknown`. A leitura pública tem semântica de velocidade pretendida em RPM; não comprova tacômetro físico, posição de fan nem decodifica o buffer privado. As exigências de um novo contrato de captura abaixo se aplicam a essa pesquisa privada, não são pré-requisito para consultar a API pública documentada.

## Evidência disponível

O escopo é somente a placa do proprietário: UUID `GPU-fca3647e-8390-15a8-f23b-d0f870c9accd`, PCI `10de:2504`, subsystem `10de:1536`, VBIOS `94.06.25.00.fc`, driver `610.88`. A observação passiva fixa GPU-Z 2.70.0 x86 e o módulo `nvapi_impl.dll` x86; não autoriza aquisição direta nem equivalência com o módulo x64 do monitor.

| Artefato local | Identificação |
| --- | --- |
| `evidence/v08-final-20260827-190125/cooler-passive-v2-profiled/nvapi-cooler-status-v1-observation-v2.json` | 388.089 bytes; SHA-256 `7b9751f49849756c22949423d53604174a85543dc9263eebd5d9c32d99789a4b` |
| GPU-Z fixado pela observação | SHA-256 `6cb0ef29682452de81a9576808881685161411a1fad00938ba04131159979c29` |
| `nvapi_impl.dll` fixado pela observação | SHA-256 `fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf` |
| Prefixo correspondente de `evidence/GPU-Z Sensor Log.txt` | 15.504.728 bytes; SHA-256 **calculado offline em 2026-09-05** `a9ae964a9627dcd290204337250963aa1d26b7eca3f7d4e8fe9feb04bd0b480d`; última linha completa `2026-08-27 19:41:50` |

A captura v2 ocorreu em 2026-08-27, 19:41:39–19:41:51, UTC−03:00. Preserva 36 retornos, 18 em cada call site, estrutura de 1.704 bytes/426 DWORDs e duas entradas. Somente as palavras globais 11 e 24 variaram. O log GPU-Z cresceu antes/meio/depois de 15.499.202 para 15.500.430 e 15.504.728 bytes; os últimos timestamps registrados foram 19:41:38, 19:41:44 e 19:41:50.

**O hash do prefixo foi calculado posteriormente e não constitui selo registrado durante a captura.** O contrato v2 também não possui timestamp individual por retorno. Esses arquivos permitem examinar faixas da janela; não sustentam pareamento temporal exato.

## Hipóteses e alternativas

Os offsets abaixo são relativos à base de cada entrada; `entry_index` representa a posição no buffer, não uma ventoinha física.

| Localização bruta | Valores na captura v2 | Hipótese a testar e ambiguidade |
| --- | --- | --- |
| Entrada 0, `+4` bytes; palavra global 11 | 1.094–1.104, considerando os dois call sites | Candidato a leitura compatível com RPM; unidade e associação física ainda desconhecidas. |
| Entrada 1, `+4` bytes; palavra global 24 | 1.094–1.111, considerando os dois call sites | Mesma hipótese; não prova correspondência com Fan 2. |
| `+8` e `+16` bytes, ambas as entradas | 30 e 30, constantes | Dois candidatos numericamente compatíveis com o percentual exibido. Podem representar leituras, alvos, limites ou configuração; não é possível distingui-los neste patamar nem afirmar PWM físico. |
| `+12` bytes, ambas as entradas | 100, constante | Significado desconhecido; a coincidência com uma escala percentual não prova unidade, limite ou controle. |
| Identificador bruto no início das entradas | 1 e 2 | Pode identificar uma entrada lógica; não prova ordem ou posição das ventoinhas físicas. |

Entre 19:41:38 e 19:41:50, as dez linhas presentes do GPU-Z mostram Fan 1 a 1.097–1.104 RPM, Fan 2 a 1.094–1.106 RPM e ambas a 30%. As faixas semelhantes deixam a associação direta e a associação invertida plausíveis. As duas outras capturas locais v1, de 17:44 e 19:02 no mesmo dia, também mostram aproximadamente 1.100 no campo `+4` e os demais campos constantes em `[30, 100, 30]`; não acrescentam um patamar alternativo.

## Requisitos definidos antes da captura privada

Os requisitos abaixo foram implementados no [contrato v3](../schema/nvapi-cooler-status-v1-observation-v3.schema.json), mantendo os gates v2. Sua implementação não equivale à conclusão dos ciclos privados:

- timestamp individual de cada retorno, sequência, call site, thread e informação da resolução e origem do relógio; registrar UTC e a relação com os timestamps locais das referências. Se o relógio individual não estiver disponível, não substituir por interpolação da sequência;
- prefixos completos e terminados em LF dos logs GPU-Z e HWiNFO, com tamanho e SHA-256 selados durante a captura, antes/meio/depois, além de crescimento e cobertura temporal comprovados;
- sessão e cabeçalho exatos de cada referência, codificação, canais selecionados e registros ausentes ou inválidos. O log GPU-Z existente contém sessões com layouts diferentes, que precisam permanecer separadas;
- preservação integral dos 426 DWORDs, identidade, hashes e prova do módulo carregado; limites de atraso, política de pareamento/reutilização de linhas e tolerâncias definidos antes de comparar os valores.

As referências esperadas são `Fan 1 Speed (RPM) [RPM]`, `Fan 2 Speed (RPM) [RPM]`, `Fan 1 Speed (%) [%]` e `Fan 2 Speed (%) [%]` no GPU-Z; `GPU Ventilador1 [RPM]`, `GPU Ventilador2 [RPM]`, `GPU Ventilador1 [%]` e `GPU Ventilador2 [%]` no HWiNFO deste ambiente. Cabeçalhos ausentes ou duplicados não devem ser resolvidos por aproximação. As duas aplicações são referências externas de software; sua concordância não comprova por si só a construção física do sensor ou PWM elétrico. Uma futura comparação também pode registrar a fonte pública NVML, com seus próprios timestamps e semântica de RPM pretendido, mantendo explícita a ausência de associação física entre índices.

## Sequência proposta para a pesquisa privada

1. Em sessão elevada autorizada, confirmar os gates do perfil e os logs correntes das duas referências. Marcar início e fim de cada janela, preservando o serviço instalado.
2. Registrar repouso; executar uma carga gráfica normal que possa ser encerrada imediatamente; registrar sua evolução natural. Não exigir potência constante nem ajustar ventoinhas, clocks, tensão, power limit ou firmware.
3. Encerrar a carga e registrar o resfriamento. Repetir o ciclo repouso/carga/resfriamento para procurar patamares distintos e comportamento repetível.
4. Em outra sessão da aplicação de referência, repetir as janelas e verificar se estrutura, identificadores e hipóteses continuam reconhecíveis.

Cada janela deve respeitar os limites existentes: **10–60 segundos, até 1.024 retornos e transcript de depuração de até 16 MiB**. O coletor exige PowerShell administrador; o token inicialmente inspecionado em 2026-09-05 não era elevado, e as tentativas posteriores usaram helper elevado autorizado. A elevação continuou sendo pré-requisito, sem alteração ou contorno desse gate. O contrato legado v2 não resolve sozinho as lacunas de sincronização e selagem acima.

## Decisão após o experimento

A análise deve separar os call sites, testar ambas as associações entrada/canal, comparar palavras vizinhas e conservar os valores brutos. Relatar atrasos, pares descartados/reutilizados, valores distintos, transições, erros por faixa e hipóteses alternativas, incluindo a diferença entre `+8` e `+16`. Investigar escala, sinal, largura e atualização somente com evidência sincronizada; não escolher retrospectivamente um atraso ou uma tolerância apenas para obter concordância.

**Manter `raw_unknown`** quando faltar referência corrente, selo, alinhamento suficiente, variação discriminante, repetição ou separação entre hipóteses concorrentes. Se as duas ventoinhas permanecerem indistinguíveis, preservar a ambiguidade da ordem física. Ausência de mudança dos percentuais não autoriza concluir seu significado nem controlar a ventoinha para forçar a distinção.

Uma eventual promoção deve ser individual por campo e restrita ao perfil e ao nível de evidência alcançado. O experimento pode terminar legitimamente sem promover nenhum campo; esta pesquisa privada não implementa provider ou publicação experimental na API. A integração da fonte pública de RPM é um incremento separado e deve ser validada por seu contrato documentado, sem aguardar a decodificação privada.

Referências de contrato e critérios: [observação cooler v2](../schema/nvapi-cooler-status-v1-observation-v2.schema.json), [aquisição por perfil fixo](../adr/0010-fixed-profile-private-nvapi-acquisition.md) e [v0.10 no roadmap](../ROADMAP.md#v0100--correlação-e-validação-de-candidatos).
