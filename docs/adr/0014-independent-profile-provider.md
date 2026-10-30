# ADR 0014 — provedor independente por perfil

Data: 2026-09-06. Decisão autorizada pelo proprietário.

## Problema

O candidato v0.11 preservava leituras do driver, mas aplicava um limite de
arredondamento de uma mesma aquisição a comparações de leitores assíncronos.
Os resultados divergentes foram mantidos. A investigação com horários por
retorno confirmou aquisições distintas e a equivalência de arredondamento
dos 18 retornos da própria referência; não demonstrou precisão física nem
que toda diferença entre leitores seja exclusivamente temporal.

## Decisão

Adotar o critério autorizado `v011-source-fidelity-1`: fonte identificada,
conversão exata, independência, fidelidade de transporte e supervisão estável
no perfil exato. Nenhum offset, tolerância ampliada ou leitura de um programa
de referência será usado no produto.

O relatório `profile-telemetry` v1 e os eventos v8 usam `private_profile`.
O alcance das tolerâncias é obrigatório e estruturado. A API canônica é
`/api/v1/gpus/{gpuUuid}/profile-telemetry`; a rota do candidato local é
substituída. As chaves de configuração anteriores são aliases de migração
exclusivos. Configurações com ambas as chaves são rejeitadas.

Relatórios experimentais v1/v2 e eventos v6/v7 são contratos históricos
imutáveis. SQLite mantém schema1 e exporta os bytes armazenados. Nomes C#
internos anteriores permanecem para limitar ruptura desnecessária de fonte.
O provedor fica opt-in, com gates exatos e falhas sem números; não aumenta
a cobertura de sensores públicos nem promove cooler privado.

## Validação

[Critério, autorização e diagnóstico](../research/2026-09-06-v011-stabilization.md).
[Teste do pacote e conclusão](../research/2026-09-06-v011-stable-validation.md).
Esta decisão substitui a classificação de entrega da ADR0013, preservando
seus mecanismos de supervisão, persistência e proveniência.
