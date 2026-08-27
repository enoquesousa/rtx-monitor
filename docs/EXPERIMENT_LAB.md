# Laboratório de engenharia reversa v0.8.0

Este documento descreve como obter evidência experimental sem confundir acesso de baixo nível com certeza sobre o hardware. O laboratório é opt-in, separado do serviço e inadequado para uma máquina da qual dependa trabalho importante.

## MVP executável atual

O primeiro componente implementado é `rtxmon-lab`, um empacotador e verificador de **um arquivo local fornecido pelo operador**. O MVP v0.8 desse CLI é restrito ao Windows para validar arquivos regulares, reparse points, volumes remotos e alternate data streams com as primitivas da plataforma. Ele não lê a GPU, não eleva privilégio e não afirma que o arquivo é VBIOS. O layout aceito é deliberadamente fechado:

```text
artifact-package/
├── manifest.json
└── artifact/
    └── payload.bin
```

Nenhum terceiro arquivo ou diretório é aceito. `manifest.json` segue exatamente [`artifact-package-manifest-v1.schema.json`](schema/artifact-package-manifest-v1.schema.json); o objeto embutido `artifact` segue [`raw-artifact-v1.schema.json`](schema/raw-artifact-v1.schema.json). A saída JSON de sucesso de `create` e `verify` segue [`evidence-package-v1.schema.json`](schema/evidence-package-v1.schema.json); erros estruturados em `stderr` seguem [`lab-command-error-v1.schema.json`](schema/lab-command-error-v1.schema.json).

O manifesto canônico v1 contém apenas:

- `schema_version: 1`;
- `source_kind: user_provided_local_file`;
- caminho fixo `artifact/payload.bin`, nome original, tamanho e SHA-256 minúsculo;
- GPU, driver e versão de VBIOS opcionais, como metadados declarados.

Criação e verificação:

```powershell
$created = dotnet run --project .\csharp\RtxMonitor.Lab -- create `
  --input C:\evidence-local\vbios.rom `
  --output C:\evidence-local\package-001 `
  --gpu "NVIDIA GeForce RTX 3060" `
  --driver-version "<versão observada>" `
  --vbios-version "<versão observada>" |
  ConvertFrom-Json

dotnet run --project .\csharp\RtxMonitor.Lab -- verify `
  --package C:\evidence-local\package-001 `
  --expected-manifest-sha256 $created.manifest_sha256
```

`create` recusa sobrescrever o destino, limita o payload a 256 MiB, copia e calcula o hash em streaming e grava um manifesto determinístico. Ele mantém payload e manifesto com handles exclusivos durante o staging e, depois do rename, reabre os dois de forma exclusiva para conferir identidade e SHA-256. Se essa validação pós-publicação falhar, o comando não retorna sucesso nem âncora e mantém o destino como não confiável; isso evita tentar apagá-lo por um pathname que um processo concorrente pode ter trocado. Ele não usa o atributo NTFS `ReadOnly`: esse atributo vale para todos os hardlinks do arquivo e não é uma fronteira de integridade. O valor `manifest_sha256` retornado deve ser preservado fora do pacote. `verify` exige esse hash esperado e rejeita propriedade duplicada, ausente ou extra, caminho diferente, layout extra, reparse point ou hardlink no pacote, tamanho divergente ou SHA-256 divergente.

Com um `manifest_sha256` obtido de uma fonte externa confiável, esse MVP torna alterações do arquivo e do manifesto detectáveis. Sem preservar o hash fora da pasta, o conteúdo é apenas autoconsistente: SHA-256 não prova sozinho a origem física declarada. O MVP não captura VBIOS e não identifica um sensor.

O CLI deve rodar como usuário comum em um diretório privado. A criação ainda combina validações por handle com `CreateDirectory`, `Directory.Move` e cleanup por pathname; um processo que já possa escrever no mesmo diretório mantém uma superfície TOCTOU residual. Antes de ligar o pacote a um helper privilegiado, essa fronteira precisa usar operações relativas a handles de diretório ou separar rigidamente o processo privilegiado do filesystem escolhido pelo operador.

### Parser de VBIOS offline

O segundo componente executável é `rtxmon-vbios`. No Windows, ele recebe o caminho fornecido pelo operador, limita a leitura a 16 MiB, valida a cadeia PCI ROM, os cabeçalhos `PCIR`, o checksum da imagem legacy e metadados da tabela NVIDIA `BIT` 1.00, e emite [`vbios-analysis-v1.schema.json`](schema/vbios-analysis-v1.schema.json). Caminho UNC/de dispositivo, stream alternativo, drive remoto e reparse point são rejeitados:

```powershell
.\build\windows-x64\bin\Release\rtxmon-vbios.exe `
  C:\evidence-local\package-001\artifact\payload.bin
```

