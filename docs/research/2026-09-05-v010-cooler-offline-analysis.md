# v0.10 — análise offline do cooler da Galax RTX 3060 de 12 GB

Estado histórico do primeiro incremento em 2026-09-05: **implementado quando a v0.10 ainda estava em andamento**, com produto 0.9.0/ABI 7. A v0.9 foi integrada à `main` pela PR #11, commit `7c984d321667efccd6efb113402a191cf1fe04c3`. Este incremento acrescentou uma ferramenta offline; não acrescentou um sensor ao monitor ou serviço.

**Nota posterior:** o produto 0.10.0 foi concluído localmente com CI Windows/Linux nos snapshots registrados, três ciclos públicos, pacote e preservação aprovados. A captura privada v3 acrescentou dois preflights válidos; uma janela de 30 segundos foi rejeitada por −101 e a investigação permaneceu interrompida/parcial. Isso não altera retroativamente os bytes, timestamps ausentes ou ausência de selo original da evidência v2 analisada aqui. Nenhum campo privado foi promovido. [Conclusão e limites](2026-09-05-v010-completion.md).

Este registro trata da análise histórica. No incremento subsequente do mesmo dia, a fonte pública NVML de RPM foi integrada e validada na placa real, com cobertura 32/37. Seus testes, novas coletas e comparação com GPU-Z/HWiNFO estão no [relatório de RPM público](2026-09-05-public-fan-rpm.md). Isso não promove os campos privados analisados aqui.

## Comportamento entregue

`rtxmon-lab analyze-nvapi-cooler-status --input REPORT [--gpuz-log FILE]` aceita somente a observação cooler v2 do perfil fixo. Antes de calcular estatísticas, verifica identidade da unidade, driver/VBIOS, hashes e prova do módulo carregado, dois call sites, estrutura de 426 DWORDs, sequência, contagens e igualdade dos campos extraídos com o buffer completo. JSON duplicado, layout incompatível, erro de retorno ou inconsistência são recusados. Arquivos são limitados; o comando não abre GPU, driver, debugger ou serviço.

O relatório separa call site, posição de entrada e offset bruto. Preserva identificadores sem nomeá-los como fan index, e calcula mínimo/máximo, primeiro/último valor, valores distintos e transições em uint32. `mapping_status` e `evidence_stage` continuam `raw_unknown`; a ausência de timestamps individuais aparece como `unavailable_sequence_only`.

Com `--gpuz-log`, a ferramenta lê o prefixo de tamanho registrado na captura (até 16 MiB) e separa sessões anexadas, incluindo mudanças de cabeçalho. Exige uma única janela contendo os marcos antes/meio/depois e terminada no último timestamp registrado. Duplicatas/ordem inválida são verificadas dentro dessa janela: o histórico anterior da mesma sessão não é usado para julgar as dez linhas selecionadas. Somente faixas de canais exatos de fan são exibidas. Valores ausentes permanecem ausentes.

O hash do prefixo é calculado durante a análise e marcado `unsealed_historical_reference`. O contrato v2 não contém um selo original nem tempos individuais; logo não há pareamento, coeficiente de correlação, erro por amostra ou promoção. As referências de software não estabelecem posição física nem comprovam PWM elétrico.

## Aplicação à evidência histórica

Esta análise offline não realizou nova coleta física. Foi analisado o arquivo de 388.089 bytes `evidence/v08-final-20260827-190125/cooler-passive-v2-profiled/nvapi-cooler-status-v1-observation-v2.json`, SHA-256 `7b9751f49849756c22949423d53604174a85543dc9263eebd5d9c32d99789a4b`.

O resultado está em `evidence/v010-offline-20260905/cooler-analysis.json`: 36 amostras, 18 por call site e 16 grupos brutos. O prefixo GPU-Z tem 15.504.728 bytes e SHA-256 calculado agora `a9ae964a9627dcd290204337250963aa1d26b7eca3f7d4e8fe9feb04bd0b480d`. A sessão selecionada é 2, com dez linhas entre 2026-08-27 19:41:38 e 19:41:50 (horário local preservado).

| Posição no buffer | Faixa agregada dos dois call sites | Interpretação atual |
| --- | ---: | --- |
| Entrada 0, campo `+4` | 1.094–1.104 | Hipótese compatível com RPM, ainda sem unidade/associação validada |
| Entrada 1, campo `+4` | 1.094–1.111 | Mesma hipótese; associação invertida continua possível |
| Campos `+8` e `+16`, ambas as entradas | 30, constantes | Não distinguem percentual, alvo, limite ou configuração |
| Campo `+12`, ambas as entradas | 100, constante | Sem significado atribuído |

O GPU-Z exibe Fan 1 em 1.097–1.104 RPM e Fan 2 em 1.094–1.106 RPM, ambas a 30%. A semelhança de faixas sustenta uma hipótese a testar, sem provar origem ou alinhamento individual. HWiNFO não foi pareado a essa janela histórica: o novo comando não oferece importação de fan HWiNFO.

## Reprodução

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- analyze-nvapi-cooler-status `
  --input .\evidence\v08-final-20260827-190125\cooler-passive-v2-profiled\nvapi-cooler-status-v1-observation-v2.json `
  --gpuz-log ".\evidence\GPU-Z Sensor Log.txt"

dotnet run --project .\csharp\RtxMonitor.Lab.Tests -c Release
.\scripts\verify-ci.ps1 -Configuration Release
```

Os artefatos em `evidence/` permanecem locais. O CI usa a fixture sintética existente e mutações de contrato; esses testes não constituem evidência física.

## Validação e pendências

A suíte Lab em Release e a aplicação ao histórico passaram; a saída real passou no [schema v1](../schema/nvapi-cooler-status-analysis-v1.schema.json). Uma revisão independente refez os 16 grupos a partir dos buffers e confirmou todas as estatísticas.

`scripts/verify-ci.ps1 -Configuration Release` passou: 32 testes CTest, 14 testes da auditoria de perfil, suítes Managed/Storage/Console/Service/Lab, schemas e formatação. O Console mantém seus 35 cenários sem hardware. O CI validou o novo JSON e recusou sua promoção para `externally_validated`. Log: `evidence/v010-offline-20260905/windows-ci.log`; hashes dos artefatos: `artifact-sha256.json` na mesma pasta. Nenhum teste físico de novos sensores foi realizado.

As novas suítes offline estão incluídas no fluxo Linux, mas **não foram reexecutadas em Linux nesta etapa**: o daemon Docker local estava indisponível. O CI remoto Windows/Linux da `main` passou após a integração da v0.9, no commit `7c984d3`; essa execução anterior não valida os arquivos novos da v0.10.

Ao fim da verificação, o serviço instalado respondeu `healthy`, versão 0.6.0, com o mesmo início `1788604360279`; GPU-Z, HWiNFO e o serviço mantiveram seus processos. O estado está registrado em `installed-service-health.json` na pasta de evidência. A análise offline não alterou a instalação nem os controles da GPU.

Para avançar na identificação dos campos, falta implementar captura com timestamps individuais e selagem contemporânea das referências, depois executar ciclos repetíveis. O [plano do próximo experimento](2026-09-05-v010-cooler-experiment-plan.md) registra hipóteses alternativas, critérios e limites. O coletor legado exige sessão elevada e não resolve sozinho a falta de timestamps. A atualização do serviço instalado continua uma implantação separada.
