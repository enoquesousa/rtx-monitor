[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$configurationLower = $Configuration.ToLowerInvariant()
$nativeOutput = Join-Path $projectRoot "build\windows-x64\bin\$Configuration"
$cppExecutable = Join-Path $nativeOutput 'rtxmon.exe'
$cExecutable = Join-Path $nativeOutput 'rtxmon-c.exe'
$csharpExecutable = Join-Path $projectRoot "csharp\RtxMonitor.Console\bin\$Configuration\net8.0\RtxMonitor.Console.exe"

function Assert-LastExitCode {
    param([Parameter(Mandatory)][string]$Description)

    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Assert-Temperature {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][int]$Temperature
    )

    if ($Temperature -lt -50 -or $Temperature -gt 150) {
        throw "$Source returned an implausible GPU temperature: $Temperature C."
    }
}

Push-Location -LiteralPath $projectRoot
try {
    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
    }

    & ctest --preset "windows-x64-$configurationLower"
    Assert-LastExitCode -Description 'CTest'

    $cOutput = (& $cExecutable | Out-String)
    Assert-LastExitCode -Description 'C example'
    if ($cOutput -notmatch 'GPU die temperature:\s*(-?\d+) C') {
        throw 'The C example did not expose a GPU die temperature.'
    }
    $cTemperature = [int]$Matches[1]

    $cppOutput = (& $cppExecutable --once --json | Out-String).Trim()
    Assert-LastExitCode -Description 'C++ CLI'
    $cppSample = $cppOutput | ConvertFrom-Json

    $csharpOutput = (& $csharpExecutable --once --json | Out-String).Trim()
    Assert-LastExitCode -Description 'C# monitor'
    $csharpSample = $csharpOutput | ConvertFrom-Json

    $smiOutput = (& nvidia-smi `
        --query-gpu=index,uuid,temperature.gpu `
        --format=csv,noheader,nounits | Select-Object -First 1)
    Assert-LastExitCode -Description 'nvidia-smi reference query'
    $smiFields = $smiOutput -split ',' | ForEach-Object { $_.Trim() }
    if ($smiFields.Count -ne 3) {
        throw "Unexpected nvidia-smi output: $smiOutput"
    }
    $smiTemperature = [int]$smiFields[2]

    Assert-Temperature -Source 'C' -Temperature $cTemperature
    Assert-Temperature -Source 'C++' -Temperature ([int]$cppSample.temperature_c)
    Assert-Temperature -Source 'C#' -Temperature ([int]$csharpSample.temperature_c)
    Assert-Temperature -Source 'nvidia-smi' -Temperature $smiTemperature

    if ($cppSample.gpu_uuid -ne $csharpSample.gpu_uuid -or $cppSample.gpu_uuid -ne $smiFields[1]) {
        throw 'C++, C#, and nvidia-smi did not address the same GPU UUID.'
    }

    if ($cppSample.sensor -ne 'gpu_die' -or $csharpSample.sensor -ne 'gpu_die') {
        throw 'A consumer did not identify the reading as the GPU die sensor.'
    }

    $temperatures = @(
        $cTemperature,
        [int]$cppSample.temperature_c,
        [int]$csharpSample.temperature_c,
        $smiTemperature
    )
    $minimum = ($temperatures | Measure-Object -Minimum).Minimum
    $maximum = ($temperatures | Measure-Object -Maximum).Maximum
    if (($maximum - $minimum) -gt 5) {
        throw "Sequential readers differed by more than 5 C: $($temperatures -join ', ')."
    }

    $cppWatch = @(& $cppExecutable --watch --count 2 --interval 100 --json)
    Assert-LastExitCode -Description 'C++ watch mode'
    if ($cppWatch.Count -ne 2) {
        throw "C++ watch mode produced $($cppWatch.Count) samples instead of 2."
    }

    $csharpWatch = @(& $csharpExecutable --watch --count 2 --interval 100 --json)
    Assert-LastExitCode -Description 'C# watch mode'
    if ($csharpWatch.Count -ne 2) {
        throw "C# watch mode produced $($csharpWatch.Count) samples instead of 2."
    }

    Write-Host 'Verification passed.'
    Write-Host "GPU: $($cppSample.gpu_name)"
    Write-Host "UUID: $($cppSample.gpu_uuid)"
    Write-Host "C / C++ / C# / nvidia-smi: $($temperatures -join ' / ') C"
    Write-Host "Backend: $($cppSample.backend)"
}
finally {
    Pop-Location
}
