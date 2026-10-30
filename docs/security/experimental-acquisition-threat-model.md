# Threat model — aquisição experimental v0.8.0

- Estado: deferido; obrigatório somente antes de um futuro helper/driver em kernel mode
- Escopo: coordenador, IPC, helper/driver, perfil allowlisted, artefatos e analisador offline
- Fora do escopo: monitor e serviço estáveis, que não carregam o helper

## Objetivos de segurança

1. não oferecer leitura ou escrita arbitrária ao hardware;
2. não alterar configuração, energia, firmware ou memória da GPU;
3. impedir que um cliente menos privilegiado use o helper;
4. preservar disponibilidade do host e da GPU na medida possível;
5. impedir que evidência adulterada seja tratada como captura válida;
6. manter firmware proprietário e dados de outros processos fora do Git e de pacotes públicos;
7. falhar fechado quando identidade, perfil, versão ou operação divergir.

“Somente leitura” é uma propriedade do protocolo, não uma garantia absoluta do dispositivo: um registrador pode limpar estado ou disparar comportamento ao ser lido. A allowlist reduz esse risco, mas não o elimina.

## Ativos

- estabilidade e dados do host;
- estado da GPU e do driver NVIDIA;
- isolamento de memória entre processos;
- integridade e proveniência dos artefatos;
- chave de perfil e allowlist revisada;
- pacote e assinatura do driver;
- VBIOS, firmware GSP e outros binários proprietários;
- identidade do operador e caminhos locais, que não devem vazar no pacote.

## Fronteiras de confiança

```text
operador
   |
   v
coordenador user mode ----> filesystem de evidências ----> analisador offline
   |
   | IOCTL local autorizado por ACL e acesso explícito
   v
device object / driver KMDF
   |
   | operação resolvida pela allowlist do kernel
   v
PCI config ou BAR0 pontual da GPU identificada
```

Entradas do operador, manifesto, VBIOS, artefatos importados e respostas do hardware são não confiáveis. O driver não confia na validação feita pelo coordenador. O analisador não confia no pacote até comparar a âncora externa do manifesto, recalcular os hashes e validar os schemas.

## Ameaças e controles

| Ameaça | Impacto | Controles obrigatórios |
|---|---|---|
| Endereço, BAR ou tamanho arbitrário | Leitura de memória física, vazamento ou falha do kernel | Cliente envia `operation_id`; allowlist independente no driver; somente `pci_config` e `bar0_mmio`; offset/largura fixos; aritmética checked |
| Caminho de escrita escondido | Alteração da GPU, firmware ou host | Nenhum IOCTL/write handler; nenhuma função recebe payload destinado ao dispositivo; testes de superfície binária e revisão do dispatch |
| BAR1/VRAM ou DMA | Vazamento de dados de processos e grande expansão de risco | Sem enum correspondente, sem mapeamento, sem operação; BAR permitido deve ser `0`; driver nunca configura DMA |
| Varredura disfarçada como várias leituras | Efeitos colaterais e descoberta arbitrária | IDs finitos, limite por sessão, taxa mínima, total de bytes e auditoria; repetição fora do perfil é recusada |
| Manifesto amplia a allowlist | Bypass da revisão de segurança | Manifesto só referencia IDs; perfis executáveis são compilados ou assinados; driver verifica versão/hash/revogação |
| GPU errada ou troca após descoberta | Bytes atribuídos ao perfil incorreto | Bind por identidade PCI/instância PnP; validar vendor/device/subsystem/revision ao abrir e antes da sessão; não usar índice como autoridade |
| Overflow, desalinhamento ou leitura além do recurso | Corrupção/falha de kernel | Inteiros de largura explícita, adição checked, alinhamento fixo, bounds contra comprimento real do recurso e buffers de saída limitados |
| Buffer de IOCTL malformado | Corrupção de kernel ou disclosure | `METHOD_BUFFERED`, versão e `struct_size`, comprimentos exatos, zero de padding/saída, rejeição de campos reservados não zero |
| Cliente não autorizado | Elevação indireta de privilégio | ACL em INF/device object para `SYSTEM` e Administradores, `FILE_DEVICE_SECURE_OPEN`, acesso IOCTL explícito, nunca `FILE_ANY_ACCESS` |
| Driver adulterado ou incompatível | Execução de kernel não confiável, bloqueio de boot | Assinatura apropriada, pacote íntegro, teste com HVCI ativo, Driver Verifier/HLK aplicáveis e rollback documentado |
| DoS por polling | Travamento do driver/GPU ou degradação do sistema | Intervalo mínimo, amostras e bytes máximos, timeout, cancelamento, uma sessão por GPU e watchdog no coordenador |
| Reset/remoção da GPU durante leitura | Use-after-free, dados parciais | Objetos KMDF/PnP, referências de recurso com ciclo de vida correto, cancelamento, falha explícita e artefato marcado incompleto |
| Registrador read-to-clear | Perda de estado ou mudança observável | Revisão por offset, fonte da hipótese, primeira captura curta, máquina de laboratório e revogação imediata ao detectar efeito |
| VBIOS maliciosa ou truncada | Exploração do parser | Parser offline, limite de 16 MiB, offsets e somas checked, sem execução/plugins, testes negativos e fixtures sintéticas |
| Path traversal ou symlink no pacote | Leitura/substituição de arquivos fora da evidência | Somente caminhos relativos normalizados, recusar `..`, drive, UNC e links; abrir sem seguir symlink quando suportado |
| Troca concorrente de pathname durante o pacote | Pacote inválido ou operação no alvo errado | Usuário comum, diretório privado, handles exclusivos, rehash pós-move e fail-leak; antes de aquisição privilegiada, substituir create/move/cleanup por operações relativas a handles ou isolar o helper do pathname do operador |
| Artefato alterado depois da captura | Conclusão falsa | SHA-256 e tamanho no descritor; hash do manifesto preservado fora do pacote; verificação integral antes da análise |
| Pacote omite falhas | Viés e falsa completude | Status explícito `completed`/`aborted`, comandos, warnings e leituras curtas preservados; ausência nunca vira zero |
| Vazamento de firmware ou dados pessoais | Violação de licença/privacidade | Payloads locais ignorados pelo Git; caminhos/usuários redigidos; revisão antes de compartilhar; hashes podem ser públicos sem o binário |

