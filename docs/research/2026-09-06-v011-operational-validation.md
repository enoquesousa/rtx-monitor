# v0.11 — validação operacional e atualização reversível

Continuação autorizada em 2026-09-06, após a entrega local da v0.11 e a
proposta de teste prolongado e atualização com backup. Este registro distingue
o ensaio em cópia do banco, testes do Windows Service e eventual corte de produção.

## Backup e compatibilidade

A instalação inicial era 0.6.0, `RtxMonitorService`, `LocalSystem`, inicialização
automática e porta 5136. Banco: `C:\ProgramData\RtxMonitor\telemetry.db`.

O backup online verificado preservou 653.829 eventos e os binários/configuração
da 0.6.0. Ele usa a API de backup do SQLite com transação de leitura estável,
integridade e SHA256. A primeira tentativa paginada, sem manter esse snapshot,
atingiu o prazo de 120 segundos durante gravações concorrentes; foi preservada
como incompleta e não foi usada como backup válido. A segunda terminou em
aproximadamente 14 segundos.

O ensaio utilizou uma cópia desse backup. Nenhum dos 653.829 eventos anteriores
foi perdido ou alterado. A versão 0.6.0 reabriu a mesma cópia depois da v0.11,
leu eventos v8 e continuou gravando sem modificar os eventos novos existentes.

## Procedimento de atualização

O novo `scripts/update-service.ps1` apresenta um plano por padrão. Ele valida
hash do ZIP, manifesto, versão, ABI, arquivos, caminhos Windows e espaço livre.
O modo `-Apply` prepara uma pasta nova, preserva configuração e caminho absoluto
do banco, faz backup com o serviço parado e altera somente `binPath` no SCM.
Erros após a parada acionam retorno ao binário anterior e verificação de saúde.
Não há restauração automática de banco nem remoção/recriação do serviço.

Os testes offline cobrem 13 casos, inclusive pacote adulterado, arquivo ausente,
entrada extra/duplicada, caminhos inválidos e destino já existente. O procedimento
foi exercitado também em Windows Service temporário como `LocalSystem`: atualização
0.6→0.11, ambos os sensores disponíveis, reversão e histórico preservado.

Um pacote negativo deliberadamente identificado como fixture simulou versão
incorreta. A verificação de prontidão o rejeitou, e a reversão automática retornou
à 0.6.0 saudável. Esse ZIP fica apenas em evidências e não é uma release.
Os serviços temporários foram removidos; os diretórios de teste permanecem como
evidência. A instalação de produção não participou desses ensaios.

## Primeiro ensaio prolongado e correção da medição

Em 900 segundos, houve 1.656 respostas numéricas já presentes no SQLite,
888 eventos SSE e 917 registros exportados com bytes preservados. Os sensores
permaneceram disponíveis. GPU: utilização observada de 0–20%, média 4,36%;
die 33–34 °C. Isso representa o uso atual do desktop, não carga controlada.

O verificador inicial calculou a cadência pelas aquisições distintas vistas
no cliente HTTP e reprovou um salto aparente de 10.109 ms. A consulta posterior
ao banco encontrou as leituras intermediárias desse intervalo. Portanto, o
resultado não comprova uma parada do sensor. Os intervalos de cerca de 2 segundos
entre valores distintos publicados também não enumeram todas as coletas do
provedor: o sampler público e o provedor têm relógios independentes.

A reprovação original foi preservada. A instrumentação passou a registrar
separadamente o início/fim da requisição e a confirmação SQLite. Um evento Debug
opcional do serviço (`1101`, `ProfileAcquisition`) registra cada coleta, estados,
horários reais dos campos e o relógio monotônico térmico. Ele não fornece valores
ao produto nem altera leitura, gates ou cadência; é desativado no nível normal
de log. A consulta de conferência do cliente passou a ler apenas relatórios por
perfil recentes, evitando decodificar eventos públicos completos sem necessidade.

O protocolo corrigido foi escrito antes da nova janela fixa de 900 segundos.
Ele mantém o limite de 2.000 ms para as aquisições reais, verifica a idade no
momento da resposta pelo contrato existente e trata lacunas de observação HTTP
como diagnóstico. Não houve alteração das tolerâncias térmicas ou seleção de
uma subjanela favorável.

## Resultado final

A janela instrumentada de 900 segundos passou:

