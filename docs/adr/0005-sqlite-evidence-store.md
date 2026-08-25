# ADR 0005: SQLite como armazenamento local de evidências

- Status: aceito
- Data: 2026-08-25

## Contexto

Até a v0.4.0, o monitor mantém somente um buffer circular em memória e pode emitir eventos JSON Lines. Isso é suficiente para observar o estado atual, mas não para comparar reinícios, drivers, VBIOS ou experimentos realizados em momentos diferentes.

A futura engenharia reversa precisa de uma cadeia de evidências: o valor bruto ou calculado deve permanecer ligado à sessão de coleta, à identidade da GPU, à placa, ao driver e ao contrato serializado que existia no momento da leitura. Um arquivo de log isolado não garante essas relações nem oferece migrations, retenção ou consultas eficientes.

## Decisão

Criar `RtxMonitor.Storage`, uma biblioteca C# separada da ABI C e do core C++. Ela usa `Microsoft.Data.Sqlite` e mantém um banco local com schema próprio, inicialmente na versão 1.

O banco possui quatro conjuntos de dados:

1. `schema_migrations`: registra migrations aplicadas;
2. `monitor_runs`: registra início, fim, configuração, versão do aplicativo e ambiente de cada execução;
3. `gpu_snapshots`: registra GPU, driver, NVML, PCI, VBIOS, profile key e o resultado da captura da placa;
4. `telemetry_events`: registra o stream completo da v0.4.0 e mantém o JSON original do evento v2.

Cada evento usa a chave única `(run_id, stream_sequence)`. Repetir a mesma gravação com conteúdo e snapshot idênticos retorna o registro existente. Reutilizar a sequência com outro conteúdo é erro explícito; o dado anterior não é sobrescrito.

O SQLite opera com:

- `journal_mode=WAL`;
- `synchronous=NORMAL`;
- foreign keys habilitadas em cada conexão;
- timeout de bloqueio configurável, com padrão de 5 segundos;
- uma conexão curta por operação, sem compartilhar objetos ADO.NET entre threads;
- transações para gravação idempotente, retenção e migrations.

O CLI C# ganha três usos:

- `--watch --database PATH`: coleta e persiste todos os eventos;
- `--history --database PATH`: faz uma consulta limitada;
- `--export --database PATH`: percorre um recorte estável e emite JSON Lines.

O banco não é criado por `--history` ou `--export` quando o caminho não existe. Um arquivo inválido, schema futuro ou estrutura incompatível produz erro e nunca é sobrescrito como forma de “recuperação”.

O padrão de retenção é 30 dias, configurável entre 1 e 3650 dias. A limpeza remove eventos fora da janela, snapshots órfãos também antigos e runs sem eventos cujo último marco conhecido esteja fora da janela. Dentro da retenção, `completed_at_unix_ms = null` permanece como evidência de interrupção sem encerramento confirmado.

## Contrato de exportação

Cada linha exportada segue `docs/schema/evidence-record-v1.schema.json` e contém:

- versão do schema de evidência e do banco;
- identificadores e horários de armazenamento;
- configuração e ambiente do run;
- snapshot da GPU e da placa, quando capturado;
- evento original no schema `telemetry-event-v2`.

O snapshot ligado a um `gap` pode representar a última identidade confirmada naquele run. O próprio evento continua com `gpu_index` e `gpu_name` nulos quando a GPU não estava disponível; a persistência não transforma contexto anterior em leitura atual.

## Motivos

- SQLite oferece transações, constraints, índices e migrations em um único arquivo local.
- A biblioteca separada evita adicionar uma dependência de banco à DLL nativa e aos consumidores que precisam apenas ler a GPU.
- Preservar o JSON v2 permite auditar exatamente o contrato emitido, enquanto colunas selecionadas permitem filtrar sem analisar todos os documentos.
- WAL e conexões curtas permitem leitura concorrente sem manter um objeto de conexão compartilhado.
- Retenção padrão impede crescimento ilimitado antes de o serviço da v0.6.0 existir.

## Consequências

- O executável C# passa a distribuir SQLite e as dependências do `Microsoft.Data.Sqlite`.
- Solicitar persistência torna a gravação parte do contrato: uma falha no banco encerra o processo em vez de perder evidências silenciosamente.
- A sequência continua local a um run; consultas por sequência exigem `run_id`.
- A ordem física `event_id` registra a confirmação no banco, enquanto `stream_sequence` preserva a ordem lógica do stream.
- A v0.5.0 não adiciona serviço, endpoint HTTP, GUI, Parquet nem aquisição experimental.

## Alternativas rejeitadas

### Persistência dentro da ABI C

Rejeitada porque acoplaria SQLite ao caminho de aquisição e aumentaria a superfície binária e de falhas da biblioteca que hoje apenas observa o driver.

### Um arquivo JSON Lines por execução

Rejeitado como armazenamento canônico porque não oferece migrations, constraints relacionais, deduplicação transacional ou consultas indexadas. JSON Lines continua sendo o formato de exportação.

### Uma conexão global compartilhada

Rejeitada porque os objetos do `Microsoft.Data.Sqlite` não são thread-safe. A implementação abre uma conexão por operação e deixa o pooling cuidar do custo de abertura.

### Reparar ou recriar automaticamente um arquivo inválido

Rejeitado porque poderia destruir a única cópia de uma evidência. O erro é explícito e o arquivo permanece intocado para diagnóstico ou recuperação externa.

## Referências

- [Microsoft.Data.Sqlite: transações](https://learn.microsoft.com/dotnet/standard/data/sqlite/transactions)
- [Microsoft.Data.Sqlite: locking, retries e timeouts](https://learn.microsoft.com/dotnet/standard/data/sqlite/database-errors)
- [SQLite: Write-Ahead Logging](https://www.sqlite.org/wal.html)