## Contrato mínimo do IOCTL futuro

O protocolo ainda será especificado em código, mas deve obedecer a estes invariantes:

- uma versão fixa e `struct_size` em request/response;
- `session_id`, `profile_id` e `operation_id`, sem endereço solicitado pelo cliente;
- `sample_count` e `interval_ms` só podem tornar a operação mais restritiva;
- resposta com status, quantidade efetiva, relógio monotônico e bytes copiados;
- sem ponteiro de usuário persistido, `METHOD_NEITHER`, seção compartilhada ou `mmap`;
- sem IOCTL para escrita, mapeamento, enumeração de intervalos, leitura física, BAR1, VRAM, ROM, I2C ou SMBus;
- campos reservados precisam ser zero e versões futuras falham explicitamente.

## Controles específicos por plataforma

### Windows

- instalação e início exigem Administrador, mas a aquisição ocorre somente pelo driver aprovado;
- pacote de kernel assinado conforme a política da versão alvo;
- HVCI permanece habilitado e todos os caminhos do driver são testados nesse estado;
- ACL por SDDL no INF, device interface restrita e acesso IOCTL diferente de `FILE_ANY_ACCESS`;
- KMDF gerencia PnP, remoção e cancelamento; nenhuma biblioteca de terceiros de acesso físico é aceita;
- falha de assinatura, HVCI, perfil ou identidade encerra a sessão sem fallback inseguro.

### Linux

- a biblioteca pura pode analisar bytes já carregados em usuário comum; o CLI `rtxmon-vbios` v0.8 retorna `unsupported_platform` antes de acessar qualquer path;
- root não autoriza escrita: arquivos `config`, `resourceN`, `rom`, `enable`, `reset` e `remove` não são modificados;
- qualquer leitura privilegiada usa o mesmo modelo de `operation_id` e allowlist;
- `resource1`/BAR1, VRAM, `/dev/mem` e interfaces genéricas de acesso físico são proibidos;
- versão do kernel, módulo NVIDIA/Nouveau e GSP são registradas no manifesto.

## Riscos residuais aceitos

- Uma leitura allowlisted ainda pode ter efeito colateral não documentado.
- Um sensor pode ser calibrado ou multiplexado pelo firmware e permanecer impossível de identificar.
- SHA-256 prova igualdade de bytes, não veracidade da origem declarada pelo operador.
- HVCI e assinatura reduzem riscos de código de kernel, mas não corrigem um erro lógico na allowlist.
- Evidência de uma placa não se generaliza para outra revisão, VBIOS ou driver.

Esses riscos só são aceitos em máquina de laboratório, com backup e recuperação disponíveis. Qualquer comportamento inesperado revoga a operação até nova revisão.

## Gate de liberação de um helper futuro

Este checklist permanece intencionalmente aberto e não é gate de saída da v0.8.0: a versão concluída não implementa helper/driver nem acessa PCI config, BAR ou MMIO. Nenhuma futura aquisição em kernel mode será liberada antes de todos os itens abaixo:

- [ ] ADR 0008 aceito e contratos JSON revisados;
- [ ] allowlist contém somente identidade e operações necessárias para uma hipótese documentada;
- [ ] revisão demonstra ausência de dispatch/caminho de escrita;
- [ ] testes negativos cobrem operação, perfil, placa, offset implícito, largura, versão e tamanho inválidos;
- [ ] pacote do driver está assinado e carrega com HVCI ativo;
- [ ] ACL e acesso de IOCTL impedem cliente comum;
- [ ] limites de taxa, bytes, timeout, cancelamento e remoção PnP foram testados;
- [ ] máquina de laboratório, backup e rollback foram confirmados;
- [ ] política de artefatos proprietários e `.gitignore` foram verificados;
- [ ] primeira execução usa o menor número possível de amostras e acompanhamento físico do host.

## Referências primárias

- [Microsoft: modelo de segurança para drivers](https://learn.microsoft.com/windows-hardware/drivers/driversecurity/windows-security-model)
- [Microsoft: IOCTLs e controle de acesso](https://learn.microsoft.com/windows-hardware/drivers/kernel/defining-i-o-control-codes)
- [Microsoft: buffers em drivers WDF](https://learn.microsoft.com/windows-hardware/drivers/wdf/accessing-data-buffers-in-wdf-drivers)
- [Microsoft: assinatura de drivers](https://learn.microsoft.com/windows-hardware/drivers/install/windows-driver-signing-tutorial)
- [Microsoft: compatibilidade com HVCI](https://learn.microsoft.com/windows-hardware/test/hlk/testref/driver-compatibility-with-device-guard)
- [Linux kernel: recursos PCI por `sysfs`](https://docs.kernel.org/PCI/sysfs-pci.html)
