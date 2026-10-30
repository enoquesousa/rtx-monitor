[CmdletBinding()]
param(
    [string]$BranchName = 'codex/public-clean-snapshot',
    [switch]$CreateBranch
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$actualRoot = (& git -C $projectRoot rev-parse --show-toplevel).Trim()
$normalizedProjectRoot = $projectRoot.TrimEnd([IO.Path]::DirectorySeparatorChar)
$normalizedActualRoot = [IO.Path]::GetFullPath($actualRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($LASTEXITCODE -ne 0 -or
    -not [string]::Equals(
        $normalizedActualRoot,
        $normalizedProjectRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Execute a ferramenta somente no repositório RTX Monitor.'
}
if ($BranchName -notmatch '^codex/public-[a-z0-9-]+$') {
    throw 'A branch local deve seguir codex/public-<nome>.'
}
& git -C $projectRoot check-ref-format --branch $BranchName | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Nome de branch inválido.' }
if ($CreateBranch) {
    & git -C $projectRoot show-ref --verify --quiet "refs/heads/$BranchName"
    if ($LASTEXITCODE -eq 0) { throw 'A branch de destino já existe; nada foi substituído.' }
}

$auditRoot = Join-Path $projectRoot 'artifacts\publication'
New-Item -ItemType Directory -Path $auditRoot -Force | Out-Null
$runId = [Guid]::NewGuid().ToString('N')
$temporaryIndex = Join-Path $auditRoot "index-$runId"
$report = Join-Path $auditRoot "audit-$runId.json"
$previousIndex = [Environment]::GetEnvironmentVariable('GIT_INDEX_FILE')

try {
    # A second index stages the exact current worktree without touching the
    # caller's Git index, branch, or checkout.
    $env:GIT_INDEX_FILE = $temporaryIndex
    # Start empty: a tracked file must not bypass today's .gitignore. In
    # particular, the local reviewed-reader archive stays out of this tree.
    & git -C $projectRoot read-tree --empty
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível iniciar o índice temporário.' }
    & git -C $projectRoot add --all
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível preparar o snapshot temporário.' }
    $tree = (& git -C $projectRoot write-tree).Trim()
    if ($LASTEXITCODE -ne 0 -or $tree -notmatch '^[0-9a-f]{40,64}$') {
        throw 'Não foi possível gerar a árvore Git.'
    }

    & python -B (Join-Path $PSScriptRoot 'audit-tree.py') --tree $tree --report $report
    if ($LASTEXITCODE -ne 0) {
        throw "A auditoria recusou o snapshot. Relatório local: $report"
    }
    Write-Host "Snapshot aprovado: $tree"
    Write-Host "Relatório local: $report"

    if (-not $CreateBranch) {
        Write-Host 'Somente auditoria. Use -CreateBranch para criar a branch órfã local.'
        return
    }

    # No parent: historical local commits and existing origin/main commits
    # cannot become ancestors of this snapshot.
    $commit = (& git -C $projectRoot commit-tree $tree -m 'Create reviewed public RTX Monitor snapshot').Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40,64}$') {
        throw 'Não foi possível criar o commit órfão.'
    }
    & git -C $projectRoot update-ref "refs/heads/$BranchName" $commit ('0' * $commit.Length)
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível registrar a branch local.' }

    $line = (& git -C $projectRoot rev-list --parents -n 1 $commit).Trim()
    if ($LASTEXITCODE -ne 0 -or $line -ne $commit) {
        throw 'A branch criada não é órfã; revise-a antes de publicar.'
    }
    $branchTree = (& git -C $projectRoot rev-parse "$BranchName^{tree}").Trim()
    if ($LASTEXITCODE -ne 0 -or $branchTree -ne $tree) {
        throw 'A árvore da branch diverge do snapshot auditado.'
    }
    Write-Host "Branch órfã local criada: $BranchName ($commit)"
    Write-Host 'Nenhum checkout, push ou alteração da main foi feito.'
}
finally {
    if ($null -eq $previousIndex) {
        Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
    }
    else {
        $env:GIT_INDEX_FILE = $previousIndex
    }
}
