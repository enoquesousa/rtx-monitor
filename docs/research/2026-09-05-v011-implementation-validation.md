# v0.11 — implementação local e validação

Registro histórico do primeiro candidato. As correções posteriores de bruto, supervisão, cadência e pacote estão na [continuação da validação](2026-09-06-v011-correction-validation.md). Os resultados abaixo permanecem preservados.

Estado: **provedor experimental implementado localmente; aceite térmico pendente**. Produto 0.11.0, ABI 7. Esta implementação não encerra o marco da v0.11, não publica uma release e não substitui o serviço instalado 0.6.0.

## Entrega de software

- Configuração explícita `ExperimentalTelemetryEnabled`, padrão `false`; intervalo próprio com limites e supervisão de processos.
- Endpoint separado `/api/v1/experimental/gpus/{gpuUuid}/telemetry`; uma consulta somente lê o snapshot.
- Hotspot e tensão com origem experimental, estados estruturados, horário, identidade observada, perfil/revisão, representação bruta, fórmula, unidade, tolerâncias históricas e referências.
- Gates do catálogo compilado e dos payloads. Identidade desconhecida/alterada, revogação, falhas, timeout e cancelamento produzem valores e bruto nulos. Uma sessão invalidada não reinicia automaticamente para procurar sucesso.
- Evento v6 somente quando há relatório experimental. Modo padrão conserva v5; standalone público v3, HTTP público v2, banco/evidência v1. Eventos antigos v1–v5 permanecem legíveis.
- Persistência do JSON canônico já existente, sem migração destrutiva, com a versão individual do evento e a maior versão do run registradas.

O raw térmico é o inteiro em milésimos de grau exposto pela ABI. O DWORD original em ponto fixo 8 não é exposto por essa ABI: `wire_raw_value=null` e `wire_raw_state=not_exposed_by_abi`. Essa limitação é distinta de preservar o bruto em microvolts, disponível para tensão. Não foi criado helper privilegiado nem ampliada a superfície de aquisição privada.

## Regressões

Fixtures cobrem valores gravados, perfil desconhecido, revisão alterada, revogação, identidade/versões incompatíveis, payload inválido e estados de falha. Testes do coletor verificam zero acesso no modo desativado, limites de intervalo, supervisão e cancelamento, expiração, ausência de GPU e proibição de reinício após falha.

O primeiro evento após uma mudança de identidade é verificado contra a GPU e a evidência daquele evento, antes de persistir ou enviar ao SSE. O teste reproduz a situação em que o estado global ainda contém a identidade anterior e exige valores nulos já nesse primeiro evento.

Testes HTTP/SSE/SQLite/exportação usam um servidor isolado com fixtures e exigem o mesmo relatório tanto no sucesso quanto em timeout. A regressão de armazenamento reabre o banco e confirma os bytes dos históricos, a idempotência e a rejeição de conteúdo conflitante. O transporte Console compartilha os limites de protocolo e seus 35 casos sintéticos.

Validação Windows Release aprovada: `verify-ci.ps1` e, após o guard final de identidade do serializador, `verify.ps1` completo, incluindo novamente CI e smoke físico. Passaram **33 CTest, 14 testes de auditoria, 37 verificações PowerShell**, suítes Managed/Storage/Console/Service/Lab, schemas, formatação e paridade 0.11.0. Fontes: `windows-ci-final.log`, `physical-smoke-final.log` e `service-tests-final.log`. O primeiro CI foi interrompido por formatação de testes; a correção e as rodadas aprovadas estão preservadas. Não foi executada uma nova rodada Linux nesta entrega.

O guard comum de serialização também recusa relatório associado a outro UUID e leitura disponível com índice/driver/NVML divergentes. Fixtures de falha podem preservar versões observadas diferentes, sempre com valores nulos e o UUID correto. A exportação CLI real produziu **62 registros**; os 20 eventos da captura SSE foram encontrados integralmente iguais. Todos os registros exportados e os 20 relatórios/SSE passaram no JSON Schema com referências resolvidas exclusivamente de arquivos locais: `export-validation.json`, `actual-schema-validation.json`.

## Execução real isolada

Evidências em `evidence/v011-implementation-20260905/`.

Primeiro, a instância padrão na porta `11883`, com banco `default-mode.db`, retornou os dois campos em `disabled`, valores nulos e evento v5. Não existia processo `--private-worker`. Artefato: `default-mode.json`.

