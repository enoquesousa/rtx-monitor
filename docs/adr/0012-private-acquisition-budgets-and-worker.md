# ADR 0012 — frequência, prazo e processo de aquisição privada

- Status: aceito
- Data: 2026-09-05
- Marco: segundo incremento da v0.9 (registro histórico)
- Complementa: [ADR 0011](0011-private-profile-policy-and-diagnostic.md)

O [fechamento da v0.9](../research/2026-09-05-v09-completion.md) registra a versão final 0.9.0 e o escopo definido pelo proprietário: somente sua Galax RTX 3060 de 12 GB. As menções a produto 0.8.0 e novos perfis ao fim deste registro descrevem o estado anterior ao fechamento. GSP continua `not_observed` e o serviço instalado continua sendo uma implantação separada.

## Contexto

O gate de identidade e revogação não limitava a frequência das chamadas. Os modos C# de temperatura/tensão executavam P/Invoke síncrono no próprio processo: cancelamento só era observado entre leituras. Uma espera assíncrona com timeout não cancela a chamada síncrona que a originou.

## Política compilada

O catálogo passa à revisão 2. Cada operação admite no máximo uma tentativa a cada 100 ms; a operação térmica é o par completo die/hotspot. A janela é compartilhada entre contextos do mesmo processo e independente entre térmico e tensão. Admissões que chegam ao backend consomem a janela mesmo quando falham. Diagnóstico e consultas rejeitadas pelos gates não consomem a janela. Não há coordenação entre processos, GPU-Z ou HWiNFO.

Cada leitura possui prazo de 2.000 ms, medido com relógio monotônico desde sua entrada, incluindo espera pelo lock e gates. São feitos checkpoints nas consultas de identidade/associação, antes/depois dos callbacks e antes da publicação. Um canal térmico que esgote o prazo impede a consulta do próximo. Resultado tardio perde todos os valores/flags, e qualquer timeout bloqueia ambas operações privadas durante a vida do processo. Falha ou regressão do relógio também bloqueia. A chamada que já está dentro do driver não pode ser interrompida por esse mecanismo.

`rate_limited` e `timeout` são estados do RTX Monitor, não códigos atribuídos à NVIDIA. A ABI 7 acrescenta os status 12/13 e quatro campos de política ao relatório de perfil (304 bytes). O JSON de diagnóstico passa a v2; o schema v1 permanece para históricos. O estado `compatible` continua sujeito à janela dinâmica de admissão. Após timeout, o diagnóstico informa `timeout` sem consultar o backend.

Esses limites são políticas iniciais do projeto, não características garantidas pelo driver. Sua alteração exige revisão do catálogo, código e testes; o usuário não pode aumentá-los por configuração.

## Supervisor dos modos experimentais

`--thermal-watch` e `--voltage-watch` agora iniciam um filho persistente do mesmo executável. Só o filho abre `NvidiaMonitor`. Ele aceita pelo stdin as solicitações sequenciais `sample N` e `stop`, e produz envelopes JSON pelo stdout. Há somente duas operações fixas e nenhuma operação de hardware adicional.

O pai admite uma solicitação por vez, aplica o intervalo escolhido pelo usuário e mantém a saída pública JSON das amostras em v1. A inicialização/seleção da GPU tem watchdog de 10 segundos; escrita e resposta de cada aquisição têm watchdog de 5 segundos. Não há watchdog durante o intervalo ocioso, inclusive com `--interval 60000`.

Timeout, cancelamento, EOF inesperado ou protocolo inválido invalidam o cliente e encerram o filho; não há reinício automático nem publicação de resposta tardia. Respostas são limitadas a 16 KiB e a captura de stderr a 4 KiB. O pai verifica sequência, versão, identidade, origem, relógios e medidas antes de publicar. No encerramento normal, solicita `stop` e espera até 2 segundos; se necessário, encerra o filho e espera até 2 segundos pela confirmação. A drenagem de pipes também é limitada. Falha ao confirmar a saída é reportada com o PID.

O watchdog limita a espera do cliente e contém a aquisição em processo separado. Ele não cancela nem desfaz uma operação já submetida ao driver, e não oferece garantia de recuperação do kernel. Usuários diretos da ABI/P/Invoke continuam responsáveis por isolamento quando precisam conter chamadas que não retornam. O diagnóstico avulso `--profile-status` não possui esse supervisor.

## Testes e fronteiras

Testes nativos usam relógio e callbacks simulados para cobrir limites exatos, contexto adicional, falha transitória do relógio, fila de lock, gates lentos, retorno tardio e bloqueio persistente. Não se provoca travamento real do driver.

A suíte `RtxMonitor.Console.Tests` inicia processos falsos reais para verificar handshake, sucesso, espera ociosa, timeout, cancelamento, encerramento, ausência/duplicação de campos, tamanho excessivo e identidade/valores inválidos. O encerramento é confirmado pelo PID criado por cada teste. Os testes não usam a GPU.

O serviço instalado, telemetria pública, SQLite e HTTP/SSE não ganham dependência do worker privado. A v0.9 permanece pendente de novos perfis e validações por configuração; produto continua 0.8.0 durante estes incrementos locais.

## Referências de execução

A documentação de [Task.WaitAsync](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.waitasync?view=net-8.0) define uma espera que termina por conclusão, timeout ou cancelamento; a contenção aqui exige também encerrar o processo filho. [Process.Kill](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-8.0) exige confirmação posterior da saída. Essa confirmação não comprova o encerramento de todos os descendentes; o worker de produção deste projeto não cria subprocessos.