Em outras plataformas, o CLI v0.8 retorna o diagnóstico `unsupported_platform` antes de validar ou abrir o caminho. A biblioteca C++ continua portátil: recebe somente um `span` de bytes já carregado pelo chamador e não realiza I/O.

O analisador usa offsets e somas verificados, segue `ImageLength`/`Indicator` sem procurar uma continuação arbitrária e busca a BIT somente na imagem legacy validada. Quando uma imagem UEFI `Code Type 03` segue a legacy, um ponteiro além da legacy recebe o ajuste de tamanho UEFI documentado pela NVIDIA; `validated_data_offset` só é marcado se todo o intervalo opaco couber no artefato. O analisador não interpreta o conteúdo, não altera o arquivo e não abre GPU, driver, PCI, MMIO ou I2C.

Uma BIT com header e checksum válidos, mas versão diferente de `0x0100`, é preservada com `version_supported=false`, `tokens=[]`, diagnóstico `bit_version_unsupported` e status `partial`. Assim, offset e versão continuam auditáveis sem assumir um layout de token desconhecido.

### Referência de telemetria do GPU-Z

O comando `analyze-gpuz-log` importa offline um log textual produzido pelo GPU-Z:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- analyze-gpuz-log `
  --input "C:\evidence-local\GPU-Z Sensor Log.txt" > gpuz-reference.json
```

A saída segue [`gpuz-reference-analysis-v1.schema.json`](schema/gpuz-reference-analysis-v1.schema.json). O importador limita a entrada a 16 MiB, aceita UTF-8 ou o fallback Latin-1 usado pelo arquivo observado, calcula SHA-256, exige cabeçalho `Date`, timestamps locais válidos e quantidade consistente de colunas. Se o GPU-Z anexar várias sessões ao mesmo arquivo, cabeçalhos repetidos só são aceitos quando o layout de canais é idêntico; a quantidade de sessões é registrada nos avisos. Todas as amostras brutas permanecem alinhadas ao catálogo de canais; estatísticas numéricas e a cadência mediana são derivadas sem substituir o conteúdo original.

Os escopos `gpu_board` e `host_system` evitam atribuir à placa campos que o GPU-Z agrega de outras fontes. O nome `Hot Spot` é preservado porque está no log, mas seu valor tem autoridade `external_reference`: o arquivo não revela qual chamada privada, firmware ou sensor o originou. Da mesma forma, `PerfCap Reason` permanece `raw_code`; o número do log não é convertido para o rótulo visual sem um contrato versionado.

### Marcadores sincronizados

O laboratório registra eventos de cenário como linhas JSON independentes:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- mark `
  --scenario idle.baseline --phase begin --note "ventoinhas estabilizadas" `
  >> experiment-markers.jsonl
```

Use `begin`, `note` e `end`. O contrato [`experiment-marker-v1.schema.json`](schema/experiment-marker-v1.schema.json) preserva UTC em milissegundos e tempo monotônico em nanossegundos, além da frequência original do relógio. O tempo monotônico ordena eventos produzidos no mesmo boot; o UTC permite alinhamento aproximado com ferramentas externas. Não converta o timestamp local do GPU-Z em UTC sem registrar explicitamente o fuso usado.

### Correlação inicial do log externo

Depois da captura, compare um canal de referência com os demais canais numéricos:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- correlate-gpuz-log `
  --input "C:\evidence-local\GPU-Z Sensor Log.txt" `
  --reference "Hot Spot" --session 0 > gpuz-hotspot-correlation.json
```

O relatório segue [`gpuz-correlation-v1.schema.json`](schema/gpuz-correlation-v1.schema.json). O método atual é Pearson com defasagem zero e exige pelo menos três pares; séries constantes ou curtas recebem um status explícito e coeficiente `null`. Use `--session INDEX` para isolar uma sessão anexada; sem essa opção, o relatório combina todas e emite um aviso. Canais do host continuam presentes com `source_scope=host_system`, pois uma correlação alta causada pela carga compartilhada não prova origem na placa. O coeficiente orienta o próximo estímulo controlado, mas não atribui identidade física.

