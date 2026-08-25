# Validação

## Critérios de aceite

Uma entrega é considerada válida quando:

1. C e C++ compilam com `/W4 /WX` no MSVC;
2. C# compila com warnings tratados como erro;
3. os testes de tamanho/layout da ABI passam;
4. C, C++ e C# retornam uma temperatura plausível para o mesmo UUID;
5. o backend informado é a API NVML moderna ou o fallback explicitamente rotulado;
6. uma consulta independente do `nvidia-smi` fica dentro de 5 °C das leituras sequenciais;
7. os modos watch produzem a quantidade solicitada de amostras;
8. C++ e C# retornam a mesma identidade PCI/VBIOS e o mesmo conjunto de capabilities;
9. o campo público de temperatura da memória sempre gera um registro, inclusive quando não suportado;
10. o JSON Schema de capabilities declara `schema_version = 2`;
11. o JSON Schema de eventos declara `schema_version = 1` e os três tipos de evento;
12. testes sem GPU reproduzem gap, backoff limitado, recuperação e mudança de índice para o mesmo UUID;
13. o buffer circular descarta somente os eventos mais antigos ao atingir sua capacidade;
14. C++ e C# selecionam o mesmo UUID e emitem envelopes equivalentes em um stream saudável.

Execute:

```powershell
.\scripts\verify.ps1 -Configuration Release
```

Para a verificação independente de hardware usada no CI:

```powershell
.\scripts\verify-ci.ps1 -Configuration Release
```

`verify-ci.ps1` compila com avisos como erros, executa os testes de ABI e dos samplers simulados, verifica a formatação C# e analisa os dois schemas. `verify.ps1` acrescenta as leituras reais da GPU e a comparação independente com `nvidia-smi`.

## Snapshot local inicial

Em 2026-08-24, a validação inicial foi executada em:

- GPU: NVIDIA GeForce RTX 3060;
- driver: 610.88;
- NVML: 13.610.88;
- biblioteca: `C:\Windows\System32\nvml.dll`;
- backend selecionado: `nvmlDeviceGetTemperatureV`;
- primeira leitura C/C++/C#: 48 °C nas três camadas;
- CMake/MSVC: build Release sem avisos;
- .NET: build Release sem avisos;
- CTest: teste de ABI aprovado.

A temperatura é dinâmica; o snapshot comprova o caminho de leitura, não fixa 48 °C como valor esperado.

## Snapshot do inventário público

Em 2026-08-24, após a ABI v2, o teste completo confirmou:

- perfil da placa: `10de:2504/10de:1536@94.06.25.00.fc`;
- PCI: `00000000:01:00.0`;
- NVML thermal settings: um alvo `gpu`, controlador `gpu_internal`, disponível;
- NVML field 82: alvo `memory`, explicitamente `not_supported` nessa RTX 3060;
- NVAPI thermal settings: um alvo `gpu`, controlador `gpu_internal`, disponível;
- C / C++ / C# / `nvidia-smi`: leituras entre 32 e 33 °C, com dispersão máxima de 1 °C na execução final registrada;
- C++ e C#: mesma identidade e os mesmos três registros de capability;
- build Release: zero avisos; CTest e verificação integrada aprovados.

Esses resultados descrevem esta combinação de placa, VBIOS e driver. Eles não demonstram que toda RTX 3060 — nem toda placa com o mesmo chip — publica o mesmo conjunto de sensores.

## Snapshot do monitoramento resiliente v0.3.0

Em 2026-08-24, a validação Release confirmou:

- ABI C preservada na versão 2;
- dois testes CTest aprovados: ABI e sampler C++;
- testes gerenciados do sampler C# aprovados sem acesso à GPU;
- perda, backoff, recuperação, mudança de índice e buffer limitado exercitados por simulação;
- C++ e C# serializaram `gap` para um UUID ausente com temperatura nula e backoff inicial de 250 ms;
- seleção do UUID `GPU-fca3647e-8390-15a8-f23b-d0f870c9accd` aceita também em letras minúsculas;
- C++ e C# emitiram dois eventos `sample` válidos e equivalentes;
- um ensaio contínuo adicional produziu 100 amostras e sequências contíguas em cada CLI, sem lacunas;
- C, C++, C# e `nvidia-smi` reportaram 33 °C na execução registrada;
- builds MSVC Release e Debug concluídos sem avisos;
- build adicional com Clang 22.1.8 e os dois testes CTest aprovados.

O UUID identifica apenas a GPU usada nesta validação e não é um valor esperado em outras máquinas.
