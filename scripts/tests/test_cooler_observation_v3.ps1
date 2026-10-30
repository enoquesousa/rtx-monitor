#Requires -Version 7.2
# Deterministic SYNTHETIC fixtures only; no GPU, debugger, capture or elevation.
# Extract only the named standalone parsing and local-file helper definitions
# through the PowerShell AST. Never dot-source or execute the capture producer:
# its top-level statements attach a debugger and are outside this test's scope.
# Each run owns a fresh temporary directory. Preserve the small fixtures for
# failure diagnosis; only the explicitly created size-only file is deleted.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$scriptPath = Join-Path $repoRoot 'scripts/capture-gpuz-nvapi-cooler-status-v1.ps1'
$schemaPath = Join-Path $repoRoot 'docs/schema/nvapi-cooler-status-v1-observation-v3.schema.json'
$fixtureDirectory = Join-Path ([IO.Path]::GetTempPath()) ('rtxmon-cooler-v3-synthetic-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureDirectory) | Out-Null
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Producer syntax failed.' }
$helperNames = @('Assert-RegularLocalFile','Convert-CdbReturnClock','Split-ReferenceCsvLine','Get-ReferenceSnapshotMetadata','New-ReferenceCheckpoint','Assert-ReferenceCheckpointGrowth','Save-SealedReference')
$functions = $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]}, $true)
foreach ($name in $helperNames) {
    $function = @($functions | Where-Object Name -CEQ $name)
    if ($function.Count -ne 1) { throw "Missing helper $name" }
    Invoke-Expression $function[0].Extent.Text
}
$maximumGpuzPrefixSizeBytes = 64MB
$maximumHwinfoPrefixSizeBytes = 64MB
$gpuzFanChannels = @('Fan 1 Speed (RPM) [RPM]', 'Fan 1 Speed (%) [%]', 'Fan 2 Speed (RPM) [RPM]', 'Fan 2 Speed (%) [%]')
$hwinfoFanChannels = @('GPU Ventilador1 [RPM]', 'GPU Ventilador1 [%]', 'GPU Ventilador2 [RPM]', 'GPU Ventilador2 [%]')
$checks = [Collections.Generic.List[string]]::new()
function Check($Condition, [string]$Name) { if (-not $Condition) { throw "FAILED: $Name" }; $checks.Add($Name) }
function Refuse([scriptblock]$Action,[string]$Name) { $rejected=$false; try { $null=&$Action } catch { $rejected=$true }; Check $rejected $Name }
$nl = [Environment]::NewLine
$clock = Convert-CdbReturnClock -Text ("Debug session time: Sat Sep  5 15:20:30.123 2026 (UTC + 0:00)"+$nl+"System Uptime: 4 days 4:53:56.461"+$nl+"Process Uptime: 0 days 0:00:08.750"+$nl)
Check ($clock.captured_at_utc -ceq '2026-09-05T15:20:30.1230000Z') 'Modern UTC CDB timestamp'
Check ($clock.timestamp_precision_ns -eq 1000000 -and $clock.system_uptime_precision_ns -eq 1000000) 'Printed millisecond precision'
Check ($clock.system_uptime_100ns -eq 3632364610000L) 'Integer uptime without interpolation'
$oldClock = Convert-CdbReturnClock -Text ("Debug session time: Mon Apr 07 19:10:50 2003"+$nl+"System Uptime: 4 days 4:53:56.461"+$nl)
Check ($oldClock.timestamp_precision_ns -eq 1000000000L) 'Documented legacy second precision'
$precise = Convert-CdbReturnClock -Text ("Debug session time: Sat Sep 5 15:20:30.1234567 2026 (UTC +0:00)"+$nl+"System Uptime: 0 days 0:00:00.1234567"+$nl)
Check ($precise.timestamp_precision_ns -eq 100 -and $precise.system_uptime_100ns -eq 1234567) 'Seven printed fraction digits'
Refuse { Convert-CdbReturnClock -Text ("Debug session time: Sat Sep 5 15:20:30 2026 (UTC - 3:00)"+$nl+"System Uptime: 0 days 0:00:01"+$nl) } 'Reject non-UTC return'
Refuse { Convert-CdbReturnClock -Text ("Debug session time: Sat Sep 5 15:20:30 2026"+$nl) } 'Reject missing uptime'
Refuse { Convert-CdbReturnClock -Text ("Debug session time: Sat Sep 5 15:20:30 2026"+$nl+"Debug session time: Sat Sep 5 15:20:30 2026"+$nl+"System Uptime: 0 days 0:00:01"+$nl) } 'Reject duplicate per-return clocks'
Refuse { Convert-CdbReturnClock -Text ("Debug session time: Sat Sep 5 15:20:30 2026"+$nl+"System Uptime: 0 days 24:00:01"+$nl) } 'Reject invalid uptime'
$csv = Split-ReferenceCsvLine -Line 'Date,"value, text","embedded ""quote""",'
Check ($csv.Count -eq 3 -and $csv[1] -ceq 'value, text' -and $csv[2] -ceq 'embedded "quote"') 'Quoted CSV and trailing delimiter'
Refuse { Split-ReferenceCsvLine -Line 'Date,"unfinished' } 'Reject incomplete quoted row'
Refuse { Split-ReferenceCsvLine -Line 'Date,"closed"extra' } 'Reject quote garbage'
$gpuzPath = Join-Path $fixtureDirectory 'SYNTHETIC-gpuz.csv'
$hwinfoPath = Join-Path $fixtureDirectory 'SYNTHETIC-hwinfo.csv'
$gpuzHeader = 'Date,' + ($gpuzFanChannels -join ',') + $nl
$hwinfoHeader = 'Date,Time,' + ($hwinfoFanChannels -join ',') + ',Temperature [°C]' + $nl
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
$cp1252 = [Text.Encoding]::GetEncoding(1252)
$gpuzCheckpoints=@(); $hwinfoCheckpoints=@()
foreach ($i in 0..2) {
    $rowsGpu = (0..$i | ForEach-Object { '2026-09-05 12:20:{0:D2},1100,30,1110,30' -f (10+$_) }) -join $nl
    $rowsHw = (0..$i | ForEach-Object { '5.9.2026,12:20:{0:D2}.500,1090,30,1100,30,50' -f (10+$_) }) -join $nl
    [IO.File]::WriteAllText($gpuzPath,$gpuzHeader+$rowsGpu+$nl+'incomplete trailing append',[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes($hwinfoPath,$cp1252.GetBytes($hwinfoHeader+$rowsHw+$nl+'incomplete trailing append'))
    [IO.File]::SetLastWriteTimeUtc($gpuzPath,[DateTime]::UtcNow.AddSeconds($i))
    [IO.File]::SetLastWriteTimeUtc($hwinfoPath,[DateTime]::UtcNow.AddSeconds($i))
    $phase=@('before','midpoint','after')[$i]
    $gpuzCheckpoints += New-ReferenceCheckpoint -Path $gpuzPath -Kind gpuz -Phase $phase
    $hwinfoCheckpoints += New-ReferenceCheckpoint -Path $hwinfoPath -Kind hwinfo -Phase $phase
}
# Regression from the real log's formatting; these values remain synthetic.
# HWiNFO writes one-digit minute/second components, including second zero.
$singleDigitFraction = Get-ReferenceSnapshotMetadata -Bytes ($cp1252.GetBytes(
    $hwinfoHeader + '5.9.2026,21:4:0.137,1090,30,1100,30,50' + $nl)) -Kind hwinfo
$singleDigitWholeSecond = Get-ReferenceSnapshotMetadata -Bytes ($cp1252.GetBytes(
    $hwinfoHeader + '5.9.2026,21:4:0,1090,30,1100,30,50' + $nl)) -Kind hwinfo
$invalidSecondRejected = $false
try {
    $null = Get-ReferenceSnapshotMetadata -Bytes ($cp1252.GetBytes(
        $hwinfoHeader + '5.9.2026,21:4:60.137,1090,30,1100,30,50' + $nl)) -Kind hwinfo
} catch {
    $invalidSecondRejected = $_.Exception.Message -ceq 'The final reference sample timestamp is invalid.'
}
Check ($hwinfoCheckpoints[2].metadata.text_encoding -ceq 'windows-1252' -and
    $singleDigitFraction.last_sample_local -ceq '2026-09-05 21:04:00.137' -and
    $singleDigitWholeSecond.last_sample_local -ceq '2026-09-05 21:04:00.000' -and
    $invalidSecondRejected) 'HWiNFO Windows-1252 and one-digit times with/without fractions; reject second 60'
Check ($gpuzCheckpoints[0].bytes[-1] -eq 10 -and $gpuzCheckpoints[0].bytes.Length -lt (Get-Item $gpuzPath).Length) 'Seal complete LF prefix only'
$gpuzReference=Save-SealedReference -Path $gpuzPath -Kind gpuz -Checkpoints $gpuzCheckpoints -Directory $fixtureDirectory
$hwinfoReference=Save-SealedReference -Path $hwinfoPath -Kind hwinfo -Checkpoints $hwinfoCheckpoints -Directory $fixtureDirectory
Check ($gpuzReference.checkpoints.Count -eq 3 -and $gpuzReference.selected_channels.Count -eq 4) 'Three contemporaneous GPU-Z seals'
Check ($hwinfoReference.prefix_sha256 -ceq (Get-FileHash (Join-Path $fixtureDirectory $hwinfoReference.sealed_relative_path)).Hash.ToLowerInvariant()) 'Persisted HWiNFO prefix hash'
$gpuzCheckpoints[1].bytes[0] = 88
Refuse { Assert-ReferenceCheckpointGrowth -Before $gpuzCheckpoints[0] -After $gpuzCheckpoints[1] } 'Reject overwritten previous prefix'
$gpuzCheckpoints[1].bytes[0] = 68
$oldIndex=$gpuzCheckpoints[1].checkpoint.session_index
$gpuzCheckpoints[1].checkpoint.session_index=1
Refuse { Assert-ReferenceCheckpointGrowth -Before $gpuzCheckpoints[0] -After $gpuzCheckpoints[1] } 'Reject changed session during capture'
$gpuzCheckpoints[1].checkpoint.session_index=$oldIndex
Refuse { Get-ReferenceSnapshotMetadata -Bytes ([Text.Encoding]::UTF8.GetBytes($gpuzHeader+'2026-09-05 12:20:10,1,2,3,4')) -Kind gpuz } 'Reject non-LF complete snapshot'
Refuse { Get-ReferenceSnapshotMetadata -Bytes ([Text.Encoding]::UTF8.GetBytes($gpuzHeader+'2026-09-05 12:20:10,1,2'+$nl)) -Kind gpuz } 'Reject final header/row mismatch'
$doubleHeader = $gpuzHeader+'2026-09-05 12:20:01,1,2,3,4'+$nl+$gpuzHeader+'2026-09-05 12:20:02,1,2,3,4'+$nl
$sessionMetadata=Get-ReferenceSnapshotMetadata -Bytes ([Text.Encoding]::UTF8.GetBytes($doubleHeader)) -Kind gpuz
Check ($sessionMetadata.session_index -eq 1) 'Exact appended session retained'
$fixture=Get-Content (Join-Path $repoRoot 'csharp/RtxMonitor.Lab.Tests/Fixtures/nvapi-cooler-status-v1-observation-v2.json') -Raw | ConvertFrom-Json -AsHashtable
$fixture.schema_version=3
$fixture.warning='SYNTHETIC FIXTURE ONLY: artificial clocks, zero raw fields, and fabricated reference values; no physical capture or correlation evidence.'
$fixture.capture_session_id=[Guid]::NewGuid().ToString('D')
$fixture.supervision_started_utc='2026-09-05T15:20:00.0000000Z'
$fixture.capture_started_utc='2026-09-05T15:20:00.5000000Z'
$fixture.captured_utc='2026-09-05T15:20:30.5000000Z'
$fixture.supervision_completed_utc='2026-09-05T15:20:30.5000000Z'
$fixture.duration_seconds=30
$fixture.samples=@($fixture.samples[0],$fixture.samples[1])
$fixture.call_count=2
foreach ($site in $fixture.call_sites) { $site.call_count=1 }
foreach ($sample in $fixture.samples) {
    $sample.captured_at_utc=$clock.captured_at_utc
    $sample.timestamp_precision_ns=$clock.timestamp_precision_ns
    $sample.system_uptime_100ns=$clock.system_uptime_100ns
    $sample.system_uptime_precision_ns=$clock.system_uptime_precision_ns
    $sample.raw_words=@('0x00000000')*426
    $sample.raw_words[0]='0x000106a8'
    $sample.raw_words[1]='0x00000002'
    $sample.raw_words[10]='0x00000001'
    $sample.raw_words[23]='0x00000002'
    foreach ($entry in $sample.raw_entries) { $entry.raw_field_words=@('0x00000000')*4 }
}
$fixture.clock=@{
 source='cdb_time_command';command='.time -h 0';timestamp_event='debugger_stopped_at_post_call'
 timezone_id='E. South America Standard Time';local_utc_offset_minutes=-180
 resolution_kind='display_precision_only';timestamp_interpolation=$false
 capture_window_source='supervisor_utc_with_attach_race';accuracy_bound_available=$false
}
$fixture.references=@{gpuz=$gpuzReference;hwinfo=$hwinfoReference}
$fixture.reference_log.last_sample_local_before=$gpuzReference.checkpoints[0].last_sample_local
$fixture.reference_log.last_sample_local_midpoint=$gpuzReference.checkpoints[1].last_sample_local
$fixture.reference_log.last_sample_local_after=$gpuzReference.checkpoints[2].last_sample_local
$fixtureJson=$fixture | ConvertTo-Json -Depth 20
$fixturePath=Join-Path $fixtureDirectory 'SYNTHETIC-observation-v3.json'
[IO.File]::WriteAllText($fixturePath,$fixtureJson,[Text.UTF8Encoding]::new($false))
Check ($fixtureJson | Test-Json -SchemaFile $schemaPath) 'Full synthetic v3 schema passes'
function SchemaRefuse([scriptblock]$Mutation,[string]$Name) {
 $copy=$fixtureJson | ConvertFrom-Json -AsHashtable
 &$Mutation $copy
 $valid=($copy | ConvertTo-Json -Depth 20 | Test-Json -SchemaFile $schemaPath -ErrorAction SilentlyContinue)
 Check (-not $valid) $Name
}
SchemaRefuse { param($x) $x.samples[0].Remove('captured_at_utc') | Out-Null } 'Schema rejects missing per-call UTC'
SchemaRefuse { param($x) $x.references.Remove('hwinfo') | Out-Null } 'Schema rejects missing sealed HWiNFO'
SchemaRefuse { param($x) $x.clock.timestamp_interpolation=$true } 'Schema rejects interpolation claim'
SchemaRefuse { param($x) $x.samples[0].timestamp_precision_ns=1 } 'Schema rejects invented nanosecond precision'
SchemaRefuse { param($x) $x.references.gpuz.checkpoints[1].phase='after' } 'Schema enforces checkpoint order'
SchemaRefuse { param($x) $x.references.hwinfo.prefix_sha256='bad' } 'Schema rejects invalid prefix hash'
SchemaRefuse { param($x) $x.samples[0].rpm=1100 } 'Schema rejects raw field promotion'
SchemaRefuse { param($x) $x.clock.accuracy_bound_available=$true } 'Schema rejects unproved clock accuracy'
SchemaRefuse { param($x) $x.references.gpuz.prefix_size_bytes=67108865 } 'Schema enforces GPU-Z 64 MiB bound'
$sizeCopy = $fixtureJson | ConvertFrom-Json -AsHashtable
$sizeCopy.references.gpuz.prefix_size_bytes = 67108864
foreach ($checkpoint in $sizeCopy.references.gpuz.checkpoints) { $checkpoint.size_bytes=67108864 }
Check ($sizeCopy | ConvertTo-Json -Depth 20 | Test-Json -SchemaFile $schemaPath) 'Schema accepts GPU-Z prefix and checkpoints at 64 MiB'
SchemaRefuse { param($x) $x.references.gpuz.checkpoints[1].size_bytes=67108865 } 'Schema rejects GPU-Z checkpoint above 64 MiB'
$limitAssignment = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and $node.Left.VariablePath.UserPath -ceq 'maximumGpuzPrefixSizeBytes'}, $true))
Check ($limitAssignment.Count -eq 1) 'Producer has one GPU-Z reference bound'
Invoke-Expression $limitAssignment[0].Extent.Text
Check ($maximumGpuzPrefixSizeBytes -eq 64MB) 'Producer GPU-Z reference bound is 64 MiB'
$boundedPath=Join-Path $fixtureDirectory 'SYNTHETIC-length-only.bin'
$lengthStream=[IO.File]::Open($boundedPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
try { $lengthStream.SetLength(64MB) } finally { $lengthStream.Dispose() }
Assert-RegularLocalFile -Path $boundedPath -Description 'synthetic length-only bound check' -MaximumSizeBytes $maximumGpuzPrefixSizeBytes
$checks.Add('Regular file bound accepts exactly 64 MiB')
$lengthStream=[IO.File]::OpenWrite($boundedPath)
try { $lengthStream.SetLength(64MB+1) } finally { $lengthStream.Dispose() }
$sizeRejected=$false
try { $null=New-ReferenceCheckpoint -Path $boundedPath -Kind gpuz -Phase before } catch { $sizeRejected=$_.Exception.Message.Contains('67108864') }
Check $sizeRejected 'Checkpoint rejects above 64 MiB before parsing bytes'
$resolvedFixtureDirectory = [IO.Path]::GetFullPath($fixtureDirectory).TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
$resolvedBoundedPath = [IO.Path]::GetFullPath($boundedPath)
if (-not $resolvedBoundedPath.StartsWith($resolvedFixtureDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolvedBoundedPath) -cne 'SYNTHETIC-length-only.bin') {
    throw 'The size-only fixture cleanup escaped its owned temporary directory.'
}
[IO.File]::Delete($resolvedBoundedPath)
if ($checks.Count -ne 37) { throw "Expected 37 synthetic checks, completed $($checks.Count)." }
[pscustomobject]@{synthetic_only=$true; capture_executed=$false; checks_passed=$checks.Count; checks=$checks;fixture_path=$fixturePath} | ConvertTo-Json -Depth 5
