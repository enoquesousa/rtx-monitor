# Próximos passos após a v0.10

Estado em 05/09/2026: **v0.10.0 concluída localmente**, com RPM público integrado, cobertura observada de **32/37 registros** e validação do pacote. A instalação em uso permanece na **v0.6.0**, preservada. Este plano não inicia implementação nem registra publicação ou aprovação de PR como realizadas.

O RPM vem da API pública NVML e representa velocidade pretendida reportada pelo driver. As comparações com GPU-Z/HWiNFO não comprovam rotação física, posição das ventoinhas ou PWM. O fechamento e seus limites estão no [relatório da v0.10](2026-09-05-v010-completion.md).

## 1. v0.11 — entregar hotspot e tensão do núcleo pelo canal experimental

Prioridade: disponibilizar no serviço, API local, SSE e histórico os candidatos já validados para o perfil exato desta Galax RTX 3060. Isso transforma evidência existente em funcionalidade utilizável e segue o [roadmap da v0.11](../ROADMAP.md#v0110--provedor-experimental-por-perfis).

- Exigir ativação explícita; manter o helper privilegiado fora do modo padrão.
- Separar o namespace experimental e identificar origem, estado, unidade, horário, perfil e revisão em campos estruturados.
- Preservar valor bruto, fórmula, tolerância e referências da evidência junto à leitura decodificada.
- Revogar a leitura quando a identidade ou as versões não corresponderem ao perfil; perfil desconhecido não recebe aproximação.

**Aceite:** fixtures cobrem perfil válido, desconhecido, alterado e revogado; falhas produzem estado explícito e valor nulo; API, SSE, exportação e histórico preservam a mesma proveniência. Um ensaio isolado confirma hotspot/tensão dentro das tolerâncias já documentadas, sem substituir o serviço instalado. Nenhum campo privado de cooler entra nessa entrega como sensor validado.

## 2. Resolver diferenças de significado na comparação com HWiNFO

As diferenças abaixo são pendências de paridade, não prova automática de leitura errada. Devem ser resolvidas antes de apresentar os números como equivalentes.

| Pendência observada | Trabalho necessário | Aceite |
| --- | --- | --- |
| Clock de memória: aproximadamente 7501 versus 1875 MHz | Identificar domínio do clock e convenção de apresentação em cada fonte. | Fórmula e unidade documentadas e verificadas em mais de um estado; nenhuma multiplicação automática apenas para coincidir. |
| Limite térmico: 93 versus 90 °C | Identificar qual limite e seletor cada ferramenta expõe. | Nome e proveniência distinguem os limites; comparação usa o mesmo conceito. |
| Memória D3D versus HWiNFO | Delimitar uso, orçamento, memória dedicada/compartilhada e escopo da contabilização. | Campos descrevem a contabilidade efetiva; não se promete igualdade entre escopos diferentes. |
| Percentual de TDP | Confirmar potência de origem e denominador da normalização. | Fórmula, unidade e limite de referência explícitos, incluindo indisponibilidade. |
| Erros e link PCIe | Distinguir contadores, intervalo, geração/largura atual e capacidades máximas. | Contratos públicos e estados documentados; ausência de suporte permanece explícita. |

**Aceite comum:** evidência com horários e identidade, comparação em estados adequados e testes das conversões. Esgotar APIs públicas documentadas antes de propor aquisição privada. Cobertura numérica, equivalência semântica e validação física devem continuar separadas.

## 3. Manter a pesquisa privada independente da entrega pública

O cooler continua **`raw_unknown`**. Dois preflights privados de 10 segundos foram válidos; a primeira janela de 30 segundos foi rejeitada por `0xffffff9b` (−101, `NVAPI_EXPECTED_PHYSICAL_GPU_HANDLE`). A causa não foi confirmada. A investigação ficou interrompida/parcial: as seis janelas privadas planejadas e a repetição após reinício da referência não foram concluídas.

- Primeiro esclarecer a falha dentro do perfil e dos limites existentes; não encurtar janelas para procurar sucesso nem aproveitar o buffer rejeitado.
- Só retomar o protocolo privado após essa condição estar compreendida, preservando os gates de identidade, administrador, duração, quantidade, selos e alinhamento.
- Exigir variação repetível, hipóteses alternativas e novas sessões antes de promover campos; entrada de estrutura não equivale automaticamente a ventoinha física.
- Manter tensões de 12 V, leituras por rail e clocks especiais como investigação, começando pelas fontes públicas disponíveis.

**Aceite:** promoção depende dos critérios originais do [roadmap](../ROADMAP.md) e de evidência suficiente. Um resultado inconclusivo permanece identificado como tal. Nenhuma etapa pede alterar ventoinhas, clocks, tensão, limites de potência ou firmware.

## 4. v1.0 — estabilizar a plataforma e então iniciar a interface

Consolidar compatibilidade de ABI, schemas, banco e API; documentar instalação, atualização e recuperação; estabelecer revisão e revogação de perfis e um pacote de diagnóstico sem artefatos proprietários ou dados sensíveis.

**Aceite:** clientes distinguem oficial, calculado e experimental sem interpretar texto; contratos e migrações têm regressões; instalação/atualização são verificadas de forma isolada. A interface gráfica começa depois dessa base, consumindo a API local e preservando os mesmos estados e a mesma proveniência.

Essa ordem entrega primeiro sensores já sustentados por evidência, resolve em seguida diferenças de significado e mantém a investigação de maior incerteza separada do caminho de estabilização do produto.
