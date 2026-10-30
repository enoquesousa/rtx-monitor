# ADR 0008 — laboratório reproduzível e aquisição experimental allowlisted

- Status: aceito
- Data: 2026-08-25
- Versão: v0.8.0

## Contexto

A v0.7.0 esgotou um catálogo fechado de telemetria documentada e preservou resultados negativos. Hotspot, VRM, controladores auxiliares e outros canais podem existir fisicamente sem aparecer na NVML ou NVAPI. Investigar esses canais exige observar bytes fora da API estável, mas um endereço que varia com a carga não prova unidade, escala ou identidade física.

Acesso elevado também não torna uma leitura segura. Espaço de configuração PCI e recursos MMIO podem conter registradores com efeitos colaterais de leitura. Uma interface que aceite endereço e tamanho arbitrários seria, na prática, um leitor de memória física utilizável por qualquer cliente que alcançasse o helper.

O projeto precisa iniciar a engenharia reversa real sem misturar resultados experimentais ao monitor estável e sem criar uma primitiva genérica de acesso ao hardware.

## Decisão

### Laboratório separado

A v0.8.0 cria uma trilha experimental opt-in composta por coordenador, helper opcional, empacotador e analisador offline. Ela não altera o serviço, o banco de telemetria ou os provedores públicos. A exceção posterior para duas funções C opt-in de perfil fixo, sem integração à telemetria estável, está registrada no [ADR 0010](0010-fixed-profile-private-nvapi-acquisition.md).

O MVP executável implementa um pacote fechado e verificável de um arquivo local, com layout fixo `manifest.json` mais `artifact/payload.bin`:

1. `artifact-package-manifest-v1`: contrato exato do `manifest.json` emitido;
2. `raw-artifact-v1`: descritor embutido com caminho fixo, nome original, tamanho e SHA-256;
3. `evidence-package-v1`: envelope JSON de sucesso emitido por `rtxmon-lab create` e `verify`;
4. `lab-command-error-v1`: envelope JSON de erro emitido em `stderr`.

O pacote não é considerado íntegro por ser autoconsistente. O MVP não altera o atributo NTFS `ReadOnly`, pois ele afetaria todos os hardlinks do mesmo arquivo sem criar uma fronteira de segurança. `verify` exige o SHA-256 do manifesto preservado fora da própria pasta e compara essa âncora antes de confiar no hash do payload.

O empacotador roda somente como usuário comum em diretório privado. Handles exclusivos e rehash pós-move detectam adulteração concorrente conhecida, mas as operações de criação, rename e cleanup ainda usam pathname. Esse TOCTOU residual é aceito apenas na fase offline: uma aquisição privilegiada não poderá reutilizar essa fronteira até adotar operações relativas a handles de diretório ou entregar bytes por IPC a um empacotador sem elevação.

O parser offline `rtxmon-vbios` é o segundo componente implementado. No Windows, ele lê o caminho fornecido pelo operador até 16 MiB, valida a cadeia PCI ROM, `PCIR`, checksum legacy e BIT 1.00, não interpreta os ranges opacos dos tokens e emite `vbios-analysis-v1` sem acessar hardware. Caminhos remotos, de dispositivo, streams alternativos e reparse points são rejeitados.

Os CLIs `rtxmon-lab` e `rtxmon-vbios` v0.8 fazem ingestão de arquivo somente no Windows. Essa restrição evita afirmar identidade de arquivo regular e ausência de links/streams usando uma abstração incompleta no Unix, além de impedir que um argumento seja confundido com `sysfs` ou um device. Em outras plataformas, `rtxmon-vbios` retorna `unsupported_platform` antes de validar ou abrir o caminho. A biblioteca C++ de parsing permanece portátil e recebe somente bytes já carregados por um chamador confiável; suporte Linux do CLI exigirá validação equivalente baseada no handle aberto.

Dois contratos adicionais, inicialmente definidos por esta decisão, passaram a ser emitidos pelo CLI ao fechar a v0.8.0:

