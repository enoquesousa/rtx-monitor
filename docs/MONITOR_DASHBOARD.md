# Monitor da RTX 3060

O painel reúne a telemetria pública do RTX Monitor e os contratos de leitura
validados na pesquisa da RTX 3060 com driver **621.01**. A interface mostra
valores atuais, evolução das leituras, fontes, horários e retornos brutos.
Uma consulta que falha ou não entrega uma saída observável aparece indisponível.

## Uso

1. Execute **`Iniciar RTX3060.cmd`**, na raiz do projeto.
2. Abra **http://127.0.0.1:3060/**. O iniciador também pode abrir o painel.
3. Aguarde a coleta inicial. As leituras públicas e as suítes de pesquisa têm
   cadências diferentes; confira a idade indicada em cada grupo.
4. Use as áreas do painel para consultar temperaturas, ventoinhas, tensão,
   potência, clocks, memória, engines do Windows e detalhes da pesquisa.
5. Nos detalhes de uma leitura, confira API, estado, unidade e dados brutos.
   A exportação JSON conserva os dados recebidos e seus estados.

Para encerrar somente o painel, execute `scripts/stop-dashboard.ps1`.
Para recompilar o pacote, encerre o painel e execute
`scripts/start-dashboard.ps1 -Build`. Esses comandos preservam os dados.

O processo do painel usa a porta local 3060 e um banco próprio em
`artifacts/dashboard/data/telemetry.db`. O pacote publicado fica em
`artifacts/dashboard/app`. O serviço instalado anteriormente mantém sua porta
e seu banco. Os arquivos de pesquisa originais são evidência preservada;
a execução do painel usa as cópias revisadas incluídas no projeto.

O atalho **RTX 3060 Monitor** na área de trabalho abre o iniciador. Para encerrar
somente o painel e seus leitores, execute `scripts/stop-dashboard.ps1`.
O histórico SQLite contém os eventos públicos; o cache privado atual fica em
`artifacts/dashboard/data/research.json` e pode ser exportado pela interface.

A API `GET /api/v1/research-telemetry?view=summary` entrega as leituras e os
contadores compactos para atualização frequente. A rota sem `view` conserva
o retorno completo, carregado ao consultar a pesquisa ou exportar um snapshot.
Os dois formatos consultam somente o cache. [Contrato JSON](schema/research-telemetry-v1.schema.json).

**Exportar snapshot** baixa um anexo JSON diretamente de
`GET /api/v1/snapshot?gpu_uuid=...`, com todos os relatórios em cache da placa
selecionada e horários individuais. A exportação não inicia consultas à GPU
nem converte os inteiros originais através dos números do JavaScript.
O histórico SQLite e a área Histórico registram os eventos públicos do serviço.
As suítes novas de pesquisa ficam no snapshot corrente `research.json` e na
exportação JSON; não são apresentadas como uma série privada já gravada no SQLite.

## Leituras reunidas

| Grupo | Conteúdo e limites |
| --- | --- |
| Temperatura pública | Temperatura GPU e demais campos publicados pela NVML. Memória ou outros alvos aparecem somente se a chamada retornar suporte e valor. |
| Temperatura por perfil | Nove canais lógicos do getter térmico validado, com inteiro original e conversão SFXP24.8 para °C. Dois têm papéis primários de média/máximo GPU; sete continuam sem origem física comprovada. |
| Ventoinhas | Percentual e RPM reportados pela NVML, além dos registros privados de cooler. Índice e ID lógico não identificam posição física ou conector da ventoinha. |
| Tensão | Getter privado do perfil 621.01. Conserva a palavra original e a fórmula experimental `/1000000`; houve concordância com HWiNFO em um patamar. Não há garantia de calibração elétrica ou validação em todos os patamares. |
| Potência e energia | Consumo, energia acumulada e limites atuais reportados. Ler um limite existente não aplica configuração. Registros privados sem semântica comprovada permanecem brutos. |
| Clocks e utilização | Domínios documentados da NVML/NVAPI, utilização GPU/memória/vídeo e clocks do cache PMUMON com unidade comprovada. Domínios privados sem unidade comprovada permanecem brutos. |
| Memória e PCIe | Memória local/compartilhada, orçamento e uso do Windows, BAR, ligação e contadores PCIe que respondem. Contadores são consultados sem limpar ou reiniciar. |
| Identidade e capacidades | UUID, PCI/subsystem, VBIOS, driver, topologia, atributos CUDA, capacidades gráficas e de vídeo. Uma capacidade publicada não equivale a um teste de execução ou a uma unidade física independente. |
| Pesquisa | Máscaras, enums, predicados, metadados, estados de cache, retornos e motivos de indisponibilidade dos contratos fixos revisados. Um bit sem nome não recebe nome físico por aproximação. |

A tabela pública nativa contém **35 tipos de campo**. Campos por índice de fan
podem gerar várias linhas. A cobertura é calculada pelos retornos da execução;
o número de tipos, de linhas, de chamadas ou de bits não é uma contagem de
sensores físicos.

## Canais térmicos

