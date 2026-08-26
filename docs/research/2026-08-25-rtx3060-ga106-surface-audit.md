# Auditoria de superfícies — RTX 3060 GA106

- Data: 2026-08-25
- Tipo: inventário somente leitura
- Estado: nenhuma captura de ROM, configuração PCI, MMIO, I2C ou VRAM realizada

Este registro descreve uma placa e uma combinação de firmware/driver específicas. Ele não deve ser generalizado para outra RTX 3060 sem repetir o inventário.

## Perfil observado

| Propriedade | Valor |
|---|---|
| GPU | NVIDIA GeForce RTX 3060, Ampere/GA106 |
| PCI | `0000:01:00.0` |
| Device | `10de:2504` |
| Subsystem | `10de:1536` |
| Revisão PCI | `A1` |
| GPU part number | `2504-302-A1` |
| Board ID | `0x100` |
| VBIOS | `94.06.25.00.fc` |
| InfoROM | `G001.0000.03.03`; objeto OEM `2.0` |
| Driver NVIDIA | `610.88`; Windows `32.0.16.1088` |
| Modelo do driver | WDDM |
| Link PCIe | Gen4 x16 atual e máximo no momento do inventário |

O UUID completo foi usado localmente para correlacionar as consultas, mas foi omitido deste documento público por ser um identificador persistente do dispositivo.

## Resultado da sondagem pública exaustiva

Um ensaio temporário chamou `nvmlDeviceGetFieldValues` para todos os IDs de 0 a 302, sem escrever arquivos ou estado da GPU:

- 49 IDs retornaram `NVML_SUCCESS`;
- 253 retornaram `NVML_ERROR_NOT_SUPPORTED`;
- 1 retornou argumento inválido;
- 41 dos 49 sucessos ainda não faziam parte do catálogo semântico da v0.7.

Sucesso significa que a NVML aceitou a consulta neste perfil. Não significa que o contador tenha valor diferente de zero nem que represente um sensor físico adicional.

| Grupo | IDs de campo com sucesso |
|---|---|
| Totais ECC | `3`, `4`, `5`, `6` |
| Tempo em políticas de desempenho | `74` a `81` |
| Energia | `83`, `191` |
| Replay PCIe | `94`, `95` |
| NVSwitch/MIG | `147`, `199` |
| Recuperação e erros PCIe | `169`, `173` a `183`, `226` a `230` |
| Potência e limites | `185` a `190`, `192` |
| Tráfego PCIe | `197`, `198` |
| Motivos térmicos/power brake | `269`, `270`, `271` |
| Troca de clock de memória | `298`, `299` |

O campo `82`, temperatura pública da memória, retornou `not_supported`. Também não houve canal público identificado como hotspot, VRM, memory junction ou temperatura de alimentação. O die da GPU e as ventoinhas continuaram disponíveis pelas APIs públicas normais.

## Recursos PCI publicados pelo Windows

O PnP Manager expôs três recursos de memória e uma faixa de I/O para a função da GPU:

| Recurso | Tamanho observado | Interpretação ainda necessária |
|---|---:|---|
| Memória | 16 MiB | BAR exata depende do PCI config bruto |
| Memória | 256 MiB | Coincide com o tamanho BAR1 publicado pela NVML |
| Memória | 32 MiB | BAR exata depende do PCI config bruto |
| I/O | 128 bytes | Não investigado |

O sistema também reportou AER, MSI e payload PCIe atual/máximo de 256 bytes. Essas propriedades são descrições do Windows; não são dumps do espaço de configuração ou dos BARs.

## Binários instalados versus bytes obtidos da placa

O pacote do driver contém `nvml.dll`, `nvapi64.dll`, `nvlddmkm.sys`, `gsp_ga10x.bin`, `nvcoproc.bin`, `nvcubins.bin` e `nvoptix.bin`.

Esses arquivos são binários distribuídos pelo driver no host. Em especial, `gsp_ga10x.bin` começa como ELF, mas **não** é um dump do firmware em execução nem uma leitura da placa. A versão da VBIOS e o InfoROM publicados pelas APIs também são metadados estruturados, não a imagem binária completa.

Nenhuma imagem `.rom` da placa foi encontrada no pacote ativo do driver.

## O que pode ser adquirido e em qual fronteira

