[CmdletBinding()]
param(
    [string]$BaseUri = 'http://127.0.0.1:5144',
    [ValidateRange(1, 1440)]
    [int]$DurationMinutes = 30,
    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 2,
    [string]$ExpectedLuid = '0x000000000001669b',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$startedAt = [DateTimeOffset]::UtcNow
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = $startedAt.ToString('yyyyMMdd-HHmmss')
    $OutputDirectory = Join-Path $PSScriptRoot "..\evidence\windows-telemetry-long-run-$stamp"
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$samplesPath = Join-Path $resolvedOutput 'samples.jsonl'
$summaryPath = Join-Path $resolvedOutput 'summary.json'

$gpuList = Invoke-RestMethod -Uri "$BaseUri/api/v1/gpus" -TimeoutSec 5
if ($gpuList.count -lt 1) {
    throw 'O serviço não publicou nenhuma GPU.'
}
$gpu = @($gpuList.gpus | Where-Object { $_.present })[0]
if ($null -eq $gpu) {
    throw 'O serviço não publicou uma GPU presente.'
}
$gpuUuid = [string]$gpu.uuid
$encodedUuid = [Uri]::EscapeDataString($gpuUuid)
$endpoint = "$BaseUri/api/v1/gpus/$encodedUuid/windows-telemetry"
$deadline = $startedAt.AddMinutes($DurationMinutes)
$writer = [IO.StreamWriter]::new($samplesPath, $false, [Text.UTF8Encoding]::new($false))

$sampleCount = 0
$httpFailures = 0
$contractFailures = 0
$identityFailures = 0
$timestampRegressions = 0
$previousTimestamp = $null
$maximumGapMs = 0L
$stateCounts = @{}
$luidCounts = @{}
$memory = @{
    local = [Collections.Generic.List[double]]::new()
    non_local = [Collections.Generic.List[double]]::new()
}
$engineStates = @{}
$engineMaximums = @{}
$errors = [Collections.Generic.List[object]]::new()

try {
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $attemptedAt = [DateTimeOffset]::UtcNow
        try {
            $sample = Invoke-RestMethod -Uri $endpoint -TimeoutSec 5
            $sampleCount++
            $writer.WriteLine((@{
                sampled_at_unix_ms = $attemptedAt.ToUnixTimeMilliseconds()
                response = $sample
            } | ConvertTo-Json -Depth 12 -Compress))

            if ($sample.schema_version -ne 1 -or @($sample.engines).Count -ne 6) {
                $contractFailures++
            }
            $state = [string]$sample.state
            $stateCounts[$state] = 1 + [int]$stateCounts[$state]
            $luid = [string]$sample.adapter.luid
            $luidCounts[$luid] = 1 + [int]$luidCounts[$luid]
            if ($luid -ne $ExpectedLuid -or $sample.gpu.uuid -ne $gpuUuid) {
                $identityFailures++
            }

            $timestamp = [long]$sample.captured_at_unix_ms
            if ($null -ne $previousTimestamp) {
                $gap = $timestamp - [long]$previousTimestamp
                if ($gap -lt 0) { $timestampRegressions++ }
                if ($gap -gt $maximumGapMs) { $maximumGapMs = $gap }
            }
            $previousTimestamp = $timestamp

            if ($sample.local_memory.state -eq 'available') {
                $memory.local.Add([double]$sample.local_memory.value)
            }
            if ($sample.non_local_memory.state -eq 'available') {
                $memory.non_local.Add([double]$sample.non_local_memory.value)
            }
            foreach ($engine in @($sample.engines)) {
                $type = [string]$engine.engine_type
                $engineState = [string]$engine.utilization.state
                if (-not $engineStates.ContainsKey($type)) { $engineStates[$type] = @{} }
                $engineStates[$type][$engineState] = 1 + [int]$engineStates[$type][$engineState]
                if ($null -ne $engine.utilization.value) {
                    $value = [double]$engine.utilization.value
                    if (-not $engineMaximums.ContainsKey($type) -or $value -gt $engineMaximums[$type]) {
                        $engineMaximums[$type] = $value
                    }
                }
            }
        }
        catch {
            $httpFailures++
            $errors.Add(@{
                attempted_at_unix_ms = $attemptedAt.ToUnixTimeMilliseconds()
                message = $_.Exception.Message
            })
        }
        $writer.Flush()
        Start-Sleep -Seconds $IntervalSeconds
    }
}
finally {
    $writer.Dispose()
}

$finishedAt = [DateTimeOffset]::UtcNow
function Measure-Values([Collections.Generic.List[double]]$Values) {
    if ($Values.Count -eq 0) { return $null }
    $measurement = $Values | Measure-Object -Minimum -Maximum -Average
    return @{
        samples = $Values.Count
        minimum = $measurement.Minimum
        maximum = $measurement.Maximum
        average = $measurement.Average
    }
}

$minimumExpectedSamples = [Math]::Floor(($DurationMinutes * 60 / $IntervalSeconds) * 0.90)
$passed = $sampleCount -ge $minimumExpectedSamples -and
    $httpFailures -eq 0 -and
    $contractFailures -eq 0 -and
    $identityFailures -eq 0 -and
    $timestampRegressions -eq 0 -and
    $memory.local.Count -eq $sampleCount -and
    $memory.non_local.Count -eq $sampleCount
$summary = [ordered]@{
    schema_version = 1
    started_at_unix_ms = $startedAt.ToUnixTimeMilliseconds()
    finished_at_unix_ms = $finishedAt.ToUnixTimeMilliseconds()
    elapsed_ms = ($finishedAt - $startedAt).TotalMilliseconds
    requested_duration_minutes = $DurationMinutes
    interval_seconds = $IntervalSeconds
    endpoint = $endpoint
    gpu_uuid = $gpuUuid
    expected_luid = $ExpectedLuid
    passed = $passed
    minimum_expected_samples = $minimumExpectedSamples
    sample_count = $sampleCount
    http_failures = $httpFailures
    contract_failures = $contractFailures
    identity_failures = $identityFailures
    timestamp_regressions = $timestampRegressions
    maximum_capture_gap_ms = $maximumGapMs
    state_counts = $stateCounts
    luid_counts = $luidCounts
    local_memory_bytes = Measure-Values $memory.local
    non_local_memory_bytes = Measure-Values $memory.non_local
    engine_state_counts = $engineStates
    engine_maximum_percent = $engineMaximums
    errors = $errors
    samples_file = 'samples.jsonl'
}
[IO.File]::WriteAllText(
    $summaryPath,
    ($summary | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 12
if (-not $passed) { exit 1 }