### Protocolo térmico RM offline

O snapshot oficial NVIDIA 610.57.04 contém `NV2080_CTRL_CMD_THERMAL_SYSTEM_EXECUTE_V2` e sua variante física non-privileged. O protocolo enumera sensores e permite consultar índice de provedor, índice de alvo, tipos, faixa e valor atual. A v0.8 representa esse contrato em C++ com tamanhos verificados e factories fechadas para os opcodes conhecidos.

Essa implementação ainda é **somente protocolo**. O código não conhece um transporte WDDM documentado, não cria handles RM, não chama o comando físico e não envia o buffer ao driver instalado 610.88. O transporte Linux `NV_ESC_RM_CONTROL` observado no repositório não deve ser transplantado para Windows. A diferença entre fonte 610.57.04 e driver 610.88 também impede afirmar compatibilidade binária apenas porque os números parecem estáveis.

### Observação controlada de IDs NVAPI

`capture-gpuz-nvapi-ids.ps1` é uma ferramenta de bancada Windows, não parte do monitor. Ela exige Administrador porque inicia o GPU-Z sob o WinDbg e precisa remover com segurança o serviço temporário criado pelo próprio GPU-Z. O script valida as assinaturas, recusa processos conflitantes, limita a duração e remove apenas o serviço/arquivo exatos cujo hash e assinatura correspondam ao helper já auditado.

Por padrão, o debugger fica oculto para uma captura de startup. `-InteractiveTarget` apenas torna as janelas visíveis para diagnóstico; nesta máquina, iniciar o alvo já depurado permaneceu no splash e não alcançou `Sensors`. Portanto, esse script caracteriza startup e não deve ser usado como prova de polling da aba.

A captura produz três camadas: IDs consultados conforme [`nvapi-query-observation-v1.schema.json`](schema/nvapi-query-observation-v1.schema.json), endereço resolvido normalizado por módulo/hash/RVA conforme [`nvapi-interface-resolution-v1.schema.json`](schema/nvapi-interface-resolution-v1.schema.json) e entradas de função realmente observadas conforme [`nvapi-call-observation-v1.schema.json`](schema/nvapi-call-observation-v1.schema.json). Os breakpoints apenas observam o caminho que o GPU-Z já executa; o script não invoca um ID privado e não lê argumentos ou buffers.

Para cruzar os IDs com um snapshot público já baixado:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- classify-nvapi-ids `
  --input C:\evidence-local\nvapi-query-report.json `
  --interface-table C:\fontes\nvapi\nvapi_interface.h
```

O relatório segue [`nvapi-interface-classification-v1.schema.json`](schema/nvapi-interface-classification-v1.schema.json). O classificador não usa rede, não carrega DLL NVIDIA e não chama um ID observado. Um resultado `not_in_public_catalog` continua sem semântica atribuída.

Depois, gere um inventário que una catálogo, execução e origem binária:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- inventory-nvapi-candidates `
  --classification C:\evidence-local\nvapi-classification.json `
  --calls C:\evidence-local\nvapi-call-report.json
```

A saída segue [`nvapi-candidate-inventory-v1.schema.json`](schema/nvapi-candidate-inventory-v1.schema.json). `executed_entry` comprova entrada no endereço durante a janela; `public_symbol_only` ainda não vincula a função a um campo do GPU-Z; `unidentified_binary_candidate` mantém o alvo sem nome até existir evidência semântica.

### Anexo tardio aos candidatos NVAPI

Com `Sensors` aberto e o log comprovadamente crescendo, observe quais endereços do inventário participam do polling:

```powershell
.\scripts\capture-gpuz-nvapi-candidate-calls.ps1 `
  -GpuzProcessId 1234 `
  -CandidateInventoryPath C:\evidence-local\nvapi-candidates.json `
  -DurationSeconds 10
