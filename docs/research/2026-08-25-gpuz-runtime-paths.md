# Caminhos de runtime do GPU-Z — RTX 3060 GA106

- Data: 2026-08-25
- GPU-Z: 2.70.0, assinatura Authenticode válida de TechPowerUp LLC
- Sistema: Windows, driver NVIDIA 610.88
- Estado no momento da pesquisa: caminhos e IDs observados; nenhuma interface privada havia sido chamada pelo projeto

## Resultado

O GPU-Z usa duas superfícies na máquina analisada:

1. carrega `nvapi.dll` e `nvapi_impl.dll` do driver NVIDIA;
2. extrai e inicia temporariamente seu próprio helper de kernel `GPU-Z-v8.sys`.

Uma captura controlada de `nvapi_QueryInterface` registrou 100 IDs distintos. Todos retornaram um endereço executável: 99 em `nvapi_impl.dll` e `NvAPI_Initialize` no wrapper `nvapi.dll`. O cruzamento offline com o arquivo oficial `nvapi_interface.h` encontrou 43 IDs no catálogo público e 57 ausentes desse catálogo.

Durante uma janela de 10 segundos, 33 dos 100 endereços foram realmente executados, somando 150 entradas observadas. Desses 33, 14 têm nome no catálogo público e 19 permanecem sem nome. `NvAPI_GPU_GetThermalSettings` foi executada quatro vezes, mas isso ainda não demonstra qual chamada ou campo alimentou `Hot Spot`.

Uma segunda janela de 30 segundos, sem interação com a interface, repetiu exatamente os mesmos 33 alvos e as mesmas 150 entradas. A contagem não cresceu com o tempo. Portanto, a evidência atual descreve inicialização/abertura do GPU-Z, não polling contínuo da aba de sensores. Esse resultado negativo evita priorizar incorretamente `0x4d7b0709` ou qualquer outro ID apenas por sua contagem de startup.

Iniciar o GPU-Z já sob o WinDbg não alcançou a aba `Sensors`: o processo permaneceu no splash. A coleta dinâmica útil passou a seguir outro desenho: iniciar o GPU-Z normalmente, confirmar visualmente `Sensors` e o log ativo e só então anexar o CDB x86 assinado da Microsoft. Esse anexo tardio não reinicia o aplicativo. O encerramento usa `qqd`, confirma a desconexão no transcript e verifica que o GPU-Z continua responsivo.

## Candidatos NVAPI no polling real

Um coletor anexado colocou breakpoints nos 100 endereços já resolvidos, sem chamar NVAPI e sem ler argumentos ou retornos. Durante dez segundos em que o log de `Sensors` continuou produzindo uma linha por segundo, 19 alvos receberam 465 chamadas: oito públicos e 11 ausentes do catálogo oficial usado. A função pública `NvAPI_GPU_GetThermalSettings`, presente no caminho de startup, recebeu zero chamadas nessa janela. Isso demonstra que o polling desta versão do GPU-Z usa outro caminho térmico.

O relatório ancora o inventário, o GPU-Z, o CDB e cada módulo NVAPI por SHA-256. Para o `nvapi_impl.dll` `fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf`, os candidatos privados ativos foram:

| ID | RVA | Chamadas | Evidência estática atual |
| --- | --- | ---: | --- |
| `0x465f9bcf` | `0x00198010` | 7 | referências diretas a `voltRailsInfo`, `voltRailsStatus`, `voltPoliciesInfo`, `voltPoliciesStatus` e `voltRailsControl` |
| `0x65fe3aad` | `0x001ad310` | 16 | referências diretas aos diagnósticos de versão de `NvAPI_GPU_ThermChannelGetStatus` |
| `0x35aed5e8` | `0x001b9f10` | 33 | referências diretas a `fanCoolerInfo` e `coolerMap` |
| `0x64b43a6a` | `0x001cb0b0` | 3 | sem atribuição semântica suficiente |
| `0x23f1b133` | `0x001cb680` | 1 | sem atribuição semântica suficiente |
| `0x507b4b59` | `0x001cbf20` | 2 | sem atribuição semântica suficiente |
| `0x1bd69f49` | `0x001e4870` | 15 | chamada interna a partir de uma rotina que referencia `clockInfo`; atribuição indireta |
| `0xedcf624e` | `0x001f7390` | 7 | referência a `policyStatusEscData`; tipo de política ainda indeterminado |
| `0xf40238ef` | `0x00216640` | 62 | sem atribuição semântica suficiente |
| `0x1ea54a3b` | `0x00233e70` | 4 | sem atribuição semântica suficiente |
| `0x3d358a0c` | `0x00258500` | 7 | observado em polling, embora ausente na execução de startup; sem atribuição semântica suficiente |

## ABI e canais térmicos confirmados no perfil testado

