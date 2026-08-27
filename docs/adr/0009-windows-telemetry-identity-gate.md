# ADR 0009 — telemetria Windows com identidade DXGI/PCI

- Status: aceito
- Data: 2026-08-26
- Versão: v0.8.0

## Contexto

O WDDM publica memória e atividade de engines por Performance Counters do Windows. As instâncias são identificadas por LUID, PID e engine físico, não pelo UUID NVML usado pelo restante do monitor. Em máquinas com mais de um adaptador, escolher a primeira instância ou inferir identidade pela ordem, pelo uso ou pela topologia pode atribuir dados de outra GPU à RTX monitorada.

Os contadores também distinguem memória local e não local e podem deixar de publicar uma instância de engine quando ela está ociosa. Somar esses valores como “dynamic memory” ou converter ausência em zero perderia semântica e criaria telemetria falsa.

## Decisão

O serviço implementa um provider Windows somente leitura, executado em background e separado da sessão NVML:

1. enumera adaptadores por DXGI;
2. exige uma única correspondência de `VendorId`, `DeviceId` e `SubSysId` com a identidade PCI da GPU descoberta pela NVML;
3. usa o `AdapterLuid` dessa correspondência para selecionar exclusivamente as instâncias PDH;
4. falha de forma explícita quando a identidade está ausente, incompatível ou ambígua, antes de publicar qualquer contador;
5. mantém memória local e não local como valores independentes;
6. publica sempre o catálogo fixo `3D`, `Copy`, `VideoDecode`, `VideoEncode`, `OFA` e `VR`;
7. agrega processos do mesmo engine físico, limita a utilização física a 100% e usa o maior engine físico para representar cada tipo;
8. distingue `inactive`, quando uma amostra válida observou zero, de `counter_unavailable`, quando não houve amostra válida;
9. preserva falhas e amostras parciais em estados explícitos, sem inventar zero.

Contadores de taxa usam duas coletas PDH. Requisições HTTP leem somente o último snapshot imutável do worker. O endpoint `/api/v1/gpus/{uuid}/windows-telemetry` expõe o contrato `windows-telemetry-v1`.

O último snapshot confirmado também é anexado ao evento `sample` v4. Como o SQLite armazena o JSON versionado integralmente, não há migration estrutural: o mesmo objeto segue para histórico, exportação e SSE. Eventos de lacuna, recuperação e alerta mantêm `windows_telemetry` nulo para não duplicar uma observação bruta.

## Consequências

- A identidade da fonte é demonstrável por PCI e LUID, sem depender da ordem dos adaptadores.
- Um endpoint pode responder com estado parcial mesmo quando memória ou alguns engines estão disponíveis.
- Memória local e não local não são uma aproximação de capacidade nem uma métrica chamada “dynamic memory”.
- Engines sem instância ativa permanecem indisponíveis; zero só representa uma amostra válida.
- O provider não requer elevação, não reinicia dispositivos e não escreve no driver ou na GPU.
- O contrato é específico do Windows; outras plataformas continuam sem esse bloco.

## Alternativas rejeitadas

### Selecionar o primeiro adaptador ou o primeiro LUID

Rejeitado porque enumeração não é identidade e pode mudar entre boot, atualização de driver ou hotplug.

### Correlacionar por carga, memória ou conjunto de engines

Rejeitado porque são propriedades dinâmicas e podem coincidir entre adaptadores.

### Somar memória local e não local

Rejeitado porque os pools têm semânticas distintas e a soma não comprova “dynamic memory”.

### Somar todos os engines do mesmo tipo

Rejeitado porque múltiplas instâncias por processo podem representar o mesmo engine físico. A agregação ocorre primeiro por engine físico; entre engines do mesmo tipo, usa-se o máximo.

### Consultar DXGI e PDH dentro da requisição HTTP

Rejeitado porque acoplaria latência e falhas do provider ao cliente. O worker mantém aquisição e publicação independentes do tráfego HTTP.

## Evidência

- [Inventário real dos contadores Windows](../research/2026-08-26-windows-gpu-counter-inventory.md)
- [Validação prolongada e recuperação física](../VALIDATION.md#validação-prolongada-da-telemetria-windows)
- [Schema `windows-telemetry-v1`](../schema/windows-telemetry-v1.schema.json)
- [OpenAPI do serviço local](../openapi/service-v1.openapi.json)
