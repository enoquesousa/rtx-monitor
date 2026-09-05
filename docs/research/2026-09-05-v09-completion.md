# Fechamento da v0.9 — Galax RTX 3060 de 12 GB

Estado: **concluída localmente em 2026-09-05**, com versão de produto **0.9.0** e ABI nativa **7**, para a única placa alvo definida pelo proprietário. Código, documentação, testes e pacote local foram produzidos; implantação do Windows Service é uma operação separada.

## Escopo e identidade

O proprietário esclareceu que o projeto se destina somente à sua Galax RTX 3060 de 12 GB. Não é requisito validar outras placas. Alterações futuras do driver, VBIOS ou módulo desta mesma unidade exigem nova evidência e revisão.

| Identidade | Valor |
| --- | --- |
| Nome comercial | Galax RTX 3060 12 GB, informado pelo proprietário |
| Nome/VRAM publicados por NVIDIA | NVIDIA GeForce RTX 3060 / 12288 MiB |
| UUID | `GPU-fca3647e-8390-15a8-f23b-d0f870c9accd` |
| PCI / subsystem | `10de:2504` / `10de:1536` |
| VBIOS / driver | `94.06.25.00.fc` / `610.88` |
| GSP | `not_observed`; versão desconhecida não é declarada validada |
| Perfil de aquisição | Windows x64, catálogo compilado revisão 2 |

O fabricante comercial não foi inferido do subsystem PCI. Os pins completos de módulo, operações e layouts estão no [manifesto auditável](../profiles/rtx3060-galax-12gb.json).

## Critérios entregues

| Critério | Evidência |
| --- | --- |
| Compatibilidade explícita | `--profile-status`, JSON v2, sem aquisição privada; gates repetidos em cada leitura |
| Revogação global/por operação | Catálogo compilado; operação revogada retorna antes de lock ou consultas ao backend, preservando a operação ativa |
| Taxa e prazo | Mínimo de 100 ms por operação/processo; orçamento nativo de 2000 ms; resultado tardio descartado e processo bloqueado para novas aquisições |
| Contenção no CLI | Worker persistente supervisionado: inicialização 10 s, resposta 5 s, encerramento limitado; nenhum reinício automático após falha |
| Auditoria da política | Snapshot compilado comparado integralmente; 16 fontes, 3 referências documentais e 7 fixtures/proveniências ancoradas por hash |
| Regressão de dados | Buffers térmicos/tensão históricos, decodificação exata, tamanho/revisão alterados, máscara incorreta e falhas após escrita; dois JSONs reais do monitor x64 |
| Módulo/layout | Positivo com hash/RVA da própria imagem de teste e negativos independentes; contratos estáticos de largura, tamanho e offset |
| Plataforma | Windows físico e Linux offline; as duas operações privadas não têm implementação NVAPI Linux |

A fixture de buffers vem da imagem GPU-Z **x86**, distinta da imagem autorizada do monitor **x64**. Ela testa layout/decodificação; não autoriza o módulo x64 por equivalência. [Proveniência dos buffers](../profiles/rtx3060-fixture-provenance.json) e [proveniência das amostras x64](../profiles/rtx3060-runtime-fixture-provenance.json) mantêm essa distinção. Hashes detectam drift e atualizações parciais; revisão de código e evidência física continuam necessárias.

## Validação executada

| Execução | Resultado |
| --- | --- |
| `scripts/verify-ci.ps1 -Configuration Release` | 32/32 CTest; Managed, Storage, Console, Service e Lab; formatação, schemas e paridade de versão 0.9.0 |
| Supervisor Console | 35 casos sem hardware, incluindo timeout, cancelamento, espera ociosa, protocolo inválido e saída confirmada do filho |
| Auditoria Python | 14/14 testes; comparação do snapshot compilado aprovada em Windows e Linux |
| `scripts/verify-ci-linux.sh Release` | 28/28 CTest; Managed, Storage, Console e recusa de plataforma do Lab; GCC com warnings como erros |
| `scripts/verify.ps1 -Configuration Release -SkipBuild` | C, C++, C# e NVIDIA-SMI em 35 °C; serviço temporário em 35 °C; streams, alertas, SQLite, histórico/exportação e HTTP aprovados |
| Coleta final do monitor | Duas sessões independentes, 12 amostras térmicas e 12 de tensão por sessão: 48/48 válidas |
| Console empacotado | Diagnóstico válido e quatro amostras adicionais válidas a intervalo de 100 ms |

Logs locais: `evidence/v09-final-ci-20260905.log`, `evidence/v09-final-linux-ci-20260905.log`, `evidence/v09-final-smoke-20260905.log` e `evidence/v09-final-20260905/`.

