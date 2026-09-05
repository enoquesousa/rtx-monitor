# v0.9 — limites de aquisição e supervisor de processo

## Escopo

Segundo incremento sobre a implementação local de perfis da v0.9. Mesmo perfil RTX 3060/driver 610.88 e mesmas duas operações experimentais. Produto continua 0.8.0; o serviço instalado 0.6.0 foi preservado. A política completa está no [ADR 0012](../adr/0012-private-acquisition-budgets-and-worker.md).

## Implementação

- Revisão 2 do catálogo, ABI 7 e diagnóstico JSON v2, mantendo os schemas históricos.
- Janela de 100 ms por operação e por processo, compartilhada entre contextos.
- Prazo monotônico de 2000 ms incluindo lock/gates; resultado tardio descartado e bloqueio até encerrar o processo.
- Worker persistente para os modos térmico/tensão, com timeout de inicialização 10 s e resposta 5 s, além de encerramento limitado.
- Nenhuma amostra em caso de timeout, cancelamento ou protocolo inválido; sem reinício automático.

O prazo nativo não interrompe chamadas síncronas. O supervisor contém a coleta no processo filho e pode encerrar a espera, sem prometer cancelar trabalho já submetido ao driver. Intervalos longos de coleta não são confundidos com timeout de resposta.

## Verificação

`scripts/verify-ci.ps1 -Configuration Release` passou: build C/C++ e C# com avisos como erro, 32 testes CTest, 35 cenários do supervisor e suítes gerenciada/SQLite/serviço/laboratório. A verificação de formatação e os schemas históricos/atuais também passaram. Log: `evidence/v09-acquisition-limits-ci-20260905.log`.

Os testes nativos incluem 14 cenários específicos de aquisição: janela exata de taxa, contexto adicional, independência de operação, erro que consome a janela, timeout de lock/gates/callbacks e falha transitória/regressão do relógio durante a admissão. O supervisor foi testado com processos falsos reais, incluindo PID encerrado antes de retornar a falha. Não foi provocado travamento do driver físico.

Na RTX 3060 foram confirmados:

- quatro amostras térmicas com intervalo de 100 ms, válidas no schema v1;
- duas amostras de tensão com intervalo de 6000 ms, sem falso timeout durante o período ocioso;
- execução via `dotnet RtxMonitor.Console.dll`, selecionando a GPU por UUID;
- seleção de GPU inexistente recusada com código 1 e zero amostras;
- diagnóstico v2/revisão 2 com limites publicados e operações compatíveis.

Os artefatos estão em `evidence/v09-limits-20260905/`. Ao final não restou processo `RtxMonitor.Console` ou `RtxMonitor.Console.Tests` em execução.

`scripts/verify.ps1 -Configuration Release -SkipBuild` também passou: C, C++, C# e `nvidia-smi` devolveram 35 °C nas consultas sequenciais, cobertura pública 30/35, streams/alertas/SQLite/histórico/exportação e endpoints locais válidos. O serviço temporário usou a porta 17164 e foi encerrado após o teste. Log: `evidence/v09-acquisition-limits-smoke-20260905.log`.

O serviço instalado continua saudável na versão 0.6.0 e com o mesmo horário de início `1788604360279` (Unix ms). GPU-Z e HWiNFO permaneceram abertos e responsivos. A validação desta etapa comprova os limites e o isolamento no perfil existente; novos perfis e comparação Windows/Linux continuam pendentes da v0.9.

A revisão final acrescentou uma checagem de cancelamento no pai imediatamente antes de publicar a amostra. Após esse ajuste, o build do console, a formatação e os 35 cenários do supervisor passaram novamente (`evidence/v09-limits-final-console-check-20260905.log`). Uma amostra térmica final do binário atualizado também passou no schema v1.
