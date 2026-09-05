# Retomada da v0.9 — política e diagnóstico do perfil

## Estado encontrado em 2026-09-05

O checkout começou limpo em `main`, commit `5be9565`, com a v0.8 concluída. A GPU continua sendo a RTX 3060 do perfil validado, PCI `10de:2504`, subsystem `10de:1536`, VBIOS `94.06.25.00.fc`, driver `610.88`. Uma consulta atual retornou 30 de 35 campos públicos disponíveis, cinco `not_supported` e nenhum erro de consulta. Hotspot e tensão continuam experimentais; RPM/PWM do candidato cooler continua `raw_unknown`.

O Windows Service instalado está saudável, mas usa a versão 0.6.0 em `C:\Program Files\RtxMonitor\0.6.0`, com banco em `C:\ProgramData\RtxMonitor\telemetry.db`. A implementação local e os testes desta rodada são separados dessa instalação. Ela não foi atualizada, parada ou substituída.

## Repetição física antes da alteração

Pacote local ignorado pelo Git: `evidence/v09-baseline-20260905-170031/`. Duas sessões independentes, com 12 amostras térmicas e 12 de tensão cada, totalizam 48 amostras válidas nos schemas existentes. As quatro execuções encerraram com código zero.

GPU-Z e HWiNFO registraram amostras atuais e seus logs cresceram antes/meio/depois de ambas as janelas. Os horários locais foram 17:03:15–17:03:20 e 17:03:23–17:03:28, UTC−03:00. O cabeçalho GPU-Z corresponde à última sessão do log, que possui Crossbar Clock, preservando a diferença em relação ao cabeçalho histórico.

| Comparação | Maior erro absoluto nas duas sessões |
| --- | ---: |
| Die × GPU-Z | 0,144 °C |
| Hotspot × GPU-Z | 0,738 °C |
| Die × HWiNFO | 0,119 °C |
| Hotspot × HWiNFO | 0,538 °C |
| Tensão × GPU-Z e HWiNFO | 0,00025 V |

A tensão permaneceu em 0,95625 V, um único patamar. O pareamento usou o timestamp mais próximo, com limite de 1.100 ms; o lag máximo observado foi 451 ms no GPU-Z e 996 ms no HWiNFO. Linhas de referência podem ser reutilizadas e o GPU-Z registra segundos inteiros. Essa comparação assíncrona não repete o gate térmico estrito da captura passiva histórica e não amplia o perfil nem promove a evidência. Não houve carga controlada ou ajuste de hardware.

O pacote contém comandos, janelas, logs atuais, pares individuais, amplitudes, tamanho e SHA-256 dos artefatos. O índice de checksums de 46 artefatos possui SHA-256 `1cd44d9d1fa51d86e5cbdc5a209f5cd6a0a5cf7fdd3dc3603fe034412cc0a318`; o relatório de comparação, `7e399bdf5e565a8c81028192520f14c1b850ac35272ce394797324db8bdbda7e`.

## Incremento implementado

Catálogo compilado com revisão e revogação global/por operação; gate compartilhado; diagnóstico C# `--profile-status --json`; ABI 6 aditiva com relatório de 288 bytes; testes de incompatibilidade, revogação e limpeza de amostras antigas. O [ADR 0011](../adr/0011-private-profile-policy-and-diagnostic.md) registra os limites. Produto permanece 0.8.0 e o marco v0.9 segue em andamento.

## Verificação da implementação

`scripts/verify-ci.ps1 -Configuration Release` passou: build C/C++ e C# com avisos tratados como erro, 18 testes CTest (incluindo quatro variantes da política), suítes gerenciada/armazenamento/serviço/laboratório, formatação e contratos JSON. O teste de schema rejeitou revogação com elegibilidade, identidade divergente, módulo indisponível com elegibilidade, aquisição declarada pelo diagnóstico e operação duplicada. As opções de coleta conflitantes foram recusadas antes da abertura da GPU. Log local: `evidence/v09-ci-20260905.log`.

`scripts/verify.ps1 -Configuration Release -SkipBuild` passou na RTX 3060. C, C++, C# e `nvidia-smi` devolveram 36 °C nas consultas sequenciais; o serviço temporário também leu 36 °C. Cobertura pública permaneceu 30/35. Streams, alertas, persistência SQLite, histórico, exportação e endpoints locais passaram. O serviço temporário usou porta 52329 e banco exclusivo de teste, sendo encerrado ao final. Log local: `evidence/v09-smoke-20260905.log`.

O novo diagnóstico físico retornou perfil ativo/revisão 1, sete identidades coincidentes e ambas operações `compatible`, com aquisição privada declarada falsa. Para índice inexistente `4294967295`, produziu JSON válido, ambas operações inelegíveis e `query_failed` (a NVML retornou erro nessa consulta). Três novas amostras térmicas e três de tensão, já com a ABI 6, passaram nos schemas existentes. Diagnósticos, amostras, hash da DLL e saúde da instalação preservada estão em `evidence/v09-implementation-20260905/`.

## Próximos gates

Continuam pendentes limites de taxa/timeout e testes correspondentes, novos perfis com evidência própria, comparação Windows/Linux quando aplicável e validação dos candidatos ainda desconhecidos. Integração dos sensores experimentais no serviço pertence à v0.11, e a interface final vem depois das fontes/APIs.
