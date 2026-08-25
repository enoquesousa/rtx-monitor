# ADR 0003: monitoramento resiliente acima da ABI C síncrona

- Status: aceito
- Data: 2026-08-24

## Contexto

A ABI v2 comprova a identidade da GPU, lê o die e inventaria capacidades térmicas públicas. Os modos contínuos existentes, porém, encerram no primeiro erro. Isso é insuficiente para um processo de longa duração: o driver pode reiniciar, a GPU pode desaparecer temporariamente e o índice de enumeração pode mudar.

Repetir a última temperatura durante uma falha produziria um dado aparentemente atual, mas falso. Reconectar somente pelo índice também poderia trocar silenciosamente de placa em uma máquina com múltiplas GPUs.

O projeto precisa distinguir amostras, lacunas e recuperações, manter memória limitada e permitir testes determinísticos sem depender de uma GPU física.

## Decisão

A ABI C permanece na versão 2 e continua oferecendo operações curtas e síncronas. A política de longa duração será implementada acima dela, com contratos equivalentes nas camadas C++ e C#.

O motor resiliente terá:

1. seleção persistente pelo UUID da GPU;
2. nova resolução do índice a cada sessão reaberta;
3. eventos explícitos `sample`, `gap` e `recovered`;
4. backoff exponencial limitado após erros recuperáveis;
5. buffer circular com capacidade fixa;
6. descarte do contexto após falha recuperável;
7. nenhuma reutilização da última amostra como se fosse atual;
8. uma fábrica de sessões injetável para testes sem hardware.

Os estados recuperáveis são falhas de carregamento/backend, driver não carregado, GPU não encontrada, GPU perdida e erro genérico do backend. Argumento inválido, falta de memória, permissão negada, sensor não suportado e incompatibilidade de ABI continuam fatais.

O CLI aceitará `--gpu-uuid UUID`. No modo `--watch`, um índice informado será resolvido para UUID antes do primeiro ciclo e esse UUID permanecerá como identidade do alvo.

A saída histórica de `--watch --json` continuará emitindo apenas amostras no schema 1. Lacunas e recuperações irão para `stderr`. O novo `--events` emitirá todos os eventos em JSON Lines conforme `telemetry-event-v1.schema.json`.

## Motivos

- UUID identifica o dispositivo lógico com mais estabilidade do que a posição na enumeração.
- Uma lacuna explícita impede que indisponibilidade seja confundida com zero grau ou dado antigo.
- Backoff evita um loop agressivo quando o driver está indisponível.
- Um buffer limitado torna o uso de memória previsível.
- Injeção de sessão permite reproduzir falhas, mudanças de índice e recuperação no CI sem GPU.
- Manter a política acima da ABI evita introduzir threads, callbacks ou relógios no contrato C.

## Consequências

- C++ e C# terão implementações pequenas do mesmo modelo de estados, verificadas por testes equivalentes.
- O modo contínuo poderá continuar após falhas recuperáveis.
- `--count` continuará contando amostras bem-sucedidas, não tentativas nem eventos.
- Durante uma indisponibilidade prolongada, um processo sem limite continuará tentando até `Ctrl+C`.
- O buffer é histórico em memória; persistência em SQLite ou Parquet permanece para uma fase posterior.
- A validação de hardware continuará separada dos testes determinísticos de CI.

## Alternativas rejeitadas

### Manter o índice após uma reconexão

O índice pode passar a representar outra GPU. A sessão deve enumerar novamente e localizar o UUID original.

### Colocar o loop e o backoff dentro da DLL C

Isso adicionaria ciclo de vida assíncrono, cancelamento e callbacks à ABI. A camada C deve continuar sendo uma fronteira pequena e previsível com o driver.

### Emitir a última temperatura durante uma falha

Uma amostra anterior não descreve o estado atual. O motor emite `gap` e deixa a temperatura ausente.

### Substituir o JSON legado de watch

Uma troca silenciosa quebraria consumidores existentes. O envelope completo de eventos exige a opção explícita `--events`.