| Canal lógico | Device/provider bruto | Papel primário comprovado | Origem física |
| --- | --- | --- | --- |
| 0 | 1 / 1 | Média GPU | Localização física não comprovada |
| 1 | 0 / 6 | Máximo GPU | Localização física não comprovada |
| 2 | 1 / 0 | Sem nome atribuído | Desconhecida |
| 3 | 0 / 0 | Sem nome atribuído | Desconhecida |
| 4 | 0 / 1 | Sem nome atribuído | Desconhecida |
| 5 | 0 / 5 | Sem nome atribuído | Desconhecida |
| 6 | 0 / 7 | Sem nome atribuído | Desconhecida |
| 7 | 0 / 4 | Sem nome atribuído | Desconhecida |
| 8 | 2 / 0 | Sem nome atribuído | Desconhecida |

As temperaturas atuais de `0x65fe3aad` têm prova de unidade específica.
O histórico em cache de **`0xa7f97cf3` conserva valores brutos sem unidade**:
a conversão do getter atual não foi transferida para esse histórico.
Os campos de `0xa933ce98` são estado de cache, sem unidade; não são temperaturas.
O registro de `0xb2c9d666` é metadado de outro grupo térmico, sem identificação
de sensor físico. O blob de `0x64d6c83d`, seletor 8, inclui bytes de inicialização
do chamador preservados: seus 1.048 bytes não representam 1.048 medições.

## Fontes, compatibilidade e estados

O perfil de pesquisa exige RTX 3060, PCI `00000000:01:00.0`, vendor/device
`10de:2504`, subsystem `10de:1536`, VBIOS `94.06.25.00.fc` e driver `621.01`.
As cópias revisadas mantêm os gates de identidade, SHA-256, RVA, ABI, buffers,
máscaras e seletores. Uma divergência interrompe a respectiva aquisição.
Handles, bases ASLR e LUIDs são resolvidos durante a execução.

O perfil nativo anterior da v0.11 é fixado ao driver **610.88**. Ele continua
incompatível com 621.01; as leituras novas usam o provedor separado de pesquisa.
O painel não transforma o perfil anterior em compatível por alterar rótulos.

- **Disponível:** a chamada terminou com sucesso e entregou payload observável
  conforme o contrato. Um zero efetivamente escrito pode ser um valor válido.
- **Bruto/sem unidade:** houve saída, mas seu significado físico ou sua escala
  não foi comprovado. O detalhe conserva a estrutura e as limitações.
- **Indisponível:** erro, falta de suporte, dependência ausente, cache vazio,
  buffer inalterado ou aquisição expirada. Não se substitui por zero.
- **Coletando:** a primeira aquisição ou a próxima rotação ainda está em curso.
  Um grupo já coletado mantém seu horário próprio; a atualização da página
  não renova o horário da amostra.

As consultas HTTP leem o cache do serviço. Elas não executam uma consulta
nativa a cada acesso. As suítes de pesquisa rodam em filhos isolados,
serializados e supervisionados. A configuração da placa não é alterada;
setters, seletores de configuração, limpeza de contadores, cargas artificiais
e callbacks descobertos durante a investigação ficam fora do monitor.
O pacote integra **45 suítes fixas**: 29 do conjunto de continuação e as 16
da coleta anterior, incluindo os três modos iniciais. As leituras rápidas
possuem limite de idade de 30 segundos; os nove canais correntes, 180 segundos;
os metadados conservam sua cadência e seu horário próprios. O detalhe da suíte
informa sua validade. A publicação do envelope não renova a aquisição.

## Inventário e alcance da pesquisa

O catálogo histórico de leituras foi preservado somente no arquivo local
`evidence/publication-private/2026-10-24-monitor-readings.json` e não integra
a publicação, pois contém identificadores de sessões de pesquisa. O resumo
do seu escopo é:

- As **29 suítes / 79 IDs** do conjunto de continuação, com funções, caminhos
  dos campos JSON, unidades comprovadas, estados históricos e hashes de fonte.
- As **16 suítes anteriores**: NVML, NVAPI, topologia, CUDA, Windows, vídeo,
  contratos privados, rails de tensão, térmico, cooler e amostragem pública.
  As três suítes iniciais de temperatura/tensão/cooler se sobrepõem ao conjunto
  de modos de entrada; a amostragem pública repete APIs existentes.
- Os nove canais lógicos e a distinção entre papéis GPU e origem física.

O inventário de 29 suítes agrega **303 chamadas históricas**, incluindo
263 saídas observadas, 30 rejeições e dez sucessos sem payload disponível.
São capturas datadas de etapas diferentes, não uma aquisição simultânea atual.
O catálogo contém descrições e estados históricos; não fornece valores para
preencher lacunas de uma execução atual.

A presença de um ID no dispatcher ou de um corpo decompilado não comprova
que ele seja uma leitura, um sensor ou uma função suportada nesta placa.
A investigação dos IDs restantes e a localização física dos sete canais
continuam incompletas. O monitor integra os contratos de leitura admitidos;
não concede nomes ou suporte aos caminhos ainda desconhecidos.
