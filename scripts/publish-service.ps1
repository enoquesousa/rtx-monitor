[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory,

    [switch]$SkipNativeBuild
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$configurationLower = $Configuration.ToLowerInvariant()
$nativeOutput = Join-Path $projectRoot "build\windows-x64\bin\$Configuration"
$nativeLibrary = Join-Path $nativeOutput 'rtxmon_native.dll'
$serviceProject = Join-Path $projectRoot 'csharp\RtxMonitor.Service\RtxMonitor.Service.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'artifacts\service\win-x64'
}
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Command,

        [Parameter(Mandatory)]
        [string]$Description
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

Push-Location -LiteralPath $projectRoot
try {
    if (-not $SkipNativeBuild) {
        Invoke-Checked -Description 'CMake configure' -Command {
            & cmake --preset windows-x64
        }
        Invoke-Checked -Description 'Native C/C++ build' -Command {
            & cmake --build --preset "windows-x64-$configurationLower"
        }
    }
    if (-not (Test-Path -LiteralPath $nativeLibrary -PathType Leaf)) {
        throw "Biblioteca nativa ausente: $nativeLibrary"
    }

    Invoke-Checked -Description '.NET service publish' -Command {
        & dotnet publish $serviceProject `
            --configuration $Configuration `
            --runtime win-x64 `
            --self-contained false `
            --output $resolvedOutput `
            --nologo `
            "-p:NativeLibraryDir=$nativeOutput"
    }

    foreach ($requiredFile in @(
        'RtxMonitor.Service.exe',
        'rtxmon_native.dll',
        'appsettings.json'
    )) {
        $requiredPath = Join-Path $resolvedOutput $requiredFile
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Publicação incompleta; arquivo ausente: $requiredPath"
        }
    }

    Write-Host "Service published: $resolvedOutput"
}
finally {
    Pop-Location
}
