# ADR 0007 — telemetria pública e métricas calculadas

- Status: aceito
- Data: 2026-08-25
- Versão: v0.7.0

## Contexto

Antes de investigar registradores privados, o projeto precisa esgotar as superfícies documentadas da NVIDIA e distinguir quatro situações: valor disponível, campo não suportado, provedor ausente e falha de consulta.

Também precisamos calcular tendências térmicas sem apresentá-las como sensores físicos. Persistir apenas o resultado de uma fórmula impediria auditoria e reprodução posterior.

## Decisão

1. A ABI C avança para a versão 3 e expõe um catálogo fechado de campos públicos.
2. `rtxmon_read_public_telemetry` consulta somente funções e IDs explicitamente conhecidos. Cada registro preserva campo, provedor exato, ID/seletor nativo, estado, origem, tipo, unidade, código do driver e timestamp.
3. Um campo ausente mantém valores nulos. Zero só existe quando o driver realmente devolve zero.
4. Fans são registros repetíveis: um registro por índice físico quando `nvmlDeviceGetFanSpeed_v2` está disponível.
5. A NVAPI permanece no inventário térmico complementar. O stream não cria um segundo nome de sensor para um valor duplicado do die.
6. Um motor C++ stateful calcula média por janela, inclinação, tempo acima do limiar e delta entre temperaturas conhecidas. Ele é exposto por uma ABI C opaca e consumido diretamente por C++ e C#.
7. Cada métrica carrega fórmula, unidade, janela, quantidade de amostras, limiar aplicável, entradas, origem e estado.
8. `telemetry-event-v3` persiste os campos brutos e as métricas no mesmo evento `sample`. Lacunas, recuperações e alertas não duplicam esses blocos.
9. O schema SQLite permanece na versão 1 porque o evento já é armazenado como JSON imutável. Runs novos registram `event_schema_version=3`; históricos v2 continuam legíveis e exportáveis.
10. O serviço mantém o último relatório confirmado no snapshot de runtime e o expõe por `GET /api/v1/gpus/{uuid}/telemetry`, sem abrir uma sessão nativa por requisição.

## Alternativas rejeitadas

### Interpretar a saída do `nvidia-smi`

Isso adicionaria um subprocesso e um parser sem fornecer mais proveniência que a NVML já expõe diretamente.

### Varredura de IDs NVML

IDs desconhecidos não formam um contrato e podem mudar de significado. A v0.7.0 usa uma allowlist documentada; exploração pertence ao laboratório experimental futuro.

### Converter ausência em zero

Zero é válido para utilização, encoder, decoder e tempo calculado. Reutilizá-lo para ausência tornaria os dois casos indistinguíveis.

### Persistir somente métricas calculadas

Sem as entradas brutas, janela e fórmula, o histórico não permitiria reproduzir ou contestar o resultado.

### Chamar todo dado térmico de hotspot, memória ou VRM

Uma correlação de comportamento não prova a identidade física do sensor. Somente nomes garantidos pelo provedor são usados.

## Consequências

- A ABI 3 não é binariamente compatível com consumidores compilados para a ABI 2; o runtime C# rejeita a combinação por versão e tamanho de estrutura.
- Eventos novos usam schema v3. Schemas v1 e v2 permanecem imutáveis para históricos anteriores.
- O relatório pode conter quantidades diferentes de registros conforme o número de fans, mas sempre contém um estado para cada campo semântico conhecido.
- Métricas de janela perdem o histórico após uma lacuna, reset ou retrocesso de relógio. Isso impede atravessar uma região sem dados como se ela fosse contínua.
- O endpoint HTTP pode devolver `503` antes da primeira amostra válida e, depois disso, identifica o horário do último relatório mesmo se o coletor estiver degradado.

## Segurança

A decisão não adiciona escrita na GPU, privilégio administrativo, MMIO, I2C, SMBus, leitura de ROM ou driver próprio. Todos os acessos permanecem somente leitura por APIs do driver carregadas de caminhos confiáveis.
