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
10. o JSON Schema v2 é sintaticamente válido e declara `schema_version = 2`.

Execute:

```powershell
.\scripts\verify.ps1 -Configuration Release
```

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