1. `experiment-manifest-v1`: identidade, ambiente, bases de tempo, cenários, pacotes verificados e operações solicitadas, emitido por `finalize-experiment-manifest`;
2. `analysis-report-v1`: resultados derivados e estágio de evidência de cada candidato, emitido por `analyze-experiment-series` a partir de manifesto e pacote ancorados.

### Ordem de trabalho

A ordem obrigatória é linha de base pública, VBIOS offline quando fornecida, hipótese documentada, revisão do perfil, aquisição allowlisted, selagem e análise offline. O CLI Windows não abre NVML, device object, `sysfs`, BAR ou qualquer handle para hardware; em Unix, ele falha antes de acessar o path.

### VBIOS somente offline

O laboratório aceita uma imagem de VBIOS que o operador já possua. Antes de analisar, registra origem declarada, nome lógico, tamanho e SHA-256. O projeto não habilita a ROM PCI, não faz dump, não faz flash, não executa a imagem e não a redistribui. Fixtures públicas não podem conter bytes proprietários.

### Aquisição privilegiada por operação

Quando uma leitura não for possível em user mode, o coordenador envia `operation_id`, sessão e limites mais restritivos. Ele não envia um endereço livre.

O helper mantém ou verifica uma allowlist própria e valida novamente:

- identidade PCI completa e revisão;
- versão do perfil e estado de revogação;
- espaço de leitura: somente `pci_config` ou `bar0_mmio`;
- offset, largura, alinhamento e endianess;
- quantidade máxima de amostras, intervalo mínimo e total de bytes;
- timeout, versão do protocolo e tamanho de todos os buffers.

Um manifesto não assinado nunca amplia essa allowlist. Identidade, versão ou operação diferente falha antes de qualquer acesso. O helper devolve cópia limitada dos bytes; não devolve ponteiro nem mapeamento de BAR ao cliente.

No Windows, a fronteira de kernel será um driver KMDF assinado, compatível e testado com HVCI. O device object terá ACL para `SYSTEM` e Administradores, `FILE_DEVICE_SECURE_OPEN`, IOCTL com acesso explícito e buffer validado. `FILE_ANY_ACCESS` e `METHOD_NEITHER` não serão usados. Executar um processo como Administrador permite instalar ou abrir o componente aprovado, mas não substitui assinatura, HVCI nem validação em kernel mode.

No Linux, arquivos PCI em `sysfs` são tratados conforme suas permissões e sem pressupor que leitura ou `mmap` sejam inofensivos. O coletor não escreve em `enable`, `remove`, `reset`, `config`, `rom` ou `resourceN`. A pesquisa offline não exige root.

### Espaços proibidos

A v0.8.0 não contém:

- varredura de offsets ou ranges;
- I2C, DDC ou SMBus;
- escrita em configuração PCI, registradores, BAR, ROM ou firmware;
- BAR1, VRAM, DMA ou memória física arbitrária;
- habilitação da ROM PCI;
- flash, execução ou patch de firmware;
- mudança de clocks, tensão, ventoinhas, potência ou estado de energia.

Em um eventual helper futuro, BAR0 só poderá ser admitido em offsets pontuais cujo risco de leitura tenha sido revisado para um perfil exato; a v0.8 concluída não o acessa. “Read-only” descreverá o protocolo, mas não garantirá que o silício não tenha um efeito colateral ao ler um registrador desconhecido.

### Evidência antes de interpretação

Cada payload é gravado antes da decodificação e recebe descritor separado com tamanho, SHA-256, fonte, modo de captura, base monotônica e operação autorizada. O pacote usa caminhos relativos normalizados, recusa `..`, caminhos absolutos e symlinks, e não inclui segredos ou caminhos locais.

Os estágios continuam:

- `raw_unknown`: bytes e localização conhecidos, significado desconhecido;
- `correlated`: comportamento repetível sob estímulo, ainda candidato;
- `externally_validated`: escala comparada em várias faixas com referência independente.

