# Synthetic transcript parsing only: no process attachment, GPU access or elevation.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../helpers/thermal-return-clocks.ps1')
$checks = [Collections.Generic.List[string]]::new()
function Check($Condition, [string]$Name) { if (-not $Condition) { throw "FAILED: $Name" }; $checks.Add($Name) }
function Refuse([scriptblock]$Action, [string]$Name) { $rejected = $false; try { $null = & $Action } catch { $rejected = $true }; Check $rejected $Name }
$words = @(for ($i = 0; $i -lt 42; $i++) { '0x{0:x8}' -f $i })
$wordText = (0..41 | ForEach-Object { 'w{0:D2}={1}' -f $_, $words[$_] }) -join ' '
$sample = [pscustomobject]@{ sequence=1; channel_index=1; thread_id='0x000000ab'; return_status='0x00000000'; raw_words=$words; selected_raw_fixed_8=11 }
$clockText = "Debug session time: Sun Sep 6 04:00:00.123 2026 (UTC + 0:00)`nSystem Uptime: 2 days 01:02:03.456`n"
$hit = 'RTXMON_NVAPI_THERM_V2 tid=0x000000ab channel=0x00000001 status=0x00000000 ' + $wordText
$transcript = "RTXMON_THERM_CLOCK_BEGIN`n$clockText$hit`nRTXMON_THERM_CLOCK_END`n"
$start = [DateTimeOffset]'2026-09-06T04:00:00Z'
$end = $start.AddSeconds(10)
function Parse([string]$Text, [object[]]$Samples = @($sample)) { @(Read-ThermalReturnClocks -Text $Text -Samples $Samples -StartedUtc $start -CompletedUtc $end) }
$parsed = Parse $transcript
Check ($parsed.Count -eq 1 -and $parsed[0].captured_at_utc -ceq '2026-09-06T04:00:00.1230000Z') 'Exact per-return UTC, without interpolation'
Check ($parsed[0].timestamp_precision_ns -eq 1000000 -and $parsed[0].system_uptime_100ns -eq 1765234560000L) 'Printed clock precision and integer uptime'
Check ($parsed[0].selected_raw_fixed_8 -eq 11 -and $parsed[0].sequence -eq 1) 'Clock stays associated with its validated raw sample'
$precise = Convert-ThermalReturnClock $clockText.Replace('.123 ', '.1234567 ')
Check ($precise.timestamp_precision_ns -eq 100) 'Preserve seven-digit printed precision'
Refuse { Parse $transcript.Replace('UTC + 0:00','UTC - 3:00') } 'Reject non-UTC clocks'
Refuse { Parse $transcript.Replace(' (UTC + 0:00)','') } 'Reject missing explicit timezone'
Refuse { Parse $transcript.Replace($clockText, $clockText + $clockText) } 'Reject duplicate clocks'
Refuse { Parse $transcript.Replace('System Uptime:', 'Missing Uptime:') } 'Reject missing uptime'
Refuse { Parse $transcript.Replace('01:02:03.456','24:02:03.456') } 'Reject invalid uptime'
Refuse { Parse $transcript.Replace('04:00:00.123','04:01:00.123') } 'Reject clock outside supervision'
Refuse { Parse $transcript.Replace('RTXMON_THERM_CLOCK_END', 'MISSING_END') } 'Reject incomplete clock blocks'
Refuse { Parse ($transcript + $transcript) } 'Reject extra clock block'
Refuse { Parse ($transcript + $hit + "`n") } 'Reject unclocked returns outside blocks'
Refuse { Parse $transcript.Replace('channel=0x00000001','channel=0x00000000') } 'Reject reordered channel association'
Refuse { Parse $transcript.Replace('w10=0x0000000a','w10=0x000000ff') } 'Reject raw buffer mismatch'
Refuse { Parse $transcript.Replace('w41=0x00000029','') } 'Reject truncated buffers'
$sample2 = [pscustomobject]@{ sequence=2; channel_index=1; thread_id=$sample.thread_id; return_status=$sample.return_status; raw_words=$words; selected_raw_fixed_8=11 }
$second = $transcript.Replace('04:00:00.123','04:00:00.124').Replace('01:02:03.456','01:02:03.457')
$two = Parse ($transcript + $second) @($sample, $sample2)
Check ($two.Count -eq 2 -and $two[1].captured_at_utc.EndsWith('1240000Z')) 'Preserve ordered real clocks'
Refuse { Parse ($transcript + $second.Replace('01:02:03.457','01:02:03.455')) @($sample,$sample2) } 'Reject uptime regression'
Refuse { Parse ($transcript + $second.Replace('04:00:00.124','04:00:00.122')) @($sample,$sample2) } 'Reject UTC regression'
$tokens = $null; $errors = $null
$null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../capture-gpuz-nvapi-therm-channel-v2.ps1'), [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'Producer parses without executing top-level capture'
$schema = Join-Path $PSScriptRoot '../../docs/schema/thermal-return-clocks-v1.schema.json'
$fixture = @{ schema_version=1;source_kind='thermal_return_clock_sidecar';capture_session_id='00000000-0000-4000-8000-000000000001';clock_command='.time -h 0';timestamp_event='debugger_stopped_at_post_call';timestamp_interpolation=$false;call_count=1;samples=@($parsed);warning='Synthetic fixture only';observation_sha256=('a'*64);debugger_transcript_sha256=('b'*64) }
Check (($fixture | ConvertTo-Json -Depth 10) | Test-Json -SchemaFile $schema) 'Synthetic sidecar passes its own schema'
$fixture.timestamp_interpolation = $true
Check (-not (($fixture | ConvertTo-Json -Depth 10) | Test-Json -SchemaFile $schema -ErrorAction SilentlyContinue)) 'Schema rejects interpolated timestamp claim'
$fixture.timestamp_interpolation = $false
$fixture.samples[0].timestamp_precision_ns = 1
Check (-not (($fixture | ConvertTo-Json -Depth 10) | Test-Json -SchemaFile $schema -ErrorAction SilentlyContinue)) 'Schema rejects invented precision'
$v2Schema = Join-Path $PSScriptRoot '../../docs/schema/nvapi-therm-channel-v2-observation-v2.schema.json'
$v3Schema = Join-Path $PSScriptRoot '../../docs/schema/nvapi-therm-channel-v2-observation-v3.schema.json'
$v3 = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../csharp/RtxMonitor.Lab.Tests/Fixtures/nvapi-therm-channel-v2-observation-v2.json') -Raw | ConvertFrom-Json -AsHashtable
Check (($v3 | ConvertTo-Json -Depth 20) | Test-Json -SchemaFile $v2Schema) 'Historical v2 fixture remains valid unchanged'
$v3.schema_version=3
$v3.clock=@{command='.time -h 0';timestamp_event='debugger_stopped_at_post_call';timestamp_interpolation=$false}
$v3.debugger_transcript_sha256='c'*64
foreach ($observation in $v3.samples) {
    $observation.captured_at_utc='2026-08-26T21:18:31.1230000Z'
    $observation.timestamp_precision_ns=1000000
    $observation.system_uptime_100ns=1230000
    $observation.system_uptime_precision_ns=1000000
}
foreach ($field in @('prefix_size_bytes','size_bytes_before','size_bytes_midpoint','size_bytes_after')) { $v3.references.gpuz[$field]=40MB }
Check (($v3 | ConvertTo-Json -Depth 20) | Test-Json -SchemaFile $v3Schema) 'V3 accepts clocked synthetic observations with bounded 40MiB reference'
Check (-not (($v3 | ConvertTo-Json -Depth 20) | Test-Json -SchemaFile $v2Schema -ErrorAction SilentlyContinue)) 'V3 cannot masquerade as the historical v2 format'
$v3.references.gpuz.prefix_size_bytes=64MB+1
Check (-not (($v3 | ConvertTo-Json -Depth 20) | Test-Json -SchemaFile $v3Schema -ErrorAction SilentlyContinue)) 'V3 rejects references exceeding its fixed 64MiB bound'
$v3.references.gpuz.prefix_size_bytes=40MB
$v3.samples[0].Remove('captured_at_utc')
Check (-not (($v3 | ConvertTo-Json -Depth 20) | Test-Json -SchemaFile $v3Schema -ErrorAction SilentlyContinue)) 'V3 requires a clock for every return'
$v3.samples[0].captured_at_utc='2026-08-26T21:18:31.1230000Z'
$v3.clock.timestamp_interpolation=$true
Check (-not (($v3 | ConvertTo-Json -Depth 20) | Test-Json -SchemaFile $v3Schema -ErrorAction SilentlyContinue)) 'V3 rejects interpolated clock metadata'
@{ synthetic_only=$true; capture_executed=$false; checks_passed=$checks.Count; checks=$checks } | ConvertTo-Json -Depth 4
