# ADR 0001: usar NVML como backend térmico

- Status: aceito
- Data: 2026-08-24

## Contexto

O monitor precisa obter a temperatura do chip de uma GPU NVIDIA RTX em baixo nível, com código nativo, baixa sobrecarga, operação somente leitura e um contrato consumível por C++, C# e outras linguagens.

## Decisão

Usar a NVIDIA Management Library (NVML) diretamente por sua API C. Carregar a biblioteca do driver em runtime, preferir `nvmlDeviceGetTemperatureV` e manter `nvmlDeviceGetTemperature` somente como fallback para drivers anteriores.

A API pública do projeto será uma ABI C própria e pequena. Nem o header completo do SDK nem `nvml.lib` serão necessários para compilar.

## Motivos

- A NVIDIA documenta NVML como interface C para monitoramento e como base do `nvidia-smi`.
- O enum `NVML_TEMPERATURE_GPU` é documentado como sensor do die.
- A DLL já acompanha o driver em instalações Windows suportadas.
- Carregamento dinâmico permite erros claros quando driver/API estão ausentes.
- A fronteira C é adequada para C++, P/Invoke e futuras FFI.
- A chamada é local e evita custo, parsing e dependência de subprocesso.

## Alternativas rejeitadas

### Executar `nvidia-smi`

Útil como referência independente, mas inadequado como backend: cria processo, serializa em texto, aumenta latência e dificulta um contrato forte de erros.

### Acessar registradores privados, MMIO ou SMBus

Não há contrato público e estável que permita interpretar esses valores como temperatura calibrada em todas as RTX. Essa opção exigiria driver em modo kernel, aumentaria o risco e ainda poderia produzir um valor menos confiável que o firmware oficial.

### NVAPI

Foi rejeitada como backend da leitura principal: NVML já expõe explicitamente o sensor do die, funciona com o driver instalado e permite uma evolução Linux. O [ADR 0002](0002-public-capability-discovery.md) adiciona NVAPI posteriormente como fonte opcional de inventário, sem substituir o backend NVML.

## Consequências

- A leitura tem a resolução e o tratamento definidos pelo driver NVIDIA.
- GPUs GeForce/RTX podem ter suporte NVML limitado para algumas métricas; o sensor térmico deve ser tratado como capability e pode retornar `not supported`.
- O código precisa manter uma tabela ABI mínima atualizada e testar o tamanho das estruturas.
- O fallback antigo deve permanecer visível na amostra para auditoria.
