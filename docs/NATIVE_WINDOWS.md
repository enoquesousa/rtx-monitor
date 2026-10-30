# RTX 3060 Monitor para Windows

O aplicativo tem interface nativa em **Rust, egui e wgpu**, sem WebView. O
modelo 3D da placa é carregado pelo renderizador nativo. O build público inclui
o coletor C/C# e as leituras públicas, sem Python nem provas privadas. O pacote
de leitores revisados pode ser incorporado por um build privado explícito,
mantido localmente e fora do repositório público. As consultas privadas
continuam restritas ao perfil validado nesse pacote.

## Usar o modelo 3D

Abra **RTX 3060 Monitor** pelo atalho da área de trabalho ou do menu Iniciar.
As leituras atuais aparecem ao lado do modelo, com disponibilidade explícita.

- **Montada:** reúne as peças, mostra todas as camadas e volta à câmera
  isométrica, sem rótulos sobrepostos.
- **Detalhe das ventoinhas:** aproxima a câmera dos dois rotores, com a placa
  montada. É útil para inspecionar o desenho das pás.
- **Animação por RPM:** liga ou pausa somente o movimento visual das hélices.
  Desligar essa opção não para os coolers reais nem interrompe as leituras;
  o RPM informado continua sendo atualizado. Em alta rotação, a exposição
  temporal do renderizador mostra borrão de movimento para reduzir o efeito
  de pás aparentemente paradas. É uma representação visual, sem medir a fase
  das hélices nem alterar sua rotação real.
- **Vista explodida** e **Explosão:** separam as peças; o controle deslizante
  ajusta a distância entre elas.
- **Rótulos** e **Camadas:** mostram os nomes e permitem exibir ou ocultar
  PCB, dissipador, ventoinhas, carenagem e backplate. Clique em uma peça ou
  use a lista de seleção para consultar seus detalhes.
- **Câmera:** arraste para orbitar, use o botão direito para mover e a roda
  para aproximar. Os botões Isométrica, Frente, Verso e Lateral escolhem vistas
  prontas; **Redefinir câmera** desfaz os ajustes de enquadramento.

Na **0.12.2**, a temperatura média da GPU e o máximo/hotspot permanecem em
um quadro sobre a visualização 3D, inclusive com a placa montada e os rótulos
desligados. A linha aponta para o componente GPU sob o dissipador; ela não
localiza o ponto exato do hotspot. **Ver região medida** oculta as camadas
que cobrem a GPU e ativa sua cor térmica.

O quadro **Canais térmicos** exibe os nove índices e a idade da coleta. Os
índices **02–08** são os sete canais com origem física desconhecida e não são
posicionados em componentes. Leituras ausentes ou expiradas aparecem sem
valor; isso não significa temperatura zero.

Se o coletor revisado encerrar inesperadamente, o supervisor retira seus
valores e tenta iniciá-lo novamente, com espera progressiva de até 30 segundos
entre tentativas. O relatório da sessão anterior não é reapresentado como
uma leitura atual; os números voltam quando a nova sessão publica dados válidos.

Na 0.12.1, o refinamento das ventoinhas e da placa montada conserva as **29
peças**, seus identificadores e os mesmos vínculos de leitura. O detalhe
visual novo não acrescenta sensores nem muda a origem dos canais térmicos.

Para gerar uma imagem do próprio renderizador sem iniciar os coletores:

```powershell
.\artifacts\desktop\dist-public\rtx3060-monitor.exe --render-preview .\artifacts\desktop\preview-montada.png
.\artifacts\desktop\dist-public\rtx3060-monitor.exe --render-preview .\artifacts\desktop\preview-explodida.png --exploded
```

Esse modo renderiza o modelo com as mesmas malhas e materiais. Não consulta
os sensores, não atribui temperaturas inventadas às peças e não produz uma
fotografia da placa instalada. A imagem serve para conferir a visualização
3D; não é uma captura de termografia nem evidência da temperatura física.

## Compilar e criar o MSI

