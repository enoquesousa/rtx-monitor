# RPM pela API pública NVML — Galax RTX 3060 de 12 GB

Estado em 2026-09-05: **v0.10.0 concluída localmente, com RPM público integrado e cobertura observada de 32/37 campos disponíveis**. A consulta pública `nvmlDeviceGetFanSpeedRPM` funcionou nos dois índices de fan desta unidade. O ensaio inicial e as 24 leituras posteriores no produto permanecem documentados abaixo. CI Windows/Linux, três ciclos públicos, pacote, execução dos binários empacotados e preservação final passaram. A pesquisa privada está interrompida/parcial e permanece `raw_unknown`; o marco de software foi concluído mantendo explícitos esses limites.

## Contrato e significado

A [documentação oficial NVIDIA de consultas NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceQueries.html) descreve a saída como velocidade de operação **pretendida em RPM**. A leitura não comprova rotação física de um fan bloqueado; não deve ser apresentada como tacômetro físico validado ou PWM elétrico.

A assinatura pública é:

```c
nvmlReturn_t nvmlDeviceGetFanSpeedRPM(nvmlDevice_t device, nvmlFanSpeedInfo_t *fanSpeed);
```

O [header mantido pela NVIDIA](https://raw.githubusercontent.com/NVIDIA/go-nvml/main/pkg/nvml/nvml.h), consultado em 2026-09-05, define `nvmlFanSpeedInfo_t` como alias de `nvmlFanSpeedInfo_v1_t`. Seu layout possui três inteiros `unsigned int`, nesta ordem:

| Campo | Papel |
| --- | --- |
| `version` | Versão de estrutura fornecida pelo chamador |
| `fan` | Índice NVML consultado |
| `speed` | Saída em RPM quando a consulta retorna sucesso |

No layout verificado, a estrutura tem 12 bytes; a macro oficial `nvmlFanSpeedInfo_v1` combina esse tamanho com a versão 1 deslocada em 24 bits, resultando em `0x0100000c`. O ensaio confirmou esse tamanho antes da chamada. O contrato prevê recusa por versão incompatível e por recurso não suportado; nenhuma dessas condições deve produzir RPM estimado ou valor zero fictício. [Estrutura oficial](https://docs.nvidia.com/deploy/nvml-api/structnvmlFanSpeedInfo__v1__t.html).

## Evidência do ensaio

O ensaio leu somente APIs públicas, selecionou a placa pelo UUID e confirmou nome, VBIOS, driver e inventário de dois fans antes das consultas. A biblioteca foi carregada pelo caminho absoluto `C:\Windows\System32\nvml.dll`, após conferência de hash. Não houve ajuste de fan, clocks, tensão, power limit ou firmware.

| Identidade / artefato | Valor |
| --- | --- |
| Unidade alvo | Galax RTX 3060 12 GB do proprietário; nome NVML `NVIDIA GeForce RTX 3060` |
| UUID | `GPU-fca3647e-8390-15a8-f23b-d0f870c9accd` |
| VBIOS / driver | `94.06.25.00.fc` / `610.88` |
| Biblioteca | NVIDIA Management Library 610.88; versão de arquivo `8.17.16.1088`; assinatura registrada como `Valid` |
| SHA-256 da biblioteca | `029885fa79dcae6c6dd0a5cf41bf4300464834e864c985d44dc07538fb06670e` |
| Resultado local | `evidence/v010-offline-20260905/public-fan-rpm-probe.json` |
| SHA-256 do resultado | `2401d707d19573bf9ffbe0729b858fcf9d5c252ce926f62252e8e25099ecda6f` |
| Script do ensaio | `evidence/v010-offline-20260905/probe-public-fan-rpm.py` |
| SHA-256 do script | `b8e46262411a36eeaa5369bea06f6910eabc388c10460596dbf1a1e6fe750e79` |

Foram realizadas três rodadas, com aproximadamente 500 ms entre rodadas. O timestamp UTC foi registrado pelo processo imediatamente antes de cada consulta; não representa um timestamp interno de amostragem do sensor.

| Rodada | Índice NVML | Timestamp UTC em 2026-09-05 | RPM pretendido | Status |
| --- | ---: | --- | ---: | --- |
| 1 | 0 | `22:54:55.572663+00:00` | 1.097 | `0 / Success` |
| 1 | 1 | `22:54:55.573283+00:00` | 1.090 | `0 / Success` |
| 2 | 0 | `22:54:56.073708+00:00` | 1.097 | `0 / Success` |
| 2 | 1 | `22:54:56.073963+00:00` | 1.091 | `0 / Success` |
| 3 | 0 | `22:54:56.574451+00:00` | 1.095 | `0 / Success` |
| 3 | 1 | `22:54:56.574687+00:00` | 1.100 | `0 / Success` |

São **6/6 consultas com sucesso**: índice 0 entre 1.095–1.097 RPM e índice 1 entre 1.090–1.100 RPM. O encerramento NVML também retornou zero. O artefato registra `semantics=intended_operating_speed_rpm` e `physical_tachometer_verified=false`.

## Integração ao produto

O novo campo `fan_speed_intended_rpm` usa unidade `rpm`, provider `NVML nvmlDeviceGetFanSpeedRPM` e `provider_native_id` igual ao índice NVML. O export é opcional: ausência, recurso não suportado e falha de consulta continuam explícitos, com valor nulo. Zero é publicado somente quando a chamada tem sucesso e devolve zero. A enumeração de fans e a capacidade do relatório são verificadas antes das consultas; uma contagem inválida não provoca chamadas adicionais ou truncamento silencioso.

O layout nativo e a ABI **7** permanecem iguais. O JSON standalone C++/C# usa o [schema público v3](../schema/public-telemetry-v3.schema.json); eventos usam o [schema v5](../schema/telemetry-event-v5.schema.json). O histórico continua lendo e exportando eventos v1–v4. O endpoint HTTP de telemetria mantém `schema_version=2`, cujo contrato já admite campos e unidades adicionais. A integração inicial abaixo usou versão **0.9.0**; a entrega local declara **0.10.0**.

O manifesto de auditoria do perfil teve apenas os hashes das duas fontes compartilhadas `native/include/rtxmon/rtxmon.h` e `native/src/rtxmon.c` atualizados, além do digest agregado dessas fontes e da nota de revisão. A mudança nesses arquivos adiciona enums, nomes e asserts do contrato público. A revisão 2 do perfil privado compilado, seu snapshot, operações permitidas, pins de módulos e limites permanecem inalterados. Os hashes anteriores e atuais estão em `evidence/v010-offline-20260905/private-audit-source-refresh.json`; a auditoria completa passou.

## Validação inicial do binário e do serviço

- `scripts/verify-ci.ps1 -Configuration Release`: **aprovado**, incluindo 33 testes CTest, auditoria do perfil, suites Managed/Storage/Console/Service/Lab, schemas e formatação. Os testes cobrem dois índices, zero válido, export ausente, erros NVML, resposta com versão/índice inválido, contagens inválidas e capacidade. Também verificam RPM em JSON, histórico, HTTP e SSE.
- `scripts/verify.ps1 -Configuration Release -SkipBuild`: **aprovado na placa real**, com C/C++/C#/nvidia-smi, streams, alertas, SQLite, exportação e serviço HTTP temporário em `127.0.0.1:12998`. A cobertura pública observada foi **32/37**.
- Duas sessões adicionais do executável C# recém-compilado produziram **12 relatórios e 24 leituras RPM válidas**, sempre com a identidade esperada, os dois índices e cobertura 32/37. Os hashes dos binários usados estão no manifesto da captura.

As referências GPU-Z e HWiNFO cresceram antes, no meio e depois de cada sessão. Foram registrados tamanhos e SHA-256 dos prefixos completos durante a captura e preservados os cabeçalhos e as últimas 128 linhas de cada referência. O pareamento foi definido previamente como o timestamp mais próximo, com distância máxima de **1.500 ms** e reutilização de linhas permitida.

| Sessão | Relatórios / leituras RPM | Maior distância temporal GPU-Z / HWiNFO | Maior diferença absoluta GPU-Z / HWiNFO |
| --- | ---: | ---: | ---: |
| 1 | 6 / 12 | 500 / 890 ms | 11 / 12 RPM |
| 2 | 6 / 12 | 426 / 763 ms | 11 / 6 RPM |

São diferenças observadas entre consultas assíncronas nesta janela, sem estabelecer acurácia física ou comportamento sob carga. Os canais de software numerados foram comparados aos índices NVML correspondentes; isso não comprova posição física, lado da placa ou associação com entradas do buffer privado. A captura privada histórica de agosto permanece uma análise separada.

| Artefato local | SHA-256 |
| --- | --- |
| `evidence/v010-offline-20260905/public-rpm-windows-ci.log` | `b6766065d1a47ff5659192a1a749735d29701948a74c6535df91bc8b8f016c29` |
| `evidence/v010-offline-20260905/public-rpm-smoke.log` | `57a8d5276bd9b6b28cd27afebb95a0fb4c36a66322b1e8ca7ac9d70b71b5bf5f` |
| `evidence/v010-offline-20260905/product-fan-rpm/capture-manifest.json` | `3f5ebd460d08ba9a227d14d71e5f82e0acfe6d34cf09224680e162b62efbec8b` |
| `evidence/v010-offline-20260905/product-fan-rpm/summary.json` | `66e3c92513dbfd5fecd0b701235df747f515a6bf5a2812bca589e15b4ed569a6` |

O serviço instalado em `127.0.0.1:5136` permaneceu saudável, pronto, na versão **0.6.0**, com o mesmo início `1788604360279` e PID 8860. GPU-Z e HWiNFO permaneceram ativos. A validação usou binários novos e um serviço temporário, sem substituir a instalação existente.

O manifesto local `evidence/v010-offline-20260905/validated-source-manifest.json` registra os hashes dos 33 arquivos de código, testes, schemas e auditoria daquele incremento, a base `7c984d321667efccd6efb113402a191cf1fe04c3` e a branch `codex/v010-cooler-correlation`. Ele preserva a validação inicial e não representa o estado final do candidato 0.10.0; a PR #11 corresponde à etapa anterior, v0.9.

Na etapa inicial acima, a CI Linux ainda não havia sido executada. A rodada posterior do candidato 0.10.0 validou a implementação em um snapshot identificado, conforme a seção seguinte; o resultado anterior da v0.9 continua histórico.

## v0.10.0: validação final

As rodadas confirmadas passaram **33 CTest no Windows**, **29 CTest no Linux**, **14 testes de auditoria por plataforma** e as suítes .NET aplicáveis. Windows inclui a suíte Service; Linux verifica Managed/Storage/Console/Lab sem GPU. Os logs estão em `evidence/v010-completion-20260905/windows-ci-final.log` e `linux/ci.log`. `linux/result.json` e `linux/source-manifest.json` delimitam o snapshot de árvore SHA-256 `81b486f218576e5995e8fba705a1013d05b86b99c9d2d737f6d6045aff761e23`. Alterações posteriores nos textos de ajuda `--events`, PowerShell e documentação não integram esse snapshot; a conferência Windows final validou essas correções e o pacote final passou na execução real. Não se afirma execução física Linux nem identidade do snapshot com todo o checkout final.

O piloto público 4, com D3D11 offscreen e 1.024 iterações do shader, passou no critério previamente fixado de incremento mediano mínimo de 15 W: observou **30,420 W**. Nas fases de 10/15/10 segundos, obteve 31 relatórios e 62 pares por referência; diferenças absolutas máximas de **13 RPM contra GPU-Z** e **14 RPM contra HWiNFO**, com lags máximos de 497/959 ms. O intervalo térmico foi 36–41 °C. A tolerância foi `max(50 RPM, 5% da referência)`, pareamento nearest até 1.500 ms e reutilização permitida. Os pilotos anteriores continuam registrados: uma falha do prazo final foi corrigida; incrementos de 2,285 W e 13,206 W foram recusados como estímulo insuficiente, sem reduzir o critério. Fontes: `evidence/v010-completion-20260905/pilot-public-4/{protocol.json,summary.json,experiment.json}` e o [relatório da rodada](2026-09-05-v010-completion.md).

**Consolidação final:** três ciclos de repouso/carga/resfriamento produziram 708 relatórios e 1.416 leituras RPM, entre 36 e 56 °C. As diferenças máximas foram 16 RPM para GPU-Z e 20 RPM para HWiNFO, dentro da tolerância estabelecida e considerando todas as candidatas mais próximas pelo pior caso. O pacote passou na validação dos 90 arquivos e em seis consultas reais dos executáveis C++/C#. Serviço instalado e referências foram preservados. Os resultados e hashes estão no [relatório de conclusão](2026-09-05-v010-completion.md). A referência a aproximadamente **55 °C** descreve percepção de ruído, não observação direta de ventoinhas fisicamente paradas. Nem silêncio, nem RPM pretendido, nem concordância entre ferramentas comprovam zero-fan ou rotação física.

## Limites e próximo passo

Os campos privados `+4`, `+8`, `+12` e `+16` permanecem `raw_unknown`; velocidade pretendida em uma API documentada não decodifica automaticamente campos privados, percentuais ou PWM. Dois preflights privados v3 reais de 10 segundos foram válidos, mas uma janela de 30 segundos foi rejeitada por status `0xffffff9b`, sem aproveitamento do buffer recusado. A causa não foi verificada. A investigação está interrompida/parcial; não foram concluídas as seis janelas privadas do [plano do cooler](2026-09-05-v010-cooler-experiment-plan.md), nem demonstrada repetição após reinício das referências. O marco de software foi concluído com os três ciclos públicos aprovados; nenhum candidato privado é promovido por esses resultados.
