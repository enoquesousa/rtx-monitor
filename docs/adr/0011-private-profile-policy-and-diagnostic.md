# ADR 0011 — política compilada e diagnóstico de perfis privados

- Status: aceito
- Data: 2026-09-05
- Marco: primeiro incremento da v0.9 (registro histórico)
- Complementa: [ADR 0010](0010-fixed-profile-private-nvapi-acquisition.md)

Atualização posterior: o [ADR 0012](0012-private-acquisition-budgets-and-worker.md) acrescenta taxa, prazo e supervisão de processo, evoluindo o relatório para ABI 7/JSON v2. Os detalhes ABI 6 abaixo registram a primeira entrega.

O [fechamento da v0.9](../research/2026-09-05-v09-completion.md) registra o escopo final definido pelo proprietário: somente sua Galax RTX 3060 de 12 GB. Referências abaixo a produto 0.8.0 e novas placas descrevem o estado deste primeiro incremento; os critérios atuais estão no roadmap.

## Contexto

A v0.8 validou duas operações no mesmo perfil físico. Identidade e associação NVML/NVAPI eram verificadas separadamente em cada leitor, e o bloqueio era informado apenas como erro da aquisição. Era necessário formalizar revogação e permitir examinar compatibilidade sem obter valores privados.

## Decisão

`native/src/private_profile_catalog.c` contém uma única entrada constante com ID, revisão, identidade exata, SHA-256 do módulo e duas operações conhecidas. A revogação pode afetar todo o perfil ou somente uma operação. Alterar a política exige mudança de código revisável, incremento de revisão e novo build; não há arquivo de configuração, argumento, setter ou endpoint para ampliar a allowlist. Os valores de endereço, estrutura e escala continuam restritos às operações existentes.

O loader usa o catálogo para resolver somente operações ativas e verificar hash/RVA. O avaliador compartilhado confirma identidade pública e associação única da GPU; uma consulta incompleta não prova unicidade. Diagnóstico e aquisição usam esse mesmo avaliador sob o lock NVAPI. Toda aquisição repete o gate; uma saída anterior de diagnóstico nunca autoriza uma chamada futura.

A ABI 6 acrescenta `rtxmon_get_private_profile_status`, com `struct_size`, índice, revisão/estado, flags de identidade, estados térmico/tensão, ID e motivo de revogação. O layout é 288 bytes, com os textos nos offsets 32 e 160. O comando C# `--profile-status [--gpu INDEX | --gpu-uuid UUID] [--json]` publica esse resultado sem chamar as funções térmica/de tensão. Código zero confirma a produção do diagnóstico, inclusive incompatibilidade; erros de argumentos, inicialização ou seleção por UUID continuam sendo erros do CLI.

Estados possíveis por operação: `unknown`, `compatible`, `revoked`, `identity_unavailable`, `identity_mismatch`, `module_unavailable`, `gpu_not_found`, `identity_ambiguous`, `query_failed`. `compatible` significa elegibilidade para tentar aquisição, não retorno validado. O estado `module_unavailable` agrupa ausência da interface, rejeição de hash/RVA e backend NVAPI indisponível; este incremento não discrimina essas causas internas. Estrutura, máscara, faixa e status de retorno só são avaliados na chamada de aquisição. O diagnóstico declara GSP não observado.

As leituras limpam flags e valores anteriores em todos os caminhos de falha quando o buffer informado possui o tamanho exigido. Buffers menores são recusados sem escrita. O par térmico só é publicado completo.

## Validação e limites

Quatro variantes de teste compiladas usam o mesmo código de política e aquisição, com backend simulado: ativo, perfil revogado, térmico revogado e tensão revogada. As substituições de teste apenas revogam, não acrescentam operações. Os contadores demonstram zero aquisição privada durante diagnóstico e bloqueio da operação revogada mesmo com perfil físico coincidente. Testes também cobrem identidade, associação ambígua, falhas, retorno incompatível e saídas antigas.

O serviço, SQLite, HTTP/SSE e schemas de amostras existentes não ganham sensores privados. A versão de produto permanece 0.8.0 durante este incremento; a v0.9 não está concluída. Novos perfis, comparação entre sistemas e observação de GSP quando aplicável continuam pendentes. Os limites de taxa/timeout, pendentes nesta primeira entrega, foram implementados no [ADR 0012](0012-private-acquisition-budgets-and-worker.md). A revogação passa a valer no novo binário; este catálogo não revoga remotamente processos antigos já instalados.
