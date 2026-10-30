# Captura e inventário VBIOS — RTX 3060 GA106

- Data: 2026-08-25
- Origem declarada: `GPU-Z 2.70.0`, ação local **Save to file**
- GPU: NVIDIA GeForce RTX 3060 (`10de:2504`, subsystem `10de:1536`)
- VBIOS publicada pelo driver: `94.06.25.00.fc`
- Estado: captura, empacotamento e análise offline concluídos; nenhuma tabela interna foi usada para acessar hardware

## Proveniência

O executável instalado do GPU-Z e o instalador baixado possuem o mesmo SHA-256, assinatura Authenticode `Valid` de `TechPowerUp LLC` e o hash publicado na página oficial do fornecedor.

| Evidência | Valor |
|---|---|
| Arquivo original | `evidence/rtx3060-94.06.25.00.fc-gpuz.rom` |
| Tamanho | 2.048.000 bytes |
| SHA-256 da ROM | `3f19f03c0d5b71e44dad4333e16bde730dfba94213eff9074e8f9a518f05fe9c` |
| SHA-256 do manifesto externo | `90b155ad7afbb9a82fb5c8f39fa9177e834479e8e7338a7c85e1986f42a13508` |
| Streams NTFS | somente `:$DATA` |
| Reparse point | não |
| Resultado `rtxmon-lab verify` | `verified` |

A ROM e o pacote permanecem ignorados pelo Git. Este documento registra apenas hashes, offsets e metadados derivados; não redistribui firmware.

## Contêiner PCI ROM

`rtxmon-vbios` encontrou o primeiro contêiner válido no offset de arquivo `0x9200` (37.376), alinhado a 512 bytes. A cadeia validada contém:

| Imagem | Offset no arquivo | Tipo PCIR | Tamanho | Indicador |
|---|---:|---:|---:|---:|
| Legacy | `0x9200` | `0x00` | 65.024 bytes | `0x00`, há continuação |
| UEFI | `0x19000` | `0x03` | 92.672 bytes | `0x80`, última imagem |

O arquivo também contém dados anteriores ao contêiner e dados posteriores à cadeia PCIR. Esse layout é previsto pela especificação NVIDIA: a VBIOS pode conter dados para consumo do hardware antes do PCI Expansion ROM e tabelas apontadas além da imagem legacy.

## BIOS Information Table

Resultado do parser:

- status `success`, sem diagnósticos;
- BIT `0x0100`, header de 12 bytes e checksum válido;
- tokens de 6 bytes;
- 18 tokens;
- `BIT_BIOSDATA` reconstrói `94.06.25.00.fc`, igual à versão publicada pelo driver.

Tokens observados e documentados:

| ID | Versão | Tamanho | Significado documentado |
|---|---:|---:|---|
| `2` | 1 | 4 | ponteiros de scripts I2C |
| `B` | 2 | 37 | dados da BIOS |
| `C` | 2 | 44 | clocks |
| `D` | 1 | 4 | DFP/painel |
| `I` | 1 | 36 | tabelas de inicialização |
| `M` | 2 | 41 | memória |
| `N` | 0 | 0 | NOP |
| `P` | 2 | 232 | desempenho, energia, térmica, ventoinha e tensão |
| `S` | 2 | 24 | strings |
| `T` | 1 | 2 | TMDS |
| `U` | 1 | 5 | display |
| `V` | 1 | 6 | campos virtuais |
| `d` | 1 | 2 | DisplayPort |
| `p` | 2 | 4 | Falcon/PMU |
| `u` | 1 | 17 | UEFI |
| `x` | 1 | 8 | MXM |

Os tokens `E` e `i` e os campos adicionais ao final de `BIT_PERF_PTRS` não receberam significado: a especificação pública consultada não os define. Eles permanecem opacos.

## Superfície térmica e elétrica encontrada

O token `P`, versão 2, contém ponteiros não nulos para as seguintes tabelas documentadas:

- `Thermal Device Table`;
- `Power Sensors Table`;
- `Power Policy Table`;
- `Power Topology Table`;
- `Thermal Channel`, `Adjustment`, `Policy` e `Monitor`;
- `Fan Cooler`, `Fan Policy` e `Fan Test`;
- `Voltage Rail`, `Voltage Device` e `Voltage Policy`;
- performance, memória, virtual P-state, leakage, overclocking e low-power.

Aplicando ao primeiro cabeçalho de cada tabela a convenção recorrente `version/header-size/entry-size/entry-count`, os bytes sugerem:

| Tabela | Versão | Entradas sugeridas |
|---|---:|---:|
| Thermal Device | `0x10` | 18 |
| Power Sensors | `0x20` | 7 |
| Power Topology | `0x20` | 32 |
| Thermal Channel | `0x10` | 32 |
| Thermal Adjustment | `0x10` | 2 |
| Thermal Policy | `0x10` | 5 |
| Thermal Monitor | `0x10` | 6 |
| Fan Cooler | `0x10` | 2 |
| Fan Policy | `0x20` | 8 |
| Fan Test | `0x10` | 2 |
| Voltage Rail | `0x20` | 1 |
| Voltage Device | `0x10` | 4 |
| Voltage Policy | `0x10` | 1 |

Essas contagens são hipóteses estruturais, não nomes de sensores confirmados. A especificação pública descreve os ponteiros do token `P`, mas não publica o layout dessas tabelas para esta geração. O snapshot `open-gpu-kernel-modules-610.57.04`, conferido byte a byte contra a tag NVIDIA, não expõe parsers textuais dessas tabelas no código aberto pesquisado.

O token `2` possui os dois ponteiros em zero: não há `I2C Scripts` nem `External Hardware Monitor Init` por esse caminho. Isso não prova ausência de dispositivos I2C; a DCB possui uma trilha separada para portas e `I2C Devices Table`.

## Interpretação

A captura confirma que a placa descreve mais entidades térmicas e elétricas do que a NVML publica neste perfil. A VBIOS fornece configuração e topologia, não as leituras instantâneas. Para transformar uma entrada em `hotspot`, `VRM`, memória ou rail real ainda precisamos:

1. decodificar offline as tabelas e seus tipos de entrada;
2. correlacionar cada entrada com DCB, RM/GSP e documentação pública;
3. localizar a interface de leitura usada pelo driver;
4. provar variação e unidade com cargas controladas;
5. somente então criar uma leitura autorizada, sem varredura nem escritas.

## Fontes

- [NVIDIA — BIOS Information Table](https://nvidia.github.io/open-gpu-doc/BIOS-Information-Table/BIOS-Information-Table.html)
- [NVIDIA — Device Control Block 4.x](https://nvidia.github.io/open-gpu-doc/DCB/DCB-4.x-Specification.html)
- [NVIDIA — open-gpu-kernel-modules 610.57.04](https://github.com/NVIDIA/open-gpu-kernel-modules/releases/tag/610.57.04)
- [NVIDIA Support — extração da ROM com GPU-Z](https://nvidia.custhelp.com/app/answers/detail/a_id/4188/~/extracting-the-geforce-video-bios-rom-file)
