# Auditoria do perfil da RTX 3060 GALAX 12 GB

Este diretório registra a única placa alvo do projeto. O nome comercial **GALAX RTX 3060 12 GB** foi informado pelo usuário; ele não foi deduzido do fabricante dos IDs PCI. A autorização de aquisição continua baseada no UUID físico e na combinação exata de PCI/subsystem, VBIOS, driver, módulo, operação e layout compilados.

`rtx3060-galax-12gb.json` é um registro offline de revisão. O monitor não lê esse arquivo, e modificá-lo não amplia a allowlist em execução. Ele contém o snapshot esperado do catálogo, hashes das fontes que implementam a política, referências documentais da evidência e um registro explícito da revisão. GSP permanece `not_observed`, com versão `null`; este registro não prova compatibilidade com outra versão de firmware.

## Verificação reproduzível

O executável de teste `rtxmon_private_catalog_snapshot` liga somente `private_profile_catalog.c` e inclui os headers de layout. Ele não liga NVML, NVAPI, loaders ou a DLL do monitor, e não abre a GPU. Seu JSON registra os pins compilados, tamanhos, offsets, larguras e limites. O mesmo snapshot pode ser produzido em um build x64 Linux: isso comprova o catálogo/ABI offline, sem tornar a aquisição NVAPI disponível naquele sistema.

Depois de compilar o alvo de auditoria, salve sua saída em um arquivo e execute:

```powershell
& .\build\windows-x64\bin\Release\rtxmon_private_catalog_snapshot.exe |
    Set-Content -Encoding utf8 .\build\private-catalog-snapshot.json
python .\scripts\verify-private-profile.py --snapshot .\build\private-catalog-snapshot.json
python -B -m unittest discover -s scripts/tests -p test_private_profile_audit.py
```

O verificador usa apenas a biblioteca padrão do Python. Ele compara integralmente o JSON compilado com o snapshot esperado e verifica cada arquivo por SHA-256. Campos ausentes/extras, tipo alterado, perfil, RVA, largura, taxa, revisão ou hash divergentes fazem o comando terminar com código 1. Não há opção de aceitar ou atualizar automaticamente a baseline. O programa aceita `--root` e `--manifest` para testes/revisão offline; esses argumentos não são opções do monitor.

## Hashes e revisão

Os hashes de arquivos são SHA-256 dos bytes após substituir exclusivamente CRLF por LF. BOM, espaços e demais bytes continuam relevantes. Isso permite o mesmo checkout em Windows/Linux sem esconder alterações de conteúdo. Os hashes dos conjuntos e do snapshot usam JSON canônico: chaves ordenadas, separadores `,`/`:`, UTF-8 sem BOM, sem escapes ASCII adicionais e sem newline final.

O último registro de `revision_history` deve corresponder à revisão compilada e ancorar o snapshot, a lista de fontes, a lista de evidências e a lista de fixtures. Alterar somente um hash de arquivo ou somente os pins esperados é insuficiente: o respectivo hash do registro também precisa ser atualizado explicitamente. Na primeira adoção dessa auditoria, a revisão 2 existente é a primeira revisão registrada; os ADRs preservam a história anterior sem inventar digests históricos.

Uma alteração semântica da allowlist exige nova revisão do catálogo, revisão da evidência e um novo registro. Mudanças apenas de testes/documentação que sejam intencionalmente reancoradas devem ter sua razão descrita no registro revisado. A revisão de código continua necessária: hashes locais detectam drift e atualizações incompletas, não substituem assinatura, autorização humana ou prova física. Um autor capaz de alterar todos os arquivos também pode reescrever os pins; o diff completo precisa ser revisado.

## Natureza da evidência

As referências documentais fixadas por hash descrevem observações históricas reais e seus limites. Elas não são cópias dos binários NVIDIA, da VBIOS ou de outros artefatos proprietários. Os pacotes brutos originais permanecem locais, nos caminhos registrados nos relatórios. Fixar o hash de um relatório prova sua integridade textual; não revalida o experimento físico descrito nele.

Fixtures devem declarar a proveniência de cada vetor. Vetores sintéticos testam gates, layout e decodificação; valores transcritos/normalizados de uma observação precisam apontar para a evidência original e registrar a transformação. Nenhuma das categorias valida uma placa, driver, VBIOS ou GSP diferente. A auditoria de fixtures é offline e nunca promove estágio de evidência.
