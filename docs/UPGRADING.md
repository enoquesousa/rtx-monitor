# Atualização reversível do Windows Service

O procedimento usa uma pasta por versão, preserva o banco e altera somente
o caminho do executável no Service Control Manager. Não remove/recria o
serviço e não troca sua conta, inicialização ou política de recuperação.

## Preparação

1. Valide o pacote e sua compatibilidade com a GPU e o driver instalados.
2. Faça um backup consistente pelo mecanismo de backup do SQLite. Copiar
   somente `telemetry.db` enquanto o serviço grava pode omitir dados do WAL.
   Mantenha uma transação de leitura durante backups paginados de um banco
   que recebe gravações contínuas, limitando a duração dessa operação.
3. Confirme integridade e hashes do backup e dos binários anteriores.
4. Teste a nova versão e a reversão com uma cópia do banco. Coordene testes
   de suspensão e reinício do Windows separadamente, após salvar o trabalho.

## Conferir o plano sem alterar a instalação

Na raiz do projeto, execute:

```powershell
.\scripts\update-service.ps1 `
  -PackageArchive .\artifacts\v0.11.0-service-stable-profile-diagnostics-local.zip `
  -ExpectedSha256 09fc23d11c036cde335e429fc0fce32bfd7244ebbbc109cc25ba4a71dd717c36 `
  -EnableProfileTelemetry
```

Sem `-Apply`, o script apenas valida e apresenta o plano. Ele exige serviço
saudável, porta loopback pertencente ao PID correto, schema SQLite1, pacote
ABI8, versão e manifesto compatíveis, conjunto exato de arquivos e hashes.
Arquivos adicionais, caminhos relativos que escapem do destino, arquivos
adulterados e diretório de instalação já existente são recusados.

O updater atual suporta o caminho SCM documentado com um único executável
entre aspas, sem argumentos adicionais. Instalações personalizadas precisam
de revisão específica. Isso evita descartar argumentos silenciosamente.

## Aplicação

Depois de concluir a preparação, execute o mesmo comando em PowerShell
elevado, acrescentando `-Apply`. O script:

- extrai o pacote em uma pasta nova ao lado da versão existente;
- preserva a configuração do operador, fixa o banco no caminho absoluto
  atual e define explicitamente a ativação do provedor;
- para o serviço e guarda uma cópia dos arquivos SQLite presentes e da
  configuração anterior, com hashes, em `backups` ao lado do banco;
- altera somente `binPath`, inicia a nova versão e exige saúde e os dois
  sensores do perfil, quando habilitados, em até 60 segundos;
- registra fases, caminhos, hashes e resultado em `update.json`.

O `appsettings.json` instalado incorpora a configuração anterior e portanto
difere do arquivo padrão do ZIP. Seu hash é registrado separadamente. O
manifesto do pacote continua identificando o conteúdo original distribuído.

Se a troca falhar, o script restaura o caminho SCM anterior e verifica a
saúde da versão anterior. Não apaga o banco nem restaura automaticamente um
backup sobre dados mais recentes. Uma falha na reversão é registrada como
`rollback_failed`, com o erro original e o de recuperação.

## Reversão posterior

Use o `old_binary_path` registrado em `update.json`: pare o serviço, restaure
esse caminho com `sc.exe config`, inicie e confira `/health` e o histórico.
Os binários e a configuração antigos permanecem disponíveis. A reversão
de binário só é adequada quando a compatibilidade do banco foi demonstrada.

Restaurar uma cópia antiga do banco é uma operação separada, pois elimina da
visão ativa os registros posteriores ao backup. Antes disso, preserve o
banco atual com o serviço parado. Não misture WAL de sessões diferentes e
não reutilize um arquivo SHM antigo como dado persistente.

O updater não reinicia o Windows, não altera o driver e não publica a release.
Os [resultados operacionais da v0.11](research/2026-09-06-v011-operational-validation.md)
distinguem ensaio sobre cópia, Windows Service temporário e instalação de produção.
