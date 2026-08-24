# Validação

## Critérios de aceite

Uma entrega é considerada válida quando:

1. C e C++ compilam com `/W4 /WX` no MSVC;
2. C# compila com warnings tratados como erro;
3. os testes de tamanho/layout da ABI passam;
4. C, C++ e C# retornam uma temperatura plausível para o mesmo UUID;
5. o backend informado é a API NVML moderna ou o fallback explicitamente rotulado;
6. uma consulta independente do `nvidia-smi` fica dentro de 5 °C das leituras sequenciais;
7. os modos watch produzem a quantidade solicitada de amostras.

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