Requisitos de desenvolvimento: Windows x64, PowerShell 7, Rust com toolchain
MSVC, CMake 3.25 ou superior, Visual Studio 2022 Build Tools com C/C++ e .NET
SDK 8 ou posterior. O usuário final não precisa do SDK Rust, de Python global
nem do .NET SDK para executar o pacote. Execute os comandos na raiz do checkout.
A primeira compilação precisa de acesso à rede para baixar as dependências e
as ferramentas portáteis; os scripts conferem os hashes dos arquivos fixados.
O build padrão é público, sem os leitores de pesquisa. Para o modo privado
local, consulte [PUBLIC_BUILD.md](PUBLIC_BUILD.md); ele requer o pacote de
pesquisa separado e o parâmetro `-IncludePrivateReaders`.

```powershell
# Obrigatório em um checkout novo: o build usa o preset já configurado.
cmake --preset windows-x64
.\scripts\build-desktop.ps1
.\scripts\package-desktop.ps1 -SkipBuild
```

A configuração cria `build/windows-x64`; o script de build prepara
`artifacts/desktop/dist-public/rtx3060-monitor.exe` e `runtime/`. O script de pacote cria o MSI em
`artifacts/desktop/installer-public/RTX3060Monitor-0.12.4-x64.msi`, junto com o SHA-256,
o manifesto dos arquivos e `msi-audit.json`. A geração do MSI exige os bytes
reais do aplicativo; a versão `0.0.0` em `installer-tests/` é somente uma
fixture de testes e não é o instalador de distribuição.

O compilador WiX 3.14.1 é baixado como ferramenta portátil do
[release oficial](https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm).
O ZIP é verificado por tamanho e pelo hash registrado no script; nenhuma
ferramenta WiX é instalada globalmente. O GitHub não publica um digest nesse
asset; o hash fixado registra os bytes obtidos do release e revisados neste
projeto, sem alegar uma assinatura digital do distribuidor.

### Verificações locais

```powershell
cargo fmt --manifest-path .\desktop\Cargo.toml --check
cargo test --manifest-path .\desktop\Cargo.toml --locked
.\scripts\verify-ci.ps1 -Configuration Release
```

Os testes Rust verificam a aplicação nativa; `verify-ci.ps1` executa as
verificações existentes dos componentes C/C++ e .NET. Eles não substituem
uma validação de leituras em hardware compatível. O workflow de CI disponível
no repositório verifica os componentes existentes; consulte sua definição para
saber exatamente quais etapas são executadas.

## Instalação por usuário

A versão 0.12.3 incorpora a proteção da API local, o ajuste de cadência do
coletor experimental e a recuperação supervisionada quando há leitores privados. O instalador também
inclui `rtx-monitor-art.png` na pasta do aplicativo; essa arte é um arquivo
de identidade do projeto e não altera a interface ou as leituras da GPU.

O build público 0.12.4 omite o coletor privado. Se instalado sobre o MSI local
0.12.3 com o pacote privado, ele substitui essa instalação; seu próprio coletor
deixa de fornecer essas leituras. Um serviço local separado já ativo ainda pode
fornecê-las à interface. Os dados permanecem fora da pasta instalada.

O MSI instala os arquivos em
`%LOCALAPPDATA%\Programs\Rtx3060Monitor` e cria atalhos para o executável
nativo no menu Iniciar e na área de trabalho. O pacote é exclusivamente por
usuário, com componentes x64 e chaves de controle em
`HKCU\Software\RtxMonitor\NativeDesktop`.

O instalador não cria serviços Windows, não instala drivers, não modifica
PATH e não executa o aplicativo durante a instalação. O serviço anterior
`RtxMonitorService` e os bancos existentes do projeto não pertencem a este MSI.
A aplicação pode consultar o backend compatível já ativo; quando precisa de
seu próprio coletor, ele roda oculto como processo do aplicativo, sem abrir um
navegador. O transporte JSON local não é uma interface web do usuário.

Os dados do aplicativo ficam em `%LOCALAPPDATA%\Rtx3060Monitor\data`, fora da
pasta de instalação. A desinstalação remove somente os arquivos registrados,
os atalhos e os valores de registro deste pacote. Não há limpeza recursiva ou
curingas para apagar bancos, exportações ou dados do usuário.

Para publicar outra versão, altere a versão do aplicativo e passe a mesma
versão para `package-desktop.ps1 -Version`. Atualizações usam o UpgradeCode
próprio do aplicativo nativo. Versões diferentes do payload devem ter uma
nova versão de lançamento; o pacote não força substituições de mesma versão.