```

O coletor valida o executável TechPowerUp, o CDB x86 Microsoft, o inventário e os módulos NVAPI assinados contra seus hashes. Os breakpoints registram entrada, thread e endereço de retorno normalizado por módulo/hash/RVA. Eles não chamam NVAPI, não alteram argumentos e não leem retorno.

Na captura válida desta RTX 3060, 19 dos 100 alvos foram executados 465 vezes durante dez segundos de polling: oito públicos e 11 ausentes do catálogo. `NvAPI_GPU_GetThermalSettings` recebeu zero chamadas nessa janela. No `nvapi_impl.dll` fixado por SHA-256, o candidato `0x65fe3aad` (RVA `0x001ad310`) contém referências diretas às mensagens de versão de `NvAPI_GPU_ThermChannelGetStatus`. Há também referências diretas de `0x465f9bcf` a estruturas de rails/políticas de tensão e de `0x35aed5e8` a estruturas de fan/cooler. Isso atribui famílias funcionais, não campos nem sensores físicos.

Depois de uma primeira observação válida, reduza o conjunto aos alvos privados que realmente executaram e capture apenas seis palavras de 32 bits na entrada — `ECX`, `EDX` e quatro posições da pilha — sem dereferenciar qualquer ponteiro:

```powershell
.\scripts\capture-gpuz-nvapi-candidate-calls.ps1 `
  -GpuzProcessId 1234 `
  -CandidateInventoryPath C:\evidence-local\nvapi-candidates.json `
  -TargetScope ObservedUnidentified `
  -PriorObservationPath C:\evidence-local\nvapi-candidate-call-report.json `
  -CaptureInputWords `
  -DurationSeconds 10
```

O relatório segue [`nvapi-candidate-call-observation-v1.schema.json`](schema/nvapi-candidate-call-observation-v1.schema.json) e ancora por hash o inventário e a observação anterior. Confirme crescimento do log antes e durante cada repetição. O primeiro detach desta máquina manteve a GUI responsiva, mas interrompeu o worker de logging; uma captura posterior feita sem novas linhas foi classificada como atividade de fundo e excluída. Uma sessão nova restaurou o polling e comprovou crescimento antes, no midpoint e depois da observação.

### Perfil térmico v2 allowlisted

Depois que análise estática e disassembly do call site provaram ID, RVA, dois argumentos, direção, versão e tamanho, o perfil térmico ganhou um coletor específico:

```powershell
.\scripts\capture-gpuz-nvapi-therm-channel-v2.ps1 `
  -GpuzProcessId 1234 `
  -CandidateInventoryPath C:\evidence-local\nvapi-candidates.json `
  -PriorObservationPath C:\evidence-local\nvapi-polling-report.json `
  -GpuzLogPath "C:\evidence-local\GPU-Z Sensor Log.txt" `
  -OutputDirectory .\evidence\thermal-v2 `
  -DurationSeconds 10
```

O script aceita somente GPU-Z, CDB x86, `nvapi_impl.dll`, interface `0x65fe3aad`, função RVA `0x001ad310`, call site GPU-Z RVA `0x002225b5` e estrutura `0x000200a8` fixados no código e por SHA-256. Ele coloca o breakpoint depois do retorno, exige status zero e lê exatamente os 42 DWORDs já inicializados pelo GPU-Z. Não chama NVAPI, não altera a estrutura e desconecta com `qqd`.

A ABI observada usa a máscara `1 << channel`. O canal 0 está na palavra 10 (`offset 0x28`) e o canal 1 na palavra 11 (`offset 0x2c`); ambos são inteiros com sinal em ponto fixo 8 e usam `raw / 256` °C. O relatório atual segue [`nvapi-therm-channel-v2-observation-v2.schema.json`](schema/nvapi-therm-channel-v2-observation-v2.schema.json), comprova por `lmv` o módulo carregado e sela no diretório da captura o prefixo LF-completo do log. O contrato v1 permanece histórico.

Faça a associação offline sem manter o debugger ou o GPU-Z anexado:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- `
  correlate-nvapi-therm-channel-v2 `
  --observation .\evidence\thermal-v2\nvapi-therm-channel-v2-observation-v2.json `
  --gpuz-log .\evidence\thermal-v2\sealed-gpuz-thermal-reference.csv