Somente `externally_validated` pode carregar um nome físico afirmado, ainda restrito ao perfil testado.

## Matriz de privilégio

| Contexto | Pode fazer | Não concede |
|---|---|---|
| Windows, usuário comum | APIs públicas, manifesto, marcadores, hash, VBIOS offline e análise | PCI config privado, BAR ou instalação de driver |
| Windows, Administrador | Instalar/iniciar pacote de driver aprovado e abrir interface restrita | Acesso arbitrário a MMIO; capacidade de ampliar a allowlist |
| Windows, KMDF assinado/HVCI | Executar no kernel apenas operações compiladas ou assinadas e validadas | BAR1, VRAM, escrita, endereço livre ou `mmap` para user mode |
| Linux, user mode/offline | Metadados legíveis, `hwmon`, hashing e análise pela biblioteca de bytes já carregados; o CLI v0.8 não ingere paths | Permissão automática para `config`, `resourceN` ou `rom` |
| Linux, root/componente auxiliar | Operação de leitura exata prevista pelo perfil e controles equivalentes | Justificativa para varredura, escrita, ROM, BAR1 ou VRAM |

## Consequências

- A primeira entrega experimental é deliberadamente menor que ferramentas genéricas de dump.
- Um novo offset exige mudança revisável da allowlist, testes e novo identificador de perfil.
- O laboratório pode produzir `not_collected` quando o helper, assinatura, HVCI ou perfil não estiver disponível; ausência nunca vira zero.
- Pacotes podem permanecer locais quando contiverem material proprietário. Descritores e hashes permitem comparar resultados sem publicar o payload.
- Windows e Linux podem usar mecanismos diferentes, mas produzem os mesmos contratos de evidência.
- O serviço estável não inicia, instala ou se comunica com o helper.

## Alternativas rejeitadas

### Driver genérico de leitura de memória física

Rejeitado porque transformaria um experimento em uma primitiva de kernel reutilizável para acesso arbitrário e tornaria impossível demonstrar fail-closed por perfil.

### Varredura de BAR0 para procurar números que mudam

Rejeitado porque multiplica o risco de efeitos colaterais e produz falsos positivos sem hipótese, unidade ou semântica.

### BAR1 ou leitura direta de VRAM

Rejeitada porque não é necessária para a hipótese térmica inicial, amplia muito a superfície e pode expor dados de outros processos.

### Fazer dump do VBIOS automaticamente

Rejeitado porque algumas interfaces exigem habilitar a ROM por escrita. A v0.8.0 separa aquisição de ROM de análise offline.

### Confiar na elevação do coordenador

Rejeitado porque Administrador não é uma política de offsets. A fronteira de kernel precisa validar a solicitação de forma independente.

## Referências primárias

- [Microsoft: modelo de segurança para desenvolvedores de drivers](https://learn.microsoft.com/windows-hardware/drivers/driversecurity/windows-security-model)
- [Microsoft: definição e bits de acesso de IOCTLs](https://learn.microsoft.com/windows-hardware/drivers/kernel/defining-i-o-control-codes)
- [Microsoft: assinatura de drivers Windows](https://learn.microsoft.com/windows-hardware/drivers/install/windows-driver-signing-tutorial)
- [Microsoft: compatibilidade de drivers com HVCI](https://learn.microsoft.com/windows-hardware/test/hlk/testref/driver-compatibility-with-device-guard)
- [Linux kernel: recursos PCI por `sysfs`](https://docs.kernel.org/PCI/sysfs-pci.html)
- [Linux kernel: interface `hwmon`](https://docs.kernel.org/hwmon/sysfs-interface.html)
- [NVIDIA Open GPU Kernel Modules](https://github.com/NVIDIA/open-gpu-kernel-modules)
- [NVIDIA: extração e compatibilidade do firmware GSP para Nouveau](https://github.com/NVIDIA/open-gpu-kernel-modules/blob/main/nouveau/extract-firmware-nouveau.txt)
