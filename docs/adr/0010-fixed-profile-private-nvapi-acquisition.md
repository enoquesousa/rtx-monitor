# ADR 0010 — aquisição NVAPI privada em perfil fixo

- Status: aceito
- Data: 2026-08-27
- Versão: v0.8.0
- Complementa: [ADR 0008](0008-reproducible-reverse-engineering-lab.md)

## Contexto

As observações passivas do GPU-Z identificaram duas interfaces privadas da NVAPI no perfil exato da bancada. A interface térmica `0x65fe3aad` devolveu die e hotspot em uma estrutura v2 de 168 bytes; a interface de status de tensão `0x465f9bcf` devolveu a tensão do núcleo na palavra 10 de uma estrutura v1 de 76 bytes. As duas associações chegaram a `matched_external_reference`, mas continuam privadas, específicas do binário e sem contrato público da NVIDIA.

O [ADR 0008](0008-reproducible-reverse-engineering-lab.md) separou o laboratório da ABI estável para impedir que uma observação fosse promovida por conveniência. Durante a implementação da v0.8, a leitura térmica direta já entrou como função opt-in da ABI C. Manter a documentação dizendo que a ABI não muda esconderia essa superfície e enfraqueceria a revisão de segurança.

## Decisão

A v0.8 admite uma exceção estreita e removível na ABI C para **aquisição experimental em user mode e perfil fixo**:

- `rtxmon_read_private_thermal_channels` e `--thermal-watch` consultam somente os dois canais térmicos já correlacionados;
- `rtxmon_read_private_voltage_status` e `--voltage-watch` consultam somente a palavra de tensão já correlacionada;
- nenhuma dessas funções participa do coletor padrão, do serviço, da API HTTP/SSE, do SQLite, da exportação ou dos schemas de telemetria estáveis;
- resolver os ponteiros privados durante a abertura do contexto não autoriza uma leitura: a função só é chamada quando o consumidor escolhe explicitamente um dos dois modos experimentais.

Cada chamada falha fechada antes de produzir valor se qualquer gate divergir:

1. correspondência única NVML↔NVAPI por barramento, slot, device ID e subsystem ID;
2. UUID físico `GPU-fca3647e-8390-15a8-f23b-d0f870c9accd`, PCI `10de:2504`, subsystem `10de:1536`, VBIOS `94.06.25.00.fc` e driver `610.88`;
3. ponteiro pertencente ao `nvapi64_impl.dll` de SHA-256 `df6455ccf83e43cfe68f405af1eec4e053c7f95da998bf358053b7583980c2f4`;
4. RVA x64 `0x001e0bc0` para `0x65fe3aad` ou `0x001c9070` para `0x465f9bcf`;
5. versão, tamanho e, no térmico, máscara de canal exatos da estrutura privada;
6. flags e limites plausíveis do valor devolvido, com publicação atômica do par die/hotspot.

O módulo validado tem versão de arquivo `32.0.16.1088`. O RVA é calculado a partir do módulo realmente dono do ponteiro e o SHA-256 é calculado sobre seu arquivo de backing aberto sem compartilhamento de escrita ou exclusão, com tamanho estável antes/depois. Isso não declara equivalência byte a byte da imagem relocada em memória. Um UUID, driver, VBIOS, placa, subsystem, módulo, RVA ou estrutura diferente devolve indisponibilidade/erro e nenhum valor válido.

GPU-Z e HWiNFO permanecem referências externas de laboratório. Eles são necessários para produzir e repetir a evidência, mas não são dependências operacionais das duas leituras diretas. O HWiNFO é opcional em uma captura quando não há log corrente; sua ausência deve ser registrada, nunca substituída por um CSV antigo.

## Fronteira preservada

Esta decisão não cria um provider genérico nem uma API configurável de interfaces privadas. IDs, RVAs, layouts, índices e perfil são compilados e revisáveis; nenhum deles vem de argumento do usuário, manifesto ou rede. A aquisição:

- não exige driver próprio, helper de kernel ou Administrador;
- não abre PCI config, BAR, MMIO, I2C, DDC, SMBus, ROM ou VRAM;
- não altera fan, clock, tensão, potência ou estado de energia;
- não executa varredura, não aceita endereço livre e não expõe o buffer privado bruto;
- serializa as chamadas NVAPI no mesmo lock já usado pelo backend público.

O candidato cooler/fan `0x35aed5e8` permanece somente como observação passiva bruta. Seu contrato v2 exige identidade exata de GPU/PCI/subsystem/VBIOS/driver, hashes fixos dos artefatos anteriores e prova da imagem NVAPI realmente carregada antes de preservar estrutura, contagem e palavras. Nenhum campo recebe nome, unidade, fan index ou semântica até passar por repetição e referências independentes; o contrato v1 permanece histórico.

## Consequências

- A afirmação do ADR 0008 de que remover o laboratório não altera a ABI passa a ter esta exceção explícita para as duas funções experimentais.
- A ABI sobe para 5 e continua aditiva dentro da v0.8, usando `struct_size`; o número novo impede que a superfície adicional seja confundida com uma DLL ABI 4 anterior.
- Uma atualização de driver revoga o perfil por padrão. Suportá-la exige nova captura, hashes, RVAs, fixtures, revisão e alteração de código.
- Generalizar perfis, publicar valores no serviço ou chamá-los de provider continua pertencendo aos marcos posteriores do roadmap.

## Alternativas rejeitadas

### Usar GPU-Z ou HWiNFO como backend em produção

Rejeitado porque acoplaria a aquisição a processos, formatos e licenças de terceiros. Eles continuam sendo referências de validação independentes.

### Aceitar apenas o ID da interface

Rejeitado porque um ID privado não garante módulo, implementação, RVA ou ABI após atualização do driver.

### Publicar automaticamente no serviço

Rejeitado porque misturaria uma leitura de perfil único à telemetria documentada e faria ausência de perfil parecer ausência física do sensor.