| Verificação | Resultado |
| --- | --- |
| Aquisições reais no rastro independente | 891, ambos os campos disponíveis |
| Maior intervalo nativo térmico | 1.181 ms; limite mantido em 2.000 ms |
| Maior intervalo de tensão | 1.179 ms |
| Respostas HTTP | 1.741/1.741 já presentes no SQLite |
| Maior idade do campo na resposta HTTP | 1.969 ms |
| Correspondência com o rastro de aquisição | Todos os pares publicados foram encontrados |
| SSE / exportação | 889 eventos / 978 registros, schemas e conteúdo preservados |
| Banco anterior | 653.829 eventos sem perda ou alteração |
| Reversão da cópia real para 0.6.0 | 978 eventos novos preservados, gravação retomada |

O tempo máximo de requisição foi 89,7 ms; a conferência SQLite do cliente levou
até 28,7 ms. O rastro confirmou cadência nativa contínua, distinguindo-a da
observação HTTP. Os dados não sustentam atribuir retrospectivamente toda a
pausa do primeiro cliente a uma causa única. Sua reprovação original continua
registrada, com a explicação dos limites daquele instrumento.

O pacote atual passou novamente pelo ensaio de atualização/reversão em
Windows Service temporário como LocalSystem. A verificação completa Windows
passou com 45 CTest, 14 testes de auditoria, suítes .NET, testes PowerShell,
13 casos do updater, schemas, formatação e paridade de versão 0.11.0.

### Corte de produção concluído

O serviço ativo foi atualizado para **0.11.0**, com hotspot e tensão habilitados:

- executável: `C:\Program Files\RtxMonitor\0.11.0\RtxMonitor.Service.exe`;
- porta 5136, conta LocalSystem e inicialização automática preservadas;
- política de recuperação SCM conferida antes/depois, sem alteração;
- PID após a troca: 6752; início Unix ms `1788707176750`;
- banco mantido em `C:\ProgramData\RtxMonitor\telemetry.db`;
- **656.938 eventos anteriores ao corte preservados byte a byte**, integridade OK;
- canário de 60 segundos: 60 consultas, nenhuma falha, ambos os campos disponíveis
  e todas as respostas numéricas já confirmadas no SQLite; maior idade 1.095 ms.

Backup do momento da troca, com hashes e diário de atualização:
`C:\ProgramData\RtxMonitor\backups\update-20260906T150614Z-67e99f03828b41d997be734197c54482`.
Os binários e a configuração de `C:\Program Files\RtxMonitor\0.6.0` permanecem
intactos para reversão. Nenhum backup antigo foi restaurado sobre dados atuais.
Os serviços temporários foram removidos. GPU-Z e HWiNFO permaneceram em execução.

Pacote instalado: `artifacts/v0.11.0-service-stable-profile-diagnostics-local.zip`.
SHA256: `09fc23d11c036cde335e429fc0fce32bfd7244ebbbc109cc25ba4a71dd717c36`.
O diagnóstico Debug é opcional; está desligado na configuração de produção.

Amostragem de armazenamento: aproximadamente 18,5 KB de JSON por evento nesta
configuração. A projeção de um evento/segundo equivale a cerca de 1,6 GB/dia de
JSON, sem estimar o tamanho final do SQLite. A retenção de 30 dias do operador
foi preservada; essa projeção fica registrada para planejamento de capacidade.
O disco tinha cerca de 112,8 GB livres na verificação anterior ao corte.

Evidências finais: `continuity-validation.json`, `publication-validation.json`,
`transport-validation.json`, `schema-validation.json`, `production-update-result.json`,
`production-validation.json`, `recovery-after.txt` e `references-after.json` em
`evidence/v011-operation-instrumented-20260906/`.

## Evidências e limites

- `evidence/v011-operation-20260906/`: backup, primeiro ensaio, testes de SCM,
  reversão automática e compatibilidade com a cópia do banco real.
- `evidence/v011-operation-instrumented-20260906/`: protocolo corrigido, pacote
  com diagnóstico opcional, rastros nativos, tempos do cliente e novo ensaio.
- [Procedimento de atualização e reversão](../UPGRADING.md).

Suspensão/retomada e reinicialização do Windows não foram executadas: dependem
da coordenação de uma janela com o proprietário. Também não foi realizado um
ciclo de carga GPU controlada. Esta etapa não substitui a pesquisa de precisão
física nem amplia o perfil de hardware/driver suportado.