Depois, uma instância opt-in na mesma porta usou `experimental-mode.db`. A captura previamente limitada a 20 eventos e 45 segundos obteve **20 relatórios SSE/40 valores experimentais disponíveis**. Os **20/20 relatórios consultados na API também foram encontrados sem alteração no histórico** e todos os eventos SSE coincidiram integralmente com o JSON persistido. `capture-result.json`, `experimental-samples.jsonl`, `live-events.jsonl` e `history.json` preservam o resultado.

Logs GPU-Z/HWiNFO cresceram antes, no meio e depois da janela. Prefixos foram selados com tamanho e SHA-256, limite individual de 64 MiB e exclusão somente da última linha incompleta para o parser. `capture-manifest.json` identifica os artefatos e o protocolo; os prefixos brutos são preservados. Houve atividade normal de fundo, sem workload criado por esta validação ou ajuste de ventoinhas, clocks, tensão, potência ou firmware.

## Comparação externa: tensão confirmada na janela; hotspot pendente

O analisador offline mantém todos os timestamps únicos por campo. Pareia o horário impresso mais próximo em até 1.100 ms, usando o pior erro entre todas as linhas empatadas/duplicadas. Cada sessão GPU-Z usa seu próprio cabeçalho; colunas HWiNFO são selecionadas por nome exato e índice. Os cálculos de tolerância usam representação decimal.

| Campo / referência | Pares | Maior diferença | Tolerância histórica | Resultado desta janela |
| --- | ---: | ---: | ---: | --- |
| Hotspot / GPU-Z | 20 | 2,381 °C | 0,051 °C | 19/20 fora da tolerância |
| Hotspot / HWiNFO | 20 | 4,681 °C | Não estabelecida | Erro informado, sem aceite declarado |
| Tensão / GPU-Z | 20 | 0 V | 0,001 V | 20/20 dentro |
| Tensão / HWiNFO | 20 | 0 V | 0,002 V | 20/20 dentro |

O lag térmico GPU-Z foi de 7–319 ms; HWiNFO, 242–741 ms. Não houve empate/duplicata nos pares desta janela. Os testes sintéticos do analisador verificam essas ambiguidades, cabeçalhos repetidos, colunas duplicadas, limite exato de tolerância e tamanho máximo.

Fonte: `comparison.json`, que registra os pares, políticas, tamanhos e hashes. **Não se atribui a divergência automaticamente à defasagem temporal**: as aquisições são assíncronas, mas a causa concreta não foi determinada. O gate histórico não foi relaxado; o ensaio não foi repetido para escolher uma janela favorável. O próximo aceite é investigar essa divergência e validar novamente hotspot com protocolo apropriado, preservando o ensaio que falhou.

`available` descreve a aquisição aceita pelos gates do perfil, não uma calibração ao vivo. A evidência `matched_external_reference` e as tolerâncias pertencem ao histórico do perfil. Esta sessão não demonstra acurácia física, novos perfis ou promoção de campos privados de cooler, que continuam `raw_unknown`.

## Preservação

A instância isolada recebeu encerramento gracioso; ambos os filhos terminaram. `preservation-after-experimental.json` confirmou somente o serviço instalado remanescente, PID **8860**, versão **0.6.0**, saudável e com o mesmo início **1788604360279**. GPU-Z PID **36020** e HWiNFO PID **30380** foram preservados. Nenhuma instalação, atualização ou publicação externa foi realizada.

## Pacote local candidato

`artifacts/v0.11.0-service-local-candidate.zip` contém o serviço Windows x64 dependente do runtime .NET, com **41 arquivos de conteúdo e um manifesto**. Os 41 hashes, tamanhos, conjunto de entradas e CRC do ZIP foram conferidos. SHA-256 do ZIP: `af719533f0c2adbd44f9cb073ae7abb6ed3c9c71b38b186c384a6b6f27e3e313`. Fontes: `package-build.log`, `package-result.json`, `package-integrity.json`.

O executável empacotado passou em `127.0.0.1:11884`, banco próprio: versão 0.11.0 saudável, três amostras persistidas, canal experimental desativado e zero filhos privados. Esse smoke do pacote valida o modo padrão; a aquisição experimental real foi verificada no build isolado descrito acima. A integridade do pacote não encerra o aceite térmico. `package-smoke.json` registra o escopo; `preservation-final.json` reconfirma o serviço instalado após todos os testes e o encerramento de todas as instâncias temporárias.
