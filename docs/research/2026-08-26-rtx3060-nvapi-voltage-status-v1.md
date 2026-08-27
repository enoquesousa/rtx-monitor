# Correlação multipatamar do status privado de tensão da RTX 3060

Data da observação: 2026-08-26. Este resultado vale somente para o perfil fixado abaixo e permanece experimental.

## Perfil observado

| Componente | Identidade |
|---|---|
| GPU | NVIDIA GeForce RTX 3060, dispositivo `10de:2504`, subsistema `10de:1536` |
| VBIOS | `94.06.25.00.fc` |
| GPU-Z | 2.70.0, SHA-256 `6cb0ef29682452de81a9576808881685161411a1fad00938ba04131159979c29` |
| NVAPI | `nvapi_impl.dll`, SHA-256 `fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf` |
| Interface candidata | `0x465f9bcf`, RVA `0x00198010` |
| Call site do GPU-Z | RVA `0x0021cee7`, imediatamente após a chamada |

## ABI comprovada nesta passagem

A desmontagem delimitada do call site mostrou que o GPU-Z zera uma estrutura local de 76 bytes, grava `0x0001004c` no primeiro DWORD, passa dois argumentos e verifica retorno zero antes de consumir o conteúdo. A observação foi feita depois da chamada, sem invocar a interface, modificar o buffer ou reutilizar o helper de baixo nível do GPU-Z.

Durante uma janela de dez segundos, dez chamadas retornaram sucesso. As dez estruturas continham:

- palavra 0: `0x0001004c`, versão/tamanho v1 de 76 bytes;
- palavra 10, offset `0x28`: `0x000d2924`, ou 862.500 em decimal;
- todas as demais 17 palavras observadas: zero nessa placa e nesse estado.

## Primeira correlação externa em repouso

Na mesma janela, de 18:10:10 a 18:10:20 no horário local:

| Fonte | Campo | Valor observado |
|---|---|---:|
| Buffer privado já fornecido ao GPU-Z | palavra 10 / `0x28` | 862.500 |
| Log do GPU-Z | `GPU Voltage [V]` | 0,8620 V |
| Log do HWiNFO | `GPU Core Voltage [V]` | 0,863 V |
| GPU-Z | `Board Power Draw [W]` | 34,0–35,6 W |
| HWiNFO | `GPU Potência [W]` | aproximadamente 34 W |
| HWiNFO | `Tensões de linhas GPU (avg) [V]` | 12,043–12,044 V |

Interpretar a palavra 10 como microvolts produz `0,862500 V`, compatível com as duas referências externas e suas resoluções de exibição.

## Repetição sob carga variável

Uma segunda janela de dez segundos foi capturada enquanto o Render Test do GPU-Z fazia a potência oscilar. A oscilação foi preservada em vez de tratada como falha, pois produziu três patamares de tensão na mesma execução:

| Palavra 10 bruta | Interpretação em microvolts | GPU-Z na amostra correspondente |
|---:|---:|---:|
| `0x00107fa2` | 1.081.250 µV = 1,081250 V | 1,0810 V |
| `0x000d418e` | 868.750 µV = 0,868750 V | 0,8680 V |
| `0x000e4e1c` | 937.500 µV = 0,937500 V | 0,9370 V |

Nos sete retornos seguintes, a palavra permaneceu em 1.081.250 µV enquanto o GPU-Z mostrava 1,0810 V e a potência de placa variava entre 46,9 W e 110,9 W. Assim, a palavra não codifica potência: ela acompanha os degraus de tensão do núcleo, e a conversão por `raw / 1.000.000` reproduz as referências externas dentro da resolução de exibição.

O estágio de evidência permanece `matched_external_reference`, agora com escala e atualização confirmadas em vários patamares para o perfil exato. Isso ainda não transforma a interface privada em contrato público nem prova universalidade para outra placa, VBIOS ou versão de driver/GPU-Z.

## O que ainda não está provado

- Os campos zerados não foram nomeados. Eles podem representar rails ausentes, flags, políticas ou espaço reservado.
- A estrutura observada não contém evidência suficiente para atribuir potência de placa, slot PCIe, conector de 8 pinos, corrente ou tensão de entrada de 12 V.
- Potência total, energia acumulada e limites já são obtidos separadamente pela NVML pública; não devem ser reatribuídos a esta estrutura privada.
- Nada aqui autoriza escrita de tensão, power limit, clocks, I2C, MMIO ou uso do driver do GPU-Z.

## Próximo gate

O schema de observação `nvapi-voltage-status-v1-observation-v1` e o correlator offline `correlate-nvapi-voltage-status` preservam o valor bruto, a conversão, os hashes e a janela da referência. A fixture multipatamar é validada no CI sem acesso à GPU.

O próximo gate é repetir em uma sessão separada para testar reprodutibilidade antes de expor o valor por um provedor experimental.