| Superfície | Conteúdo provável | Situação na v0.8 | Autoridade |
|---|---|---|---|
| NVML/NVAPI públicas | Telemetria e metadados estruturados | Disponível | Usuário comum |
| Arquivo VBIOS já obtido | PCI ROM, PCIR, BIT e tokens | Parser offline implementado | Usuário comum |
| PCI config 256 B/4 KiB | IDs, BARs e capabilities PCIe | Ainda não implementado | Driver Windows compatível ou Linux nativo |
| Imagem da ROM da placa | VBIOS exposta pelo ROM BAR/ferramenta | Ainda não capturada | Administrador com ferramenta confiável ou root; pode exigir mudança transitória de estado |
| BAR0 allowlisted | Registradores pontuais previamente revisados | Planejado, risco alto | Driver KMDF assinado/HVCI ou helper Linux equivalente |
| RM térmico privado | Alvos GPU, memória, alimentação e placa modelados pelo driver | Hipótese experimental | Interface privada versionada; suporte Windows ainda não demonstrado |
| I2C/PMBus dirigido | Controlador VRM, monitor de potência ou ventoinha, se presentes | Somente após topologia/controlador conhecidos | Componente auxiliar específico; sem varredura nem escritas |
| BAR1/VRAM | Framebuffer e dados de processos | Fora do escopo | Não será exposto pelo projeto |
| SPI completo, fuses e estado privado do GSP | Regiões não expostas ao host | Pode ser impossível por software | Instrumentação física pode ser necessária |

## Por que a VBIOS é o primeiro artefato bruto

A especificação oficial da NVIDIA informa que a BIT aponta para tabelas específicas da GPU e da placa. Entre elas há clock, memória, desempenho, energia, temperatura, fan, tensão e scripts I2C. A DCB também pode descrever portas, endereços e tipos de dispositivos I2C.

Portanto, a ordem correta é:

1. capturar ou receber uma imagem VBIOS de origem declarada;
2. empacotar tamanho e SHA-256 com `rtxmon-lab` e preservar o hash do manifesto fora do pacote;
3. validar ROM, PCIR e BIT com `rtxmon-vbios`;
4. implementar decodificação offline das tabelas necessárias;
5. identificar controlador, porta, endereço e registradores antes de qualquer I2C;
6. usar aquisição privilegiada apenas para uma operação allowlisted e revisada.

O parser atual é deliberadamente conservador: ele valida a cadeia completa de imagens do primeiro contêiner PCI ROM encontrado, mas expõe a análise detalhada somente da imagem primária e dos metadados BIT que cabem integralmente nela. Ele ainda não interpreta o conteúdo dos tokens.

## Estado de autoridade desta máquina

O diagnóstico `scripts/check-lab-access.ps1 -Json` confirmou:

- processo atual não elevado;
- VBS ativo e política de integridade de código em enforcement;
- consulta de Secure Boot negada sem elevação;
- NVFlash ausente;
- `signtool.exe` x64 presente;
- headers KMDF e `Inf2Cat.exe` ausentes, portanto o WDK de driver está incompleto;
- WSL presente apenas como infraestrutura Docker, insuficiente para expor a função PCI física da GPU.

Consequência: empacotamento e análise offline já podem continuar sem Administrador. Como nenhuma ferramenta de captura confiável foi encontrada, elevar agora não habilitaria a captura de VBIOS; primeiro é preciso obter e revisar uma ferramenta assinada. Depois disso, a captura Windows exigirá elevação. PCI config/MMIO por código próprio também exige instalar o WDK, construir um driver mínimo, obter assinatura compatível e manter HVCI ativo; elevação sozinha não resolve essa fronteira.

## Limites de interpretação

- Um valor que varia com carga não recebe automaticamente o nome hotspot ou VRM.
- Um sensor físico pode não estar montado, digitalizado ou acessível ao host.
- Um controlador VRM pode não implementar telemetria digital.
- Um field NVML com sucesso e valor zero pode ser apenas um contador aplicável sem eventos.
- Uma imagem pelo ROM BAR pode não representar todo o chip SPI.
- “Ler tudo” não é uma meta tecnicamente garantida; a meta é maximizar a superfície observável com proveniência e risco documentados.

## Fontes primárias

- [NVIDIA NVML — field IDs](https://docs.nvidia.com/deploy/nvml-api/group__nvmlFieldValueEnums.html)
- [NVIDIA NVML — field values](https://docs.nvidia.com/deploy/nvml-api/group__nvmlFieldValueQueries.html)
- [NVIDIA — BIOS Information Table](https://nvidia.github.io/open-gpu-doc/BIOS-Information-Table/BIOS-Information-Table.html)
- [NVIDIA — DCB 4.x](https://nvidia.github.io/open-gpu-doc/DCB/DCB-4.x-Specification.html)
- [NVIDIA — Open GPU Kernel Modules](https://github.com/NVIDIA/open-gpu-kernel-modules)
- [Microsoft — acesso ao PCI config](https://learn.microsoft.com/windows-hardware/drivers/pci/accessing-pci-device-configuration-space)
- [Microsoft — assinatura de drivers](https://learn.microsoft.com/windows-hardware/drivers/install/driver-signing)
- [Microsoft — compatibilidade com HVCI](https://learn.microsoft.com/windows-hardware/test/hlk/testref/driver-compatibility-with-device-guard)
- [Linux kernel — PCI por sysfs](https://docs.kernel.org/PCI/sysfs-pci.html)