A versão pública **0.12.4** atualiza a **0.12.3** no mesmo escopo por usuário. Antes de
executar o MSI de atualização, encerre o aplicativo nativo e aguarde a saída
do coletor que ele próprio iniciou. Binários abertos podem bloquear a troca
de arquivos ou levar o Windows Installer a solicitar uma reinicialização.
Um backend compartilhado ou o serviço anterior não deve ser encerrado como
se fosse um processo pertencente ao aplicativo.

O `MajorUpgrade` usa o mesmo UpgradeCode e agenda a remoção da versão anterior
após `InstallInitialize`, dentro da transação de instalação. O instalador
não contém ações para matar processos; a verificação e o encerramento do
aplicativo são feitos antes da atualização. O diretório de dados fica fora
da pasta atualizada e não entra na remoção do produto anterior. Se a nova
instalação falhar, o Windows Installer pode reverter a transação; preservar
os dados não depende de apagar ou migrar o banco durante o MSI.

## Modelo e dados da placa

O modelo segue as referências fornecidas e permite separar suas peças na
visão explodida. Proporções reconstruídas de imagens não são medições
dimensionais nem um desenho de fabricação certificado.

- A temperatura do chip gráfico pode colorir a região da GPU. O máximo
  térmico é um valor do chip, sem coordenada de hotspot conhecida.
- Os sete canais adicionais continuam sem localização física comprovada.
  Eles não são atribuídos à VRAM, VRM, heatpipes ou dissipador por proximidade.
- Peças sem medição localizada conservam esse estado; a cor de uma peça sem
  sensor não comprova que ela esteja fria.
- A animação das ventoinhas usa o RPM reportado pelo driver. Os índices NVML
  não comprovam sua correspondência à ventoinha esquerda ou direita; a posição
  visual usa uma convenção identificada. O retorno NVML de RPM pretendido não
  é apresentado como certificação de tacômetro físico.
- Animação em uma tela é amostrada pela frequência de quadros: fase aparente
  e aliasing visual não são uma leitura da posição angular da hélice real.

O aplicativo mantém o acesso à placa somente leitura. Não aplica curvas de
fan, tensão, offsets, clocks ou limites de potência. Valores sem suporte,
expirados ou rejeitados permanecem indisponíveis, com sua origem e horário.

## Auditoria do pacote

`desktop/installer/audit-msi.ps1` abre o MSI somente para leitura e verifica
as tabelas reais: escopo por usuário, destinos, componentes x64, chaves HKCU,
atalhos, ausência de ações personalizadas/serviços/drivers e ausência de
remoção de arquivos por curingas. Em seguida, extrai os CABs e compara cada
arquivo com os hashes do manifesto. A auditoria não instala o produto.

O linker mantém a validação ICE e trata seus avisos como erros, com a única
exceção explícita de **ICE91**: essa regra avisa sobre propagação de arquivos
de perfil em instalações por máquina, modalidade que este pacote não oferece.
A auditoria verifica separadamente que `ALLUSERS` está ausente. Os diretórios
do pacote só recebem remoção não recursiva quando ficam vazios.

A biblioteca nativa `e_sqlite3.dll` não publica uma lista de idiomas utilizável
pelo MSI. O autor do pacote declara um idioma neutro de fallback (`0`), sem
alterar a DLL. O aviso de linker 1101 dessa declaração explícita é excluído;
a validação ICE60 permanece ativa.

Os arquivos imutáveis exigidos pelos contratos de pesquisa permanecem
idênticos. Provas `.log` ou `.html` só podem entrar no pacote quando estão
explicitamente listadas no manifesto dos leitores e seu SHA-256 corresponde.
`wwwroot`, dados de execução, bancos, `__pycache__` e `_runtime` ficam fora do
pacote. O modelo embarcado, o código fonte e os relatórios de validação são
mantidos separadamente no projeto.

Fontes técnicas: [escopo de instalação WiX](https://docs.firegiant.com/wix3/xsd/wix/package/),
[atualizações MSI](https://docs.firegiant.com/wix3/xsd/wix/majorupgrade/).
