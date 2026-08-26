# Referência de sensores GPU-Z — RTX 3060 GA106

- Data da coleta: 2026-08-25
- Ferramenta declarada: GPU-Z 2.70.0
- GPU selecionada: NVIDIA GeForce RTX 3060
- Estado: importação offline concluída; o caminho térmico foi identificado depois para o perfil binário exato

## Proveniência do arquivo

| Propriedade | Valor |
|---|---|
| Nome original | `GPU-Z Sensor Log.txt` |
| Tamanho | 681.540 bytes |
| SHA-256 | `7b314b3fc898b7a8ad6fbb002dac673ce8062e12de9952e3e04135033ea768fa` |
| Codificação detectada | alternativa ISO-8859-1, compatível com o símbolo de grau do arquivo |
| Amostras | 1.107 em 3 sessões anexadas |
| Janela local | `2026-08-25 18:40:27` a `2026-08-25 19:20:46` |
| Cadência mediana | 1.000 ms |
| Canais | 26, sendo 24 da GPU/placa e 2 do host |

O arquivo foi tratado somente como dados. Nenhum conteúdo textual dele foi executado ou interpretado como instrução.

## Canais observados

| Grupo | Canais |
|---|---|
| Temperatura da GPU | `GPU Temperature`, `Hot Spot` |
| Clocks | `GPU Clock`, `Memory Clock` |
| Ventoinhas | percentuais e RPM de Fan 1 e Fan 2 |
| Uso e motores | memória usada, GPU, controlador de memória, vídeo e barramento |
| Potência | placa, chip, `PWR_SRC`, slot PCIe, conector de 8 pinos e percentual de TDP |
| Tensão | `PWR_SRC`, slot PCIe, conector de 8 pinos e GPU |
| Limitação | `PerfCap Reason`, gravado como código bruto `16` |
| Contexto do host | temperatura da CPU e memória do sistema usada |

Resumo das séries térmicas e elétricas mais relevantes nesta captura em repouso:

| Canal | Mínimo | Máximo | Média aproximada |
|---|---:|---:|---:|
| GPU Temperature | 32,8 °C | 34,3 °C | 33,32 °C |
| Hot Spot | 43,0 °C | 45,2 °C | 43,65 °C |
| Board Power Draw | 33,7 W | 55,5 W | 35,40 W |
| GPU Chip Power Draw | 12,2 W | 31,1 W | 13,96 W |
| PWR_SRC Power Draw | 22,0 W | 27,5 W | 22,85 W |
| PCIe Slot Power | 6,6 W | 13,4 W | 7,10 W |
| 8-Pin #1 Power | 27,1 W | 42,2 W | 28,31 W |

Na sessão mais longa, com 884 amostras, o hotspot teve correlação de `0,6332` com `GPU Temperature`, `0,4472` com consumo percentual e `0,4187` com carga da GPU. Isso descreve o comportamento térmico observado; não demonstra que qualquer um desses canais seja a origem física ou computacional do hotspot. A sessão curta de 30 amostras produziu rankings diferentes e foi mantida separada para não criar uma correlação artificial entre baselines.

## O que esta evidência prova

O GPU-Z conseguiu obter e registrar um canal chamado `Hot Spot`, além de decomposições de potência e tensão que a NVML pública não ofereceu no inventário anterior. A série de hotspot também é numericamente diferente da temperatura média do die.

Isso prova **observabilidade por software neste conjunto de GPU, driver e GPU-Z**. Ainda não prova:

- que o hotspot seja um termistor físico separado;
- que o valor esteja disponível em um registrador público;
- que a VBIOS contenha a leitura instantânea;
- que cada potência exibida seja medida por um sensor dedicado, em vez de agregada ou calculada pelo RM/PMU/GSP;
- que outro modelo ou outra versão de driver exponha os mesmos canais.

A presença de `CPU Temperature` e `System Memory Used` no mesmo log demonstra por que “aparece no GPU-Z” não significa “veio da placa”. O importador classifica esses dois canais como `host_system` e os demais como `gpu_board`, sem transformar a classificação de origem em uma afirmação sobre o mecanismo de leitura.

## Implementação resultante

`rtxmon-lab analyze-gpuz-log` emite [`gpuz-reference-analysis-v1.schema.json`](../schema/gpuz-reference-analysis-v1.schema.json) com:

- tamanho, nome, codificação e SHA-256 do arquivo;
- janela temporal e cadência mediana;
- catálogo ordenado de canais, unidade, escopo, categoria e representação;
- mínimo, máximo, média, desvio-padrão populacional e último valor numérico;
- todas as amostras brutas, sem perder a precisão textual original;
- índice de sessão para cada amostra e contagem de sessões anexadas;
- avisos explícitos sobre autoridade, fuso horário e códigos não interpretados.

Essa saída será o oráculo externo para comparar telemetria pública e futuras aquisições allowlisted. `correlate-gpuz-log --session INDEX` calcula cada sessão isoladamente. A investigação de runtime e os IDs NVAPI observados estão registrados em [Caminhos de runtime do GPU-Z](2026-08-25-gpuz-runtime-paths.md).

## Atualização após a análise de runtime

Uma captura posterior, ancorada em GPU-Z 2.70.0, driver 610.88 e hashes exatos dos módulos, demonstrou que `NvAPI_GPU_ThermChannelGetStatus` v2 entrega dois valores em ponto fixo 8. O canal 0 acompanhou `GPU Temperature` e o canal 1 acompanhou `Hot Spot`, ambos com erro máximo de `0,05` °C contra o prefixo correspondente deste log. A associação invertida divergiu em mais de 10 °C em média.

Isso identifica a origem computacional usada pelo GPU-Z nesse perfil; não transforma a interface em API pública, não prova a construção física do sensor e não autoriza generalização para outra combinação de placa, VBIOS, driver ou binário. O relatório completo e os limites estão em [Caminhos de runtime do GPU-Z](2026-08-25-gpuz-runtime-paths.md).