```

O analisador v2 verifica nome/tamanho/hash e localização do prefixo selado, ignora sessões sem o par exato de canais e escolhe a janela somente pelas fronteiras before/midpoint/after. Erros térmicos não participam da seleção. Ele compara a associação direta e a invertida e emite [`nvapi-therm-channel-correlation-v2.schema.json`](schema/nvapi-therm-channel-correlation-v2.schema.json). O analisador v1 e seus resultados anteriores permanecem históricos.

O estado `matched_external_reference` vale para o perfil binário exato. Ele não declara uma ABI pública da NVIDIA, não identifica a construção física do sensor e não habilita esse valor no monitor estável.

### Tensão v1 e cooler bruto

[`capture-gpuz-nvapi-voltage-status-v1.ps1`](../scripts/capture-gpuz-nvapi-voltage-status-v1.ps1) fixa `0x465f9bcf`, RVA x86 `0x00198010`, call site GPU-Z `0x0021cee7`, estrutura `0x0001004c` de 76 bytes e exatamente 19 DWORDs. Identidade completa da GPU, VBIOS, driver, binários e evidências anteriores são comparados antes do anexo. GPU-Z é obrigatório; HWiNFO só é incluído quando um CSV corrente cresce antes, no meio e depois da janela. O correlator `correlate-nvapi-voltage-status-v2` preserva o hash do prefixo inteiro e separa sessões GPU-Z com headers diferentes.

Na sessão final de 2026-08-27, 20 retornos produziram `956250 µV`; o GPU-Z apresentou `0,9560 V` em 20 pares, erro máximo `0,00025 V`. A referência passou a tolerância de arredondamento. Como a janela continha um único patamar, seu `mapping_status` permaneceu `ambiguous_or_outside_tolerance`; a evidência multipatamar anterior continua separada e ancorada. O HWiNFO estava em execução, mas seu CSV não crescia e foi registrado como `null`.

[`capture-gpuz-nvapi-cooler-status-v1.ps1`](../scripts/capture-gpuz-nvapi-cooler-status-v1.ps1) fixa `0x35aed5e8`, RVA x86 `0x001b9f10`, dois call sites e estrutura `0x000106a8` de 1.704 bytes. O relatório v2 exige identidade exata de GPU/PCI/subsystem/VBIOS/driver, hashes fixos do inventário e da observação anterior e prova `ModLoad` da imagem `nvapi_impl.dll` realmente carregada, incluindo hash, range e RVA. Ele preserva os 426 DWORDs completos e quatro campos por entrada como `raw_field_words`. A captura final obteve 36 retornos, 18 por site, com duas entradas; nenhum campo foi nomeado como RPM, PWM, fan index ou controle. O contrato v1 permanece histórico.

As leituras diretas `--thermal-watch` e `--voltage-watch` são uma superfície separada, opt-in e fixa ao perfil. Elas verificam PCI/subsystem, VBIOS, driver, módulo x64, SHA-256, RVA, estrutura e limites antes de produzir valor; não entram no serviço, SQLite, HTTP/SSE ou telemetria estável. Veja o [ADR 0010](adr/0010-fixed-profile-private-nvapi-acquisition.md).

### Anexo tardio ao canal do helper

Para observar polling, abra o GPU-Z normalmente, selecione `Sensors`, ative o log e só então execute, em PowerShell elevado:

```powershell
.\scripts\capture-gpuz-device-io-control.ps1 `
  -GpuzProcessId 1234 `
  -DurationSeconds 10 `
  -ObservedApi DeviceIoControl
```

O script localiza o CDB x86 dentro do pacote WinDbg, valida sua assinatura Microsoft e valida a assinatura TechPowerUp do processo-alvo. Ele anexa sem reiniciar o GPU-Z, grava somente metadados e entradas delimitadas de 4 ou 12 bytes, nunca lê o buffer de saída e desconecta com `qqd`. A repetição com `-ObservedApi NtDeviceIoControlFile` verifica se existe chamada nativa que contorne a API Win32.

Os artefatos seguem [`gpuz-device-io-control-observation-v1.schema.json`](schema/gpuz-device-io-control-observation-v1.schema.json) e [`gpuz-device-io-control-input-v1.schema.json`](schema/gpuz-device-io-control-input-v1.schema.json). O segundo arquivo referencia por SHA-256 o primeiro relatório e o transcript do debugger; uma divergência entre chamadas de 4/12 bytes e entradas registradas faz o script falhar.

Para provar a identidade de um handle sem aceitar licença de outra ferramenta nem consultar o kernel com um driver novo:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- `
  resolve-windows-handle --process-id 1234 --handle 0x368
```

O comando usa APIs nativas somente leitura e emite [`windows-handle-identity-v1.schema.json`](schema/windows-handle-identity-v1.schema.json). Na coleta real, o objeto foi `\Device\GPU-Z-v8`, alias `\\.\GPU-Z-v8`.

Os únicos códigos observados com `Sensors` ativo foram `0x80006040`, que o binário encaminha à instrução `RDMSR` e recebeu seletor Intel `0x19c` (`IA32_THERM_STATUS`), e `0x800060c0`, que o binário encaminha a `HalGetBusDataByOffset` para bytes da configuração PCI da RTX. Isso identifica CPU térmica e configuração PCI, não o hotspot da GPU. A análise detalhada, hashes e RVAs estão em [Caminhos de runtime do GPU-Z](research/2026-08-25-gpuz-runtime-paths.md).

