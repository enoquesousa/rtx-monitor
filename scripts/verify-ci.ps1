[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$configurationLower = $Configuration.ToLowerInvariant()
$nativeOutput = Join-Path $projectRoot "build\windows-x64\bin\$Configuration"
$cppExecutable = Join-Path $nativeOutput 'rtxmon.exe'
$csharpExecutable = Join-Path $projectRoot "csharp\RtxMonitor.Console\bin\$Configuration\net8.0\RtxMonitor.Console.exe"
$managedTestProject = Join-Path $projectRoot 'csharp\RtxMonitor.Managed.Tests\RtxMonitor.Managed.Tests.csproj'
$storageTestProject = Join-Path $projectRoot 'csharp\RtxMonitor.Storage.Tests\RtxMonitor.Storage.Tests.csproj'
$managedProject = Join-Path $projectRoot 'csharp\RtxMonitor.Managed\RtxMonitor.Managed.csproj'
$storageProject = Join-Path $projectRoot 'csharp\RtxMonitor.Storage\RtxMonitor.Storage.csproj'
$consoleProject = Join-Path $projectRoot 'csharp\RtxMonitor.Console\RtxMonitor.Console.csproj'
$managedAssembly = Join-Path $projectRoot "csharp\RtxMonitor.Managed\bin\$Configuration\net8.0\RtxMonitor.Managed.dll"
$storageAssembly = Join-Path $projectRoot "csharp\RtxMonitor.Storage\bin\$Configuration\net8.0\RtxMonitor.Storage.dll"
$capabilitySchemaPath = Join-Path $projectRoot 'docs\schema\capabilities-v2.schema.json'
$eventSchemaV1Path = Join-Path $projectRoot 'docs\schema\telemetry-event-v1.schema.json'
$eventSchemaPath = Join-Path $projectRoot 'docs\schema\telemetry-event-v2.schema.json'
$evidenceSchemaPath = Join-Path $projectRoot 'docs\schema\evidence-record-v1.schema.json'

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
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration

    Invoke-Checked -Description 'Managed test build' -Command {
        & dotnet build $managedTestProject --configuration $Configuration --nologo
    }

    Invoke-Checked -Description 'Storage test build' -Command {
        & dotnet build $storageTestProject --configuration $Configuration --nologo
    }

    Invoke-Checked -Description 'Native and C++ tests' -Command {
        & ctest --preset "windows-x64-$configurationLower"
    }

    Invoke-Checked -Description 'Managed tests' -Command {
        & dotnet run `
            --project $managedTestProject `
            --configuration $Configuration `
            --no-build
    }

    Invoke-Checked -Description 'Storage tests' -Command {
        & dotnet run `
            --project $storageTestProject `
            --configuration $Configuration `
            --no-build
    }

    foreach ($project in @(
        $managedProject,
        $storageProject,
        $consoleProject,
        $managedTestProject,
        $storageTestProject
    )) {
        Invoke-Checked -Description "dotnet format $project" -Command {
            & dotnet format $project --verify-no-changes --no-restore
        }
    }

    $capabilitySchema = Get-Content -Raw -LiteralPath $capabilitySchemaPath | ConvertFrom-Json
    if ($capabilitySchema.properties.schema_version.const -ne 2) {
        throw 'Capability schema must declare schema_version const 2.'
    }

    $eventSchemaV1 = Get-Content -Raw -LiteralPath $eventSchemaV1Path | ConvertFrom-Json
    if ($eventSchemaV1.properties.schema_version.const -ne 1) {
        throw 'Telemetry event schema v1 must remain available and declare schema_version const 1.'
    }

    $eventSchema = Get-Content -Raw -LiteralPath $eventSchemaPath | ConvertFrom-Json
    if ($eventSchema.properties.schema_version.const -ne 2) {
        throw 'Telemetry event schema must declare schema_version const 2.'
    }

    $eventTypes = @($eventSchema.properties.event_type.enum)
    foreach ($requiredEvent in @('sample', 'gap', 'recovered', 'alert_raised', 'alert_cleared')) {
        if ($requiredEvent -notin $eventTypes) {
            throw "Telemetry event schema is missing '$requiredEvent'."
        }
    }

    $evidenceSchema = Get-Content -Raw -LiteralPath $evidenceSchemaPath | ConvertFrom-Json
    if ($evidenceSchema.properties.evidence_schema_version.const -ne 1 -or
        $evidenceSchema.properties.store_schema_version.const -ne 1 -or
        $evidenceSchema.properties.event.'$ref' -ne 'telemetry-event-v2.schema.json') {
        throw 'Evidence schema must declare evidence/store version 1 and embed telemetry event v2.'
    }

    $cmakeProject = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'CMakeLists.txt')
    if ($cmakeProject -notmatch '(?s)project\(\s*rtx-monitor\s+VERSION\s+([0-9]+\.[0-9]+\.[0-9]+)') {
        throw 'Could not read the native project version from CMakeLists.txt.'
    }
    $nativeVersion = $Matches[1]

    [xml]$managedProps = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'csharp\Directory.Build.props')
    $managedVersion = [string]$managedProps.Project.PropertyGroup.Version
    $managedAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($managedAssembly).Version.ToString(3)
    $storageAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($storageAssembly).Version.ToString(3)
    if ($managedVersion -ne $nativeVersion -or
        $managedAssemblyVersion -ne $nativeVersion -or
        $storageAssemblyVersion -ne $nativeVersion) {
        throw "Version mismatch: native=$nativeVersion, managed project=$managedVersion, managed assembly=$managedAssemblyVersion, storage assembly=$storageAssemblyVersion."
    }

    $null = & $cppExecutable --once --alert-threshold 80 2>$null
    if ($LASTEXITCODE -eq 0) {
        throw 'C++ must reject --alert-threshold outside --watch.'
    }

    $null = & $csharpExecutable --once --alert-threshold 80 2>$null
    if ($LASTEXITCODE -eq 0) {
        throw 'C# must reject --alert-threshold outside --watch.'
    }

    $cppHysteresisError = (& $cppExecutable --once --alert-hysteresis 0 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0 -or
        $cppHysteresisError -notmatch '--alert-hysteresis requires --alert-threshold') {
        throw 'C++ must reject --alert-hysteresis even when its explicit value is zero.'
    }

    $csharpHysteresisError = (& $csharpExecutable --once --alert-hysteresis 0 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0 -or
        $csharpHysteresisError -notmatch '--alert-hysteresis exige --alert-threshold') {
        throw 'C# must reject --alert-hysteresis even when its explicit value is zero.'
    }

    $historyWithoutDatabase = (& $csharpExecutable --history 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0 -or $historyWithoutDatabase -notmatch 'exigem --database PATH') {
        throw 'C# history must require an explicit database path.'
    }

    $missingDatabase = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        ("rtxmon-ci-missing-{0}.db" -f [Guid]::NewGuid().ToString('N'))
    $missingHistory = (& $csharpExecutable --history --database $missingDatabase --json 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0 -or
        $missingHistory -notmatch 'banco de telemetria não existe' -or
        (Test-Path -LiteralPath $missingDatabase)) {
        throw 'C# history must fail without creating a missing database.'
    }

    # The two checks above intentionally invoke a command that exits
    # non-zero. Run these successful checks last so a passing script always
    # ends on a zero-exit-code native command.
    Invoke-Checked -Description 'C++ help without GPU' -Command {
        & $cppExecutable --help | Out-Null
    }

    Invoke-Checked -Description 'C# help without GPU' -Command {
        & $csharpExecutable --help | Out-Null
    }

    Write-Host 'Hardware-independent verification passed.'
    Write-Host 'C/C++: build with warnings as errors and 3 CTest tests.'
    Write-Host 'C#: build, deterministic sampler, alert, SQLite storage tests, and formatting.'
    Write-Host 'Schemas: capabilities v2, telemetry events v1/v2, and evidence records v1.'
    Write-Host "Version parity: C/C++ and C# $nativeVersion."
}
finally {
    Pop-Location
}