Uma sessão nova, com o log crescendo antes, no meio e depois do anexo, permitiu fechar o caminho de `0x65fe3aad`. A análise estática e o call site do GPU-Z em RVA `0x002225b5` demonstraram dois argumentos: o handle da GPU física e um ponteiro para uma estrutura v2 de 168 bytes. O chamador zera a estrutura, grava versão `0x000200a8`, seleciona o canal com `1 << channel` e, após retorno de sucesso, lê uma palavra de 32 bits. A implementação NVIDIA encaminha a operação privada RM `0x2080853b`.

| Canal | Máscara | Offset / palavra | Codificação observada | Associação externa |
| ---: | --- | --- | --- | --- |
| 0 | `0x00000001` | `0x28` / 10 | inteiro com sinal, ponto fixo 8; `raw / 256` °C | `GPU Temperature` / temperatura do die |
| 1 | `0x00000002` | `0x2c` / 11 | inteiro com sinal, ponto fixo 8; `raw / 256` °C | `Hot Spot` |

O coletor fixo [`capture-gpuz-nvapi-therm-channel-v2.ps1`](../../scripts/capture-gpuz-nvapi-therm-channel-v2.ps1) não chamou NVAPI nem alterou o buffer: interrompeu somente o ponto pós-chamada allowlisted e leu os 42 DWORDs que o próprio GPU-Z já havia inicializado. Em dez segundos, registrou 20 retornos bem-sucedidos, dez por canal. O canal 0 variou de `33,03125` a `33,1875` °C; o canal 1, de `43,0` a `43,75` °C. Fora da versão, máscara e palavra selecionada, os demais campos permaneceram zerados nessa janela.

O comando offline `correlate-nvapi-therm-channel` comparou a captura com o prefixo exato do log, ancorado por tamanho e SHA-256. Na sessão 5, janela local de `22:37:41` a `22:37:49`, o erro máximo foi `0,04375` °C para canal 0 → `GPU Temperature` e `0,05` °C para canal 1 → `Hot Spot`, compatível com o arredondamento de uma casa decimal do GPU-Z. A associação invertida teve erro absoluto médio combinado de `10,3565625` °C; portanto, não é uma hipótese equivalente.

Essa identificação vale somente para o perfil ancorado: RTX 3060 `10de:2504`, subsystem `10de:1536`, VBIOS `94.06.25.00.fc`, driver 610.88, GPU-Z SHA-256 `6cb0ef29682452de81a9576808881685161411a1fad00938ba04131159979c29` e `nvapi_impl.dll` SHA-256 `fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf`. É uma correspondência contra referência externa, não um contrato publicado pela NVIDIA nem prova de um termistor físico separado.

O primeiro detach histórico manteve a GUI responsiva, mas interrompeu o worker do log; a repetição executada sem novas amostras foi corretamente excluída. Na sessão válida acima, o coletor comprovou crescimento antes, no midpoint e depois da captura, desconectou com `qqd`, preservou o GPU-Z responsivo e não deixou CDB/WinDbg anexado.

Na captura final de dez segundos, `KernelBase!DeviceIoControl` registrou 130 chamadas. Uma captura de controle em `ntdll!NtDeviceIoControlFile` encontrou exatamente os mesmos dois códigos, tamanhos, handle e contagens. Portanto, não apareceu um caminho nativo adicional que contornasse `DeviceIoControl` durante essa janela.

## Evidência de Process Monitor

O recorte focado contém 1.931 eventos do processo GPU-Z:

| Operação | Eventos |
|---|---:|
| `CreateFile` | 1.684 |
| `DeviceIoControl` | 136 |
| `Load Image` | 111 |

Os 136 eventos rotulados como `DeviceIoControl` nessa captura ampla eram operações de filesystem `IOCTL_MOUNTDEV_QUERY_DEVICE_NAME`; não eram controles privados do helper. Em outra captura de 15 segundos anexada ao GPU-Z já na aba `Sensors`, o Process Monitor registrou sete `CreateFile` e nenhum `DeviceIoControl`, embora o log de sensores continuasse crescendo. Portanto, o Process Monitor serviu para módulos e ciclo de vida, mas não para enumerar o canal privado em polling.

Também foram isolados 253 eventos do ciclo de vida do driver. O trace observou `services.exe` configurando o serviço `GPU-Z-v8` e `System` carregando sua imagem. Os arquivos locais de evidência ficam em `/evidence/`, ignorado pelo Git. Os traces amplos foram mantidos localmente e o resumo registra isso explicitamente; eles não fazem parte do projeto publicável.

## Helper do GPU-Z

Antes da análise estática, uma cópia local foi preservada e o serviço temporário foi parado e removido. O arquivo original no `%TEMP%` também foi removido depois da validação exata de assinatura e hash.

