# Publicar o snapshot auditado

O build público está descrito em [PUBLIC_BUILD.md](PUBLIC_BUILD.md). Ele usa as
leituras públicas e não inclui o pacote de leitores privados. O build privado
permanece disponível localmente com `-IncludePrivateReaders`; o pacote privado
e suas provas não pertencem ao snapshot de publicação.

## Auditoria do conteúdo

Execute na raiz do projeto, em PowerShell 7, com Git e Python 3:

```powershell
.\scripts\publication\Prepare-PublicSnapshot.ps1
```

O comando cria um índice Git temporário em `artifacts/publication/` e examina
a árvore exata que seria publicada. Ele deixa a branch, o índice normal e o
checkout intactos. A auditoria verifica a lista permitida de tipos de arquivo,
caminhos de usuários, UUIDs de GPU, formatos comuns de chave privada e token,
arquivos locais proibidos e materiais de pesquisa privados. Ela registra hashes
SHA-256 de todos os arquivos em um relatório local. Uma falha impede a
publicação. Revise também direitos de redistribuição e dados que não se
encaixem nesses padrões; uma varredura automática não substitui essa revisão.

## Destino da publicação

O destino é `https://github.com/enoquesousa/rtx-monitor`. Na recriação desse
repositório, a nova `main` deve conter a árvore Git auditada, sem ancestrais da
linha de pesquisa. Depois de qualquer edição, repita a auditoria e confira o
hash da árvore final antes do envio. Após publicar, confirme no GitHub o hash
do commit, a árvore e os arquivos da `main`.

Remover o repositório antigo não revoga clones, backups ou outras cópias que
terceiros já possam ter obtido. Trate conteúdo anteriormente publicado como
divulgado, mesmo após a recriação no mesmo endereço.

Não envie a branch de pesquisa anterior: seus commits contêm os arquivos
privados originais mesmo que uma alteração posterior os remova da árvore.