O helper proprietário do GPU-Z não pode ser reutilizado. A análise estática encontrou caminhos genéricos de baixo nível, inclusive escrita; reproduzir seus IOCTLs violaria a fronteira somente leitura e criaria dependência de um ABI privado sem contrato.

Os códigos de saída são: `0` para uma ROM válida, `1` para arquivo analisado sem ROM válida, `2` para uso incorreto e `3` para plataforma não suportada, falha de abertura/leitura/tamanho ou limite de recurso. Essas falhas produzem diagnóstico JSON v1 quando a análise foi iniciada.

## Manifesto e análise implementados

`finalize-experiment-manifest` valida e emite [`experiment-manifest-v1.schema.json`](schema/experiment-manifest-v1.schema.json). Cada pacote é verificado novamente pelo SHA-256 externo de seu `manifest.json` e declara `scenario_id`: o valor identifica um cenário existente ou é `null` somente para material auxiliar/histórico. Caminhos duplicados, hashes duplicados, traversal, cenário inexistente, marcador incoerente, identidade incompleta, experimento concluído sem pacote ou `privileged_capture` sem allowlist falham fechados.

`analyze-experiment-series` exige o hash externo do manifesto, revalida e lê o pacote solicitado pelo mesmo handle usado para validar tamanho, identidade e SHA-256, e consome esses bytes já ancorados conforme [`numeric-series-v1.schema.json`](schema/numeric-series-v1.schema.json). O pacote selecionado precisa de `scenario_id` não nulo, e todas as amostras `monotonic_ns` precisam estar entre os markers `begin` e `end` desse cenário. A saída [`analysis-report-v1.schema.json`](schema/analysis-report-v1.schema.json) preserva `value_unit`, estatísticas, deltas, período de atualização e correlação com lag. O custo da correlação é limitado a 10.000.000 de pares, e o analisador nunca promove automaticamente o candidato além de `raw_unknown`.

```powershell
rtxmon-lab finalize-experiment-manifest `
  --input experiment-draft.json `
  --package-root C:\evidence-local > experiment-manifest.json

$sha = (Get-FileHash experiment-manifest.json -Algorithm SHA256).Hash.ToLowerInvariant()

rtxmon-lab analyze-experiment-series `
  --manifest experiment-manifest.json `
  --expected-manifest-sha256 $sha `
  --package-root C:\evidence-local `
  --series-package series-package `
  --max-lag-samples 2 > analysis-report.json