| Propriedade | Valor |
|---|---|
| Nome | `GPU-Z-v8.sys` |
| Tamanho | 96.656 bytes |
| Versão | 8.1.0.0 |
| Produto | `Low-Level Driver` |
| SHA-256 | `999cf056a298cfce5f5a61d44c218ffafccd36ecff53e433768512073e6bf005` |
| Assinatura | válida, TechPowerUp LLC |
| Thumbprint | `67E2A5706E605E7594D82CC9D00C804742D307B7` |

A inspeção estática observou imports para configuração PCI, mapeamento de I/O e acesso de baixo nível, além de uma família privada de IOCTLs. Entre eles estão `HalGetBusDataByOffset`, `HalSetBusDataByOffset`, `MmMapIoSpace` e `MmUnmapIoSpace`. O dispatcher contém caminhos de leitura e escrita. Portanto, esse helper é uma ferramenta genérica e privilegiada, não uma API térmica somente leitura que possamos reutilizar com segurança.

O projeto não executa a cópia, não abre seu device object, não reproduz IOCTLs e não distribui o binário.

## Canal privado observado com `Sensors` ativo

O comando C# `resolve-windows-handle` usa somente `OpenProcess`, `DuplicateHandle`, `NtQueryObject` e `QueryDosDevice`. Aplicado ao PID do GPU-Z e ao handle observado `0x368`, ele produziu um relatório válido por [`windows-handle-identity-v1.schema.json`](../schema/windows-handle-identity-v1.schema.json):

| Campo | Identidade comprovada |
|---|---|
| Tipo do objeto | `File` |
| Nome NT | `\Device\GPU-Z-v8` |
| Alias DOS | `\\.\GPU-Z-v8` |

Logo, as chamadas abaixo pertencem ao helper do GPU-Z, não à NVAPI nem diretamente ao `nvlddmkm`. O coletor não enviou nenhuma chamada: apenas interrompeu as chamadas que o GPU-Z já faria. Primeiro registrou código e tamanhos; depois, com os handlers delimitados estaticamente, registrou somente os 4 ou 12 bytes de entrada declarados. Nenhum buffer de saída foi lido.

| IOCTL | `CTL_CODE` decodificado | Janela final | Handler | Semântica confirmada |
|---|---|---:|---|---|
| `0x80006040` | tipo `0x8000`, função `0x810`, `METHOD_BUFFERED`, `FILE_READ_ACCESS` | 110 chamadas; entrada 4, saída 8 bytes | RVA `0x78bf` chama RVA `0x9cb0`, que executa `RDMSR` | todas as entradas foram `0x19c`, o MSR Intel `IA32_THERM_STATUS`; é telemetria térmica da CPU, não da RTX |
| `0x800060c0` | tipo `0x8000`, função `0x830`, `METHOD_BUFFERED`, `FILE_READ_ACCESS` | 20 chamadas; entrada 12, saída 4 bytes | jump table aponta para RVA `0x7c99`, que chama `HalGetBusDataByOffset` | leituras PCI de 1 byte em bus 1/device 0/function 0, offsets `0x34`, `0x60`, `0x61`, `0x68`, `0x69`, `0x78`, `0x84`, `0x85`, `0x8a` e `0x8b` |

Os campos `CTL_CODE` seguem a macro publicada pela Microsoft. O MSR `0x19c` é documentado pela Intel como status do monitor térmico do processador. `HalGetBusDataByOffset` documenta explicitamente bus, slot/função, offset e comprimento de configuração PCI. Assim, os dois códigos foram semanticamente classificados sem usar correlação por nome e sem interpretar o retorno.

Esse resultado elimina ambos como candidatos diretos ao `Hot Spot` da RTX. As leituras PCI podem sustentar campos de link/capacidade da placa, mas não constituem leitura de um sensor térmico. A ligação de cada offset a um campo visual específico ainda exige uma experiência isolada; ela não foi inferida apenas pelo endereço.

## Classificação dos IDs NVAPI

O comando offline:

```powershell
dotnet run --project .\csharp\RtxMonitor.Lab -- classify-nvapi-ids `
  --input C:\evidence-local\nvapi-query-report.json `
  --interface-table C:\fontes\nvapi\nvapi_interface.h
```

calcula hashes dos dois artefatos e classifica cada ID como:

- `public_catalog_match`, com o nome publicado;
- `not_in_public_catalog`, sem atribuir nome ou função.

Na coleta atual, o catálogo NVIDIA estava no commit `cd6918f60b3c9a0476fdfe7e89bb32330602049d`; o SHA-256 de `nvapi_interface.h` era `baa4dfc43e5b2c8494da532ed6e062a1edffc9dfda4c1a9987dfc3b270094365`.

Os módulos que contiveram os endereços resolvidos foram fixados por hash:

