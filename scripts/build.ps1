[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$configurationLower = $Configuration.ToLowerInvariant()
$nativeOutput = Join-Path $projectRoot "build\windows-x64\bin\$Configuration"
$managedProject = Join-Path $projectRoot 'csharp\RtxMonitor.Console\RtxMonitor.Console.csproj'
$serviceProject = Join-Path $projectRoot 'csharp\RtxMonitor.Service\RtxMonitor.Service.csproj'
$labProject = Join-Path $projectRoot 'csharp\RtxMonitor.Lab\RtxMonitor.Lab.csproj'

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
    Invoke-Checked -Description 'CMake configure' -Command {
        & cmake --preset windows-x64
    }

    Invoke-Checked -Description 'Native C/C++ build' -Command {
        & cmake --build --preset "windows-x64-$configurationLower"
    }

    Invoke-Checked -Description '.NET build' -Command {
        & dotnet build $managedProject `
            --configuration $Configuration `
            --nologo `
            "-p:NativeLibraryDir=$nativeOutput"
    }

    Invoke-Checked -Description '.NET service build' -Command {
        & dotnet build $serviceProject `
            --configuration $Configuration `
            --nologo `
            "-p:NativeLibraryDir=$nativeOutput"
    }

    Invoke-Checked -Description '.NET laboratory build' -Command {
        & dotnet build $labProject `
            --configuration $Configuration `
            --nologo
    }

    Write-Host "Build completed: $Configuration"
    Write-Host "C/C++: $nativeOutput"
    Write-Host "C#: $(Join-Path $projectRoot "csharp\RtxMonitor.Console\bin\$Configuration\net8.0")"
    Write-Host "Service: $(Join-Path $projectRoot "csharp\RtxMonitor.Service\bin\$Configuration\net8.0-windows\win-x64")"
    Write-Host "Laboratory: $(Join-Path $projectRoot "csharp\RtxMonitor.Lab\bin\$Configuration\net8.0")"
}
finally {
    Pop-Location
}