```

## Matriz de privilégios

| Operação | Windows: usuário comum | Windows: Administrador | Windows: KMDF assinado e HVCI | Linux / offline |
|---|---|---|---|---|
| NVML/NVAPI e telemetria estável | Permitida | Desnecessário elevar | Não participa | NVML e `hwmon` conforme permissões |
| Criar o pacote MVP e hashes | Permitida | Desnecessário elevar | Não participa | CLI `rtxmon-lab` ainda não suportado; hashing offline equivalente permanece possível |
| Analisar VBIOS já fornecida | Permitida, sem executar o arquivo | Desnecessário elevar | Não participa | Biblioteca pura permitida; CLI v0.8 retorna `unsupported_platform` antes do path |
| Instalar/iniciar helper Windows | Negada | Permitida somente para pacote aprovado | Driver precisa estar assinado e carregar com HVCI ativo | Não se aplica |
| Ler PCI config allowlisted | Sem acesso direto | Pode abrir o device object restrito | Driver valida identidade, operação, offset e largura | Permissão elevada pode ser necessária; usar coletor equivalente e nunca escrever |
| Ler BAR0 allowlisted | Sem acesso direto | Pode solicitar uma operação conhecida | Driver lê apenas o offset pontual e copia bytes limitados | Somente helper/perfil equivalente em máquina de laboratório |
| Varredura, escrita, BAR1, VRAM ou ROM da placa | Proibida | Proibida | Não existe IOCTL para isso | Proibida mesmo como root |
| Analisador de séries/candidatos | Permitida | Desnecessário elevar | Não participa | CLI v0.8 ainda não suportado: a verificação ancorada do pacote depende da identidade de arquivo por handle implementada no Windows |

Elevação de usuário e privilégio de kernel são fronteiras diferentes. Ser Administrador permite gerenciar um driver aprovado; não concede uma API legítima para mapear BARs e não autoriza endereços fora da allowlist.

## Pacote de experimento

O experimento completo não altera um pacote MVP. Ele mantém `experiment-manifest.json` ao lado de diretórios de pacotes fechados por contrato e referencia cada um por caminho relativo, SHA-256 do respectivo `manifest.json` e `scenario_id`. Pacotes que pertencem à janela medida apontam para um cenário declarado; material apenas auxiliar ou histórico usa `null` e não pode ser selecionado como série pelo analisador. A análise é derivada e referencia os mesmos hashes; não reescreve o payload nem o manifesto original.

Na v0.8, `finalize-experiment-manifest` e `analyze-experiment-series` herdam a verificação Windows-only de `LabPackage`. Torná-los portáveis exige provar arquivo regular, identidade, link count e leitura/hash pelo mesmo handle em cada ABI Unix suportada; o CLI não reduz essa garantia silenciosamente fora do Windows.

Todos os caminhos registrados são relativos, usam `/`, não contêm `..`, unidade, UNC ou symlink. O manifesto de experimento também recebe SHA-256 antes da análise, e o relatório registra esse hash como `input_experiment_manifest_sha256`.

## Identidade obrigatória

O manifesto fixa o alvo antes da coleta:

- UUID e nome da GPU;
- endereço e IDs PCI `vendor:device/subvendor:subdevice`;
- revisão PCI;
- versão de VBIOS e SHA-256 da imagem offline, quando fornecida;
- driver, NVML e GSP quando observável;
- sistema operacional, kernel/build e arquitetura;
- versão do RTX Monitor, coordenador, helper e analisador;
- `profile_key` e `allowlist_id` usados.

Um campo não observável recebe `null`; não use `unknown` como versão e não copie um dado de execução anterior.

## Duas bases de tempo

Cada marcador registra:

- `utc_unix_ms`, para ordenar e auditar entre processos e máquinas;
- `monotonic_ns`, para deltas, intervalos e correlação dentro da execução.

O manifesto registra as fontes dos dois relógios e a frequência nominal do monotônico. Mudança no relógio civil não altera deltas monotônicos. Dados de máquinas diferentes só podem ser correlacionados depois de documentar sincronização, erro e drift.

## Cenários

Os tipos de cenário disponíveis são:

- `idle`: repouso com carga e clocks estabilizados;
- `graphics_load`: carga predominantemente gráfica;
- `memory_load`: carga predominantemente de memória;
- `cooling`: carga encerrada e curva de resfriamento;
- `custom`: estímulo adicional descrito sem ambiguidade.

Cada cenário registra os comandos escolhidos pelo coordenador e usa marcadores `begin`, `end` ou `note`; no contrato v1, a duração efetiva é derivada do par monotônico `begin`/`end`, não de um campo de duração prevista. O laboratório não inicia uma carga arbitrária a partir de texto recebido pelo helper; comandos pertencem ao coordenador sem privilégio e aparecem no log.

## Fluxo da execução completa

### 1. Preparar o ambiente

- Use máquina física ou bancada em que uma falha não interrompa trabalho importante.
- Salve dados do sistema e confirme um caminho de recuperação do Windows/Linux.
- Mantenha HVCI ativo durante os testes Windows; não desabilite uma proteção para fazer um driver carregar.
- Confirme que o repositório está limpo e que dumps locais estão ignorados pelo Git.

### 2. Criar o manifesto de experimento

Capture identidade e ambiente atuais. Defina cenários e as operações pelo `operation_id` já existente no perfil. O manifesto pode reduzir `sample_count` ou aumentar `interval_ms`, mas não inventar offset, largura ou espaço.

### 3. Capturar linha de base pública

Colete capabilities, telemetria pública e `nvidia-smi` somente como referência cruzada. Preserve estados `not_supported`; ausência não é zero.

### 4. Ingerir VBIOS offline, quando aplicável

Copie para uma pasta local ignorada um arquivo que o operador já possua e use `rtxmon-lab create`. O pacote registra `source_kind: user_provided_local_file`; o papel `vbios_offline` pertence ao manifesto de experimento, não ao manifesto MVP.

Não use `/sys/.../rom`, ferramentas de flash ou escrita para produzir esse arquivo como parte da v0.8.0.

### 5. Revisar a hipótese

Uma solicitação privilegiada precisa declarar:

- por que aquele offset é relevante;
- documentação, código aberto ou comparação que fundamenta a hipótese;
- espaço `pci_config` ou `bar0_mmio`;
- offset, largura, alinhamento e endianess;
- risco conhecido de efeito colateral;
- limite de amostras, intervalo mínimo e timeout;
- resultado esperado que poderia refutar a hipótese.

### 6. Executar aquisição allowlisted, se necessária

As aquisições NVAPI em user mode da v0.8 usam perfis compilados e não exigem helper. Se uma hipótese futura exigir PCI config/BAR0 em kernel mode, o coordenador abrirá uma sessão por identidade, nunca por posição transitória; o helper deverá resolver `operation_id` em sua própria allowlist, verificar o perfil e recusar divergências. A v0.8 concluída não executou essa classe de leitura.

### 7. Verificar e indexar evidências

Cada payload passa por `rtxmon-lab create` e depois por `rtxmon-lab verify`. O manifesto de experimento registra o papel do artefato, o vínculo de cenário ou `null` auxiliar, o caminho relativo do pacote e o SHA-256 do `manifest.json`. Hash divergente encerra a operação; o analisador nunca “repara” evidência nem reabre por pathname os bytes já verificados.

### 8. Analisar offline

O analisador recebe o manifesto e seu SHA-256 externo, a raiz de pacotes, o caminho relativo do pacote de série e o lag máximo. Ele revalida a âncora, exige vínculo a um cenário com janela completa, calcula séries, deltas, período de atualização, lag e correlação dentro do limite defensivo, preserva unidade, parâmetros e versão, e mantém o candidato em `raw_unknown`. Promoção de estágio exige outro fluxo revisado e evidência adicional; este comando não a executa.

## Referência térmica externa

Termopar ou câmera térmica mede o ponto físico observado, não a junção interna automaticamente. Registre:

- fabricante, modelo, resolução e calibração declarada;
- posição e método de fixação;
- emissividade, distância e ângulo, quando aplicável;
- temperatura ambiente e fluxo de ar;
- frequência de amostragem, sincronização e incerteza;
- ciclos de aquecimento/resfriamento e limitações.

Uma fotografia ou desenho da posição pode ser um artefato, desde que não contenha dados pessoais e receba hash como os demais.

## Regras para VBIOS e firmware

- Nunca faça commit de `.rom`, `.bin`, `.dump` ou pacotes de evidência locais.
- Publique, quando permitido, apenas descritor, versão, tamanho, hash e método de obtenção declarado.
- Não presuma compatibilidade do GSP entre versões do driver.
- Não execute, faça flash, modifique ou redistribua firmware proprietário.
- Um parser deve impor limite de tamanho, usar offsets verificados e tratar o arquivo como entrada hostil.

## Critérios de interrupção

Interrompa e marque a execução como `aborted` quando ocorrer:

- identidade diferente do perfil;
- helper, assinatura ou HVCI em estado inesperado;
- operação ausente/revogada;
- hash divergente;
- timeout, leitura curta ou volume acima do limite;
- reset do driver, desaparecimento da GPU ou erro PCI;
- comportamento térmico/elétrico inesperado;
- necessidade de escrever, habilitar ROM, acessar BAR1/VRAM ou ampliar um intervalo.

## Referências

- [ADR 0008](adr/0008-reproducible-reverse-engineering-lab.md)
- [Threat model do laboratório](security/experimental-acquisition-threat-model.md)
- [Roadmap de engenharia](ROADMAP.md)
- [Microsoft: segurança de drivers](https://learn.microsoft.com/windows-hardware/drivers/driversecurity/windows-security-model)
- [Microsoft: compatibilidade com HVCI](https://learn.microsoft.com/windows-hardware/test/hlk/testref/driver-compatibility-with-device-guard)
- [Linux kernel: PCI por `sysfs`](https://docs.kernel.org/PCI/sysfs-pci.html)
- [NVIDIA: BIOS Information Table](https://nvidia.github.io/open-gpu-doc/BIOS-Information-Table/BIOS-Information-Table.html)
- [UEFI 2.10: PCI Bus Support](https://uefi.org/specs/UEFI/2.10/14_Protocols_PCI_Bus_Support.html)
- [PCI-SIG: PCI Firmware](https://pcisig.com/specification-overview/pci-firmware)
- [NVIDIA Open GPU Kernel Modules](https://github.com/NVIDIA/open-gpu-kernel-modules)
