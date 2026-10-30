# ADR 0002: inventariar capacidades térmicas públicas antes de sondar hardware privado

- Status: aceito
- Data: 2026-08-24

## Contexto

Placas RTX podem conter sensores de die, memória, hotspot, VRM e placa, mas presença elétrica não implica que o driver publique todos eles. Nomes comerciais, índices de ferramenta e layouts de PCB não são um contrato suficiente para interpretar bytes de MMIO, I2C ou firmware como temperaturas calibradas.

O projeto precisa descobrir o que está observável, preservar resultados negativos e criar uma base de engenharia para estudos futuros sem expor escrita ou afirmar sensores inexistentes.

## Decisão

Criar uma ABI v2 de inventário somente leitura com três provedores públicos:

1. `nvmlDeviceGetThermalSettings` para descritores térmicos NVML;
2. `nvmlDeviceGetFieldValues` com `NVML_FI_DEV_MEMORY_TEMP` para o canal público de memória;
3. `NvAPI_GPU_GetThermalSettings` para a visão térmica pública NVAPI no Windows.

NVML e NVAPI serão correlacionadas pela identidade PCI completa, nunca pela posição em suas listas. O relatório conservará provedor, ID nativo específico da fonte, alvo, controlador, estado, código nativo, flags de validade e confiança. Identidade da placa incluirá PCI e versão VBIOS para formar uma chave de perfil reproduzível.

A fase pública não fará I2C/DDC, SMBus, MMIO, leitura de ROM, parsing de firmware nem chamadas de controle.

## Motivos

- As APIs públicas já revelam quais alvos o driver decidiu suportar.
- Um registro `not_supported` é evidência útil e evita que ausência seja confundida com 0 °C.
- Proveniência permite comparar APIs sem deduplicar sensores por suposição.
- A identidade PCI/VBIOS evita generalizar resultados entre placas com PCB ou firmware diferentes.
- A ABI fixa mantém C, C++ e C# equivalentes e verificáveis.

## Alternativas rejeitadas

### Tratar todo valor térmico como hotspot

O alvo publicado pela API deve ser respeitado. Temperatura `gpu` não prova temperatura de junção/hotspot, e um controlador interno não identifica sozinho a posição física do diodo.

### Enumerar endereços I2C ou mapear BARs nesta fase

Além do risco operacional, um byte que varia com carga não é prova de unidade, escala, offset, sinal, frequência de atualização ou localização física. A API I2C pública da NVAPI é voltada a DDC e não constitui um contrato para sensores internos da placa.

### Unificar NVML e NVAPI em um único sensor

As leituras podem coincidir, mas a equivalência física não é garantida. As duas observações permanecem separadas e comparáveis.

## Consequências

- A ABI passa de 1 para 2 e consumidores antigos falham de forma explícita na verificação de versão.
- NVAPI torna-se uma dependência opcional no Windows; NVML continua sendo o backend obrigatório da leitura principal.
- Um provedor pode estar `available` enquanto uma capability específica está `not_supported`.
- Hotspot, VRM ou memória só serão exibidos quando o alvo correspondente vier do driver ou quando um futuro modo experimental cumprir critérios próprios de validação e rotulagem.

## Referências oficiais

- [Estruturas de dispositivo NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceStructs.html)
- [Consultas de campos NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlFieldValueQueries.html)
- [IDs de campos NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlFieldValueEnums.html)
- [Grupo térmico NVAPI](https://docs.nvidia.com/nvapi/group__gputhermal.html)
- [Tabela oficial de interfaces NVAPI](https://github.com/NVIDIA/nvapi/blob/main/nvapi_interface.h)
