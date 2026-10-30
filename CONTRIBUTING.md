# Contribuindo

O RTX Monitor combina uma interface Windows nativa em Rust com coletores
C/C# e leitores revisados em Python. O objetivo é monitorar a GPU sem alterar
sua configuração. Antes de propor uma mudança, consulte o
[guia nativo](docs/NATIVE_WINDOWS.md), a [arquitetura](docs/ARCHITECTURE.md)
e os [avisos de terceiros](THIRD_PARTY_NOTICES.md).

## Preparar e verificar

Em Windows x64, instale os requisitos do guia nativo. Na raiz do repositório,
configure o preset antes de compilar:

```powershell
cmake --preset windows-x64
.\scripts\build-desktop.ps1
cargo fmt --manifest-path .\desktop\Cargo.toml --check
cargo test --manifest-path .\desktop\Cargo.toml --locked
.\scripts\verify-ci.ps1 -Configuration Release
```

O build pode baixar dependências. Para criar e auditar o instalador após o
build, execute `.\scripts\package-desktop.ps1 -SkipBuild`. Os artefatos
locais ficam em `artifacts/` e não são necessários para revisar o código.
Essas verificações não provam funcionamento em todas as placas ou drivers;
relate a versão do driver, o hardware e a disponibilidade observada quando
uma alteração tiver sido testada diretamente na placa.

## Escopo das alterações

- Mantenha o acesso de monitoramento somente leitura. Não introduza chamadas
  de configuração da GPU, como ajustes de cooler, tensão, clocks ou potência.
- Identifique campos indisponíveis, expirados e de origem desconhecida. Não
  atribua os sete canais sem localização comprovada a componentes específicos
  nem represente ausência de dados como temperatura zero.
- Os leitores em `scripts/research-readers/` têm manifesto e hashes fixados;
  alterações nos bytes revisados exigem nova revisão de contratos, proveniência
  e validação apropriada. Não amplie o perfil suportado apenas para contornar
  uma verificação.
- Em issues e pull requests, descreva a mudança, como foi verificada e suas
  limitações. Remova dados pessoais, identificadores individuais da GPU,
  logs locais e credenciais de exemplos antes de compartilhá-los.

Dados de execução, bancos, dumps, firmware e artefatos de pesquisa locais não
precisam acompanhar uma contribuição. Preserve os avisos e licenças aplicáveis
às dependências e demais materiais de terceiros.
