# Protocolo térmico RM — pista para Hot Spot e sensores adicionais

- Data: 2026-08-25
- Fonte analisada: NVIDIA Open GPU Kernel Modules 610.57.04
- Driver Windows instalado: 610.88
- Estado: ABI offline implementado; transporte Windows não comprovado e não executado

## Resultado principal

O cabeçalho oficial `ctrl2080thermal.h` publica o comando RM `THERMAL_SYSTEM_EXECUTE_V2`. Diferentemente de `NvAPI_GPU_GetThermalSettings`, que expõe no máximo três slots públicos, esse protocolo executa uma lista de até 32 instruções e oferece operações explícitas para:

- contar alvos e sensores;
- consultar tipo do alvo;
- consultar tipo do provedor;
- relacionar um sensor ao provedor e ao alvo;
- consultar faixa mínima e máxima;
- ler o valor atual do sensor.

Os alvos publicados são `GPU`, `MEMORY`, `POWER_SUPPLY` e `BOARD`. O valor atual é um inteiro assinado; o código do driver converte a temperatura interna NVIDIA para graus Celsius arredondados antes de preencher a resposta.

## ABI observado

| Item | Valor |
|---|---:|
| API | versão 1, revisão 0 |
| Comando lógico | `0x20800513` |
| Comando físico non-privileged | `0x20808513` |
| Instrução | 44 bytes |
| Operand union | 32 bytes |
| Máximo por chamada | 32 instruções |
| Envelope completo | 1.432 bytes |
| Opcode — sensores disponíveis | `0x00000500` |
| Opcode — provedor do sensor | `0x00000510` |
| Opcode — alvo do sensor | `0x00000520` |
| Opcode — faixa | `0x00000540` |
| Opcode — leitura | `0x00001500` |

O cache do objeto GPU nessa versão aberta possui espaço para quatro sensores. Isso não prova que a RTX 3060 exponha quatro leituras, mas mostra uma superfície interna maior que o limite público `NVAPI_MAX_THERMAL_SENSORS_PER_GPU = 3`. É uma hipótese concreta para explicar por que o GPU-Z vê `Hot Spot` enquanto a consulta pública desta máquina retornou apenas um sensor GPU.

## Resultado da sondagem pública local

Na RTX 3060 `10de:2504/10de:1536@94.06.25.00.fc`, driver 610.88:

| Provedor | Estado | Canais |
|---|---|---:|
| `nvmlDeviceGetThermalSettings` | disponível | 1, alvo GPU |
| `nvmlDeviceGetFieldValues` para memória | não suportado | 0 |
| `NvAPI_GPU_GetThermalSettings` | disponível | 1, alvo GPU |

Portanto, o Hot Spot do GPU-Z não veio dos resultados públicos que o projeto já consulta. Ele pode vir de outra interface NVAPI, de RM/GSP ou de uma transformação interna do GPU-Z; o protocolo encontrado reduz o espaço de busca, mas ainda não identifica a chamada real.

## Implementação no projeto

[`rm_thermal_protocol.hpp`](../../cpp/include/rtxmon/lab/rm_thermal_protocol.hpp) e [`rm_thermal_protocol.cpp`](../../cpp/src/lab/rm_thermal_protocol.cpp) implementam apenas:

- layout byte-compatible com `static_assert` de tamanho e offsets;
- requests fechadas para contar sensores, obter relações e ler uma amostra;
- requests separadas para resolver tipo de provedor e alvo;
- validação de versão, quantidade, opcode, execução e status;
- limites fail-closed e conversão assinada explícita.

Não há transporte, handle de driver, IOCTL ou elevação. Os testes usam somente objetos sintéticos e não requerem GPU.

## Por que o transporte não foi ligado

O repositório aberto mostra `NV_ESC_RM_CONTROL` para Unix. Ele não publica uma implementação equivalente de user mode para WDDM. Além disso, o driver instalado é 610.88, enquanto o snapshot disponível é 610.57.04. Enviar um envelope construído a um IOCTL ou escape adivinhado seria justamente o tipo de chamada kernel especulativa que o laboratório proíbe.

Antes da primeira leitura precisamos:

1. obter a fonte/tag que corresponda ao driver ou demonstrar estabilidade do ABI entre as duas versões;
2. identificar, por fonte primária ou tracing controlado, a rota Windows que cria cliente/subdevice RM;
3. provar que o comando é aceito como somente leitura e non-privileged nesse perfil;
4. registrar identidade, request exata, status e bytes de resposta;
5. correlacionar cada índice com o log GPU-Z em múltiplos cenários.

## Fontes primárias

- [NVIDIA — `ctrl2080thermal.h`](https://github.com/NVIDIA/open-gpu-kernel-modules/blob/610.57.04/src/common/sdk/nvidia/inc/ctrl/ctrl2080/ctrl2080thermal.h)
- [NVIDIA — implementação de `THERMAL_SYSTEM_EXECUTE_V2`](https://github.com/NVIDIA/open-gpu-kernel-modules/blob/610.57.04/src/nvidia/src/kernel/gpu/subdevice/subdevice_ctrl_gpu_kernel.c)
- [NVIDIA — documentação térmica NVAPI](https://docs.nvidia.com/nvapi/group__gputhermal.html)
- [NVIDIA — interface NVAPI](https://github.com/NVIDIA/nvapi/blob/main/nvapi_interface.h)