O Linux local foi executado em contêiner Debian Bookworm x64, sem GPU, com SDK .NET 8 e GCC 12. A imagem foi fixada pelo digest `sha256:bb32ba3ba3ea36e38572d9d8db76fa15f7cbf722f3f886e06bca6d528bd4fba8`. A conexão NuGet do contêiner não confiava no certificado apresentado; essa execução usou sete pacotes já presentes no cache do projeto, com SHA-512 conferido, em feed local isolado. A verificação TLS não foi desabilitada. O restore normal do NuGet foi posteriormente validado pelo CI remoto abaixo.

O snapshot Windows/Linux tem SHA-256 canônico `5a7e71afe5143665da456ff7d0850ed8f4043123517ea2d4c5c3e0fb9a153e45`. Essa comparação é do catálogo/ABI offline. O [guia NVIDIA para WSL](https://docs.nvidia.com/cuda/wsl-user-guide/index.html) também distingue as limitações de NVML nesse ambiente; nenhum canal privado Linux foi declarado equivalente por este teste.

## Publicação da branch e CI remoto

O commit [`a3c7504`](https://github.com/enoquesousa/rtx-monitor/commit/a3c750400d99ac293f3d5998a62567102aef6bea) publicou a implementação e documentação da v0.9 na branch `codex/v09-profile-compatibility`, com a [PR #11](https://github.com/enoquesousa/rtx-monitor/pull/11) direcionada a `main`.

O [CI remoto 33995095124](https://github.com/enoquesousa/rtx-monitor/actions/runs/33995095124), referente a esse commit, terminou com sucesso em ambos os jobs: Windows x64 em 3 min 52 s e Linux x64 em 1 min 3 s. Os checkouts remotos reproduziram 32/28 CTest, auditoria, testes .NET aplicáveis e restore normal de dependências, sem os arquivos ignorados de evidência/build locais. A execução Windows também aprovou schemas, formatação e paridade de versão 0.9.0.

Este registro identifica a revisão testada. Novos commits da PR exigem conferir seus próprios checks. Revisão/aprovação formal da PR, merge e implantação do serviço permanecem estados distintos da validação técnica registrada aqui.

## Comparação física final

GPU-Z e HWiNFO permaneceram em execução e seus logs cresceram antes, no meio e depois das duas sessões. O pareamento usa o timestamp mais próximo, limite de 1100 ms e pode reutilizar linhas de referência. A maior defasagem foi 479 ms no GPU-Z e 921 ms no HWiNFO.

| Referência | Maior diferença die | Maior diferença hotspot | Maior diferença tensão |
| --- | ---: | ---: | ---: |
| GPU-Z | 1,038 °C | 1,181 °C | 0,00025 V |
| HWiNFO | 1,238 °C | 1,281 °C | 0,00025 V |

São leituras assíncronas durante atividade normal do desktop, com tensão em um único patamar. Esta repetição verifica execução e registra as diferenças atuais; não repete o gate térmico estrito histórico nem a validação multipatamar. O estágio experimental anterior foi preservado. Comandos, janelas, pares individuais e hashes estão no pacote de evidência local.

## Pacote e estado do produto

`artifacts/v0.9.0/` contém Console, Service, ferramentas nativas e Lab, com manifesto de hashes de 68 arquivos. SHA-256 do manifesto: `6beec68502bfe7aa8c1b350d311bd2af7a9869f77919b55e7cf3af9bd53535f9`. O índice final dos artefatos da captura tem SHA-256 `d0e7fd860c5e76e7f39856a976451e1486c6b0255de53fd98b711911fa15f976`.

O Console empacotado foi executado fisicamente. O serviço do build passou no smoke com banco e porta isolados. Uma verificação adicional de inicialização/encerramento do serviço empacotado foi bloqueada pela política automática de execução, que informou somente `blocked by policy`; essa inicialização extra não foi realizada. Seus cinco binários principais foram comparados por hash com a saída do build.

O serviço instalado continua saudável na versão 0.6.0, PID 8860, mesmo início `1788604360279`, com o banco original. GPU-Z e HWiNFO permaneceram ativos. Os processos temporários de coleta e o contêiner de validação foram encerrados.

Cobertura pública atual: **30/35 campos disponíveis**, cinco `not_supported`, zero `provider_unavailable` ou `query_failed`. Os cinco campos ausentes são temperatura de memória e limites térmicos de shutdown, slowdown, memória máxima e GPU máxima. Hotspot e tensão continuam disponíveis nos modos experimentais. Cooler/RPM/PWM permanece `raw_unknown`, para a v0.10; publicação dos sensores privados no serviço segue na v0.11.