| Módulo | SHA-256 | IDs resolvidos |
|---|---|---:|
| `nvapi.dll` | `530c8ce8f0484331b8682a574e3cbf98724a2daa5d8bdbfc1b33950a63c3671f` | 1 |
| `nvapi_impl.dll` | `fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf` | 99 |

O endereço absoluto varia com ASLR. Por isso, a origem publicável de cada candidato é a tupla `module_name + module_sha256 + RVA`, e não o endereço observado em memória.

Consultas públicas adjacentes à telemetria incluíram:

| ID | Função pública |
|---|---|
| `0xe3640a56` | `NvAPI_GPU_GetThermalSettings` |
| `0x5f608315` | `NvAPI_GPU_GetTachReading` |
| `0x60ded2ed` | `NvAPI_GPU_GetDynamicPstatesInfoEx` |
| `0x6ff81213` | `NvAPI_GPU_GetPstates20` |
| `0x927da4f6` | `NvAPI_GPU_GetCurrentPstate` |
| `0xdcb616c3` | `NvAPI_GPU_GetAllClockFrequencies` |
| `0x07f9b368` | `NvAPI_GPU_GetMemoryInfo` |

Os 57 IDs restantes não podem ser chamados apenas porque foram observados. Nesta instalação, todos retornaram ponteiro não nulo, o que elimina a hipótese de simples falha de resolução, mas não distingue API privada, interface antiga ou implementação específica da versão. A captura de execução reduziu o conjunto prioritário para 19 IDs ausentes do catálogo que o GPU-Z realmente chamou.

O comando offline `inventory-nvapi-candidates` combina a classificação com a observação de chamadas. Cada entrada preserva ID, nome público opcional, hash do módulo, RVA, contagem de consultas, contagem de execução e dois estados separados: evidência de execução e estado semântico. `unidentified_binary_candidate` significa “endereço executado e ainda sem significado”, nunca “sensor oculto identificado”.

## Próximo gate experimental

A primeira passagem do candidato `0x465f9bcf` comprovou uma estrutura v1 de 76 bytes (`0x0001004c`) e correlacionou a palavra 10/offset `0x28`, interpretada como 862.500 microvolts, com `0,8620 V` no GPU-Z e `0,863 V` no HWiNFO durante a mesma janela. Uma segunda passagem sob carga reproduziu três degraus: 868.750, 937.500 e 1.081.250 microvolts corresponderam respectivamente a `0,8680`, `0,9370` e `1,0810 V` no GPU-Z. A evidência e os limites estão em [Correlação multipatamar do status privado de tensão](2026-08-26-rtx3060-nvapi-voltage-status-v1.md).

O próximo gate é estabilizar um schema e correlator offline e repetir a sessão para testar reprodutibilidade. Rails, políticas, potência, limite e status não podem ser distinguidos apenas por proximidade no binário ou por essa correlação de tensão.

Atualização de fechamento (2026-08-27): o schema/correlator v2 e a repetição independente foram concluídos; a leitura direta passou a existir somente como aquisição opt-in de perfil fixo, com os gates do [ADR 0010](../adr/0010-fixed-profile-private-nvapi-acquisition.md). Esta nota preserva o estado histórico da investigação e não descreve a superfície atual do runtime.

Em paralelo, a captura térmica deve ser repetida em ciclos controlados de repouso, aquecimento e resfriamento, preservando valores brutos, log externo e incerteza. Uma sonda física independente ainda é necessária para promover o resultado além de `matched_external_reference`. A captura do startup permanece como trilha complementar para descobrir configuração ou outros canais. Nenhuma interface privada será chamada pelo projeto, e nenhum perfil será generalizado para outro driver, VBIOS ou modelo sem nova evidência.

## Fontes primárias

- [NVIDIA — catálogo público `nvapi_interface.h`](https://github.com/NVIDIA/nvapi/blob/main/nvapi_interface.h)
- [NVIDIA — documentação térmica NVAPI](https://docs.nvidia.com/nvapi/group__gputhermal.html)
- [Microsoft — Process Monitor](https://learn.microsoft.com/sysinternals/downloads/procmon)
- [Microsoft — `DeviceIoControl`](https://learn.microsoft.com/windows/win32/api/ioapiset/nf-ioapiset-deviceiocontrol)
- [Microsoft — definição de `CTL_CODE`](https://learn.microsoft.com/windows-hardware/drivers/kernel/defining-i-o-control-codes)
- [Microsoft — `HalGetBusDataByOffset`](https://learn.microsoft.com/windows-hardware/drivers/ddi/ntddk/nf-ntddk-halgetbusdatabyoffset)
- [Microsoft — WinDbg](https://learn.microsoft.com/windows-hardware/drivers/debugger/)
- [Microsoft — arquivos de comandos do WinDbg](https://learn.microsoft.com/windows-hardware/drivers/debuggercmds/using-script-files)
- [Intel — Software Developer Manuals](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html)
