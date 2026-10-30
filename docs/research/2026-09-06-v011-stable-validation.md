> Registro da entrega local inicial. A continuação com teste prolongado, diagnóstico opcional e atualização do serviço está em [validação operacional](2026-09-06-v011-operational-validation.md). O pacote e a preservação da 0.6.0 descritos abaixo pertencem à etapa anterior ao corte.

> Nota de publicação: o identificador da GPU foi substituído por um valor sintético; os resultados históricos e os limites do perfil permanecem descritos.


# v0.11.0 — entrega estável para o perfil validado

Data: 2026-09-06. Produto 0.11.0, ABI8, Windows x64.

A entrega local foi validada sob o critério `v011-source-fidelity-1`, autorizado
pelo proprietário com **“autorizo, pode seguir”** após a proposta de correção
do aceite. O provedor obtém hotspot e tensão diretamente do driver, com bruto,
fórmula e proveniência preservados. O suporte abrange exclusivamente a GPU,
VBIOS, driver e módulo fixados no perfil. Estabilidade do provedor não amplia
esse suporte nem representa certificação da precisão física do sensor.

## Correções e contrato

- ABI8 preserva o inteiro térmico fixed8 e os limites nativos da aquisição;
  a conversão usa `raw / 256`, sem arredondamento intermediário ou offset.
- Supervisão isola inicialização, diagnóstico e aquisição. Falhas, revogação,
  identidade divergente, timeout e expiração retiram valores.
- A cadência usa início a início e não acumula atraso de diagnóstico.
- A API só publica números confirmados no SQLite. Repetir consulta ou commit
  não rejuvenesce a aquisição. Exportação mantém os bytes armazenados.
- O novo relatório por perfil v1 usa origem `private_profile`, fonte
  `direct_nvidia_driver` e escopo obrigatório das tolerâncias. Eventos v8
  transportam `profile_telemetry`. O modo padrão permanece em v5.
- A rota é `/api/v1/gpus/{gpuUuid}/profile-telemetry`. A configuração canônica
  usa `ProfileTelemetryEnabled` e `ProfileIntervalMilliseconds`. Os aliases
  anteriores são exclusivos; ambiguidades são rejeitadas.
- Os schemas históricos e a leitura/exportação de eventos v1–v7 permanecem
  preservados. A rota do candidato foi substituída, com migração documentada.

## Evidência final do pacote

Protocolo definido antes da captura: `evidence/v011-stable-20260906/protocol.md`.
Os registros completos, inclusive respostas e intervalos individuais, ficam
nesse diretório. A execução não escolheu uma subjanela favorável.

| Verificação | Resultado |
| --- | --- |
| Janela fixa | 90 segundos, cadência de aquisição 1.000 ms |
| Respostas numéricas HTTP | 782/782 já presentes no SQLite imediatamente após a resposta |
| Aquisições distintas | 90 por sensor; ambos disponíveis em todas as respostas |
| Maior intervalo entre aquisições | 1.011 ms por sensor; limite declarado 2.000 ms |
| SSE | 89 eventos iguais ao histórico e à exportação |
| Exportação | 130 registros com JSON armazenado preservado byte a byte |
| Bruto, fórmula, identidade, módulo e tempos | Aprovados; status nativo 0 |
| Schemas | 89 envelopes SSE e 130 registros exportados válidos |
| Reinício no mesmo banco | 130 eventos anteriores preservados, novo run e novas aquisições confirmadas |
| Pacote padrão | Saudável, campos desativados, eventos v5, nenhum filho privado |
| Integridade do ZIP | CRC, conjunto exato e hashes aprovados: 41 arquivos + manifesto |

Fonte: `publication-validation.json`, `continuity-validation.json`,
`transport-validation.json`, `schema-validation.json`, `restart-validation.json`,
`default-validation.json` e `package-integrity.json`.

A verificação completa Windows passou com 45 CTest, 14 testes de auditoria,
suítes .NET, 39 casos de subprocesso Console, testes PowerShell, schemas,
formatação e paridade de versão. Os testes incluem inserção SQLite abortada,
expiração, revogação, identidade incompatível, cancelamento, processos presos,
reinício e compatibilidade histórica. A regressão do contrato usa um relatório
real do pacote e rejeita ausência de escopo ou promessa de precisão física.
Logs: `windows-ci-r3.log` e `windows-ci-final.log`.

Falhas intermediárias foram preservadas: finais de linha e espaçamento foram
corrigidos após rejeição de formatação; uma condição do schema de tensão ainda
exigia o objeto antigo de tolerância e foi atualizada. Essa última correção
afetou somente o schema, sem alterar pacote, leitura ou janela de captura.

## Independência e alcance

O serviço inicia seu próprio executável para diagnóstico e leitura. O worker
abre `NvidiaMonitor` e chama o backend nativo. A seleção de operação é fechada,
e os módulos NVIDIA são verificados pelo perfil. A revisão do caminho de
execução e o inventário `owned-processes-during.json` confirmam o processo do
serviço e os filhos próprios `thermal-raw`/`voltage`. Não há ponte de valores
com GPU-Z/HWiNFO, leitura de seus logs ou IPC de referência no produto.

O suporte validado é a Galax RTX 3060 UUID
`GPU-11111111-2222-3333-4444-555555555555`, PCI `10de:2504`, subsystem
`10de:1536`, VBIOS `94.06.25.00.fc`, driver `610.88` e NVML `13.610.88`.
Módulo NVAPI x64 SHA256:
`df6455ccf83e43cfe68f405af1eec4e053c7f95da998bf358053b7583980c2f4`.
Qualquer mudança exige nova validação e bloqueia a aquisição existente.

Não foi executada validação Linux da ABI8 nesta entrega. Os resultados Linux
da v0.10 são históricos e não são atribuídos ao pacote atual. O pacote exige
runtime .NET8 compatível e foi validado no Windows local. Cooler privado
permanece `raw_unknown`; a cobertura pública não foi aumentada por este canal.

## Comparativos térmicos preservados

O critério original de concordância entre leitores assíncronos reprovou
hotspot em 50/60 pares acima de 0,051 °C, máximo 0,6375 °C. A análise posterior
com horários individuais também encontrou 5/9 pares acima desse limite,
máximo 0,37525 °C. Esses resultados continuam registrados como diagnóstico;
não foram convertidos em aprovação nem apagados.

Os 18 retornos da própria referência ficaram dentro de 0,051 °C na comparação
de arredondamento da mesma aquisição. A autorização alterou o critério de
entrega para fidelidade à fonte e comportamento do provedor. Não há offset,
tolerância ampliada, correção numérica presumida ou garantia de acurácia física.
[Pesquisa, critérios e autorização](2026-09-06-v011-stabilization.md).

## Artefato e preservação

Pacote: `artifacts/v0.11.0-service-stable-profile-local.zip`.
SHA256: `da9bfe33baa26bcce12ac3fb3726719f6b2251acb4839df922283310d667735b`.
Extração verificada: `artifacts/v0.11.0-service-stable-profile-extracted`.
[Como executar e migrar](../PROFILE_TELEMETRY.md).

As instâncias de teste foram encerradas. O serviço instalado 0.6.0 permaneceu
saudável na porta 5136, PID9144 e início `1788697783846`. GPU-Z24380 e
HWiNFO25136 foram preservados. Evidência: `preservation-after.json`.
Não houve instalação, publicação externa, push, tag ou alteração da GPU.
