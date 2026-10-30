# Offline parsing only. Dot-sourcing this file never attaches a debugger.
function Convert-ThermalReturnClock {
    param([Parameter(Mandatory)][string]$Text)
    $times = [regex]::Matches($Text, '(?im)^Debug session time:\s+[A-Za-z]{3}\s+(?<value>[A-Za-z]{3}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}(?:\.(?<fraction>\d{1,7}))?\s+\d{4})\s+\(UTC\s+[+-]\s*0:00\)\s*$')
    $uptimes = [regex]::Matches($Text, '(?im)^System Uptime:\s+(?<days>\d+)\s+days\s+(?<hours>\d{1,2}):(?<minutes>\d{2}):(?<seconds>\d{2})(?:\.(?<fraction>\d{1,7}))?\s*$')
    if ($times.Count -ne 1 -or $uptimes.Count -ne 1 -or
        [regex]::Matches($Text, '(?im)^Debug session time:').Count -ne 1 -or
        [regex]::Matches($Text, '(?im)^System Uptime:').Count -ne 1) {
        throw 'Each thermal return requires exactly one explicit UTC timestamp and one system uptime.'
    }
    $time = $times[0]
    $timestamp = [DateTime]::MinValue
    $formats = [string[]]@('MMM d HH:mm:ss yyyy', 'MMM dd HH:mm:ss yyyy', 'MMM d HH:mm:ss.FFFFFFF yyyy', 'MMM dd HH:mm:ss.FFFFFFF yyyy')
    if (-not [DateTime]::TryParseExact([regex]::Replace($time.Groups['value'].Value, '\s+', ' '), $formats,
        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$timestamp)) {
        throw 'Invalid thermal return UTC timestamp.'
    }
    $uptime = $uptimes[0]
    $days = [long]$uptime.Groups['days'].Value
    $hours = [int]$uptime.Groups['hours'].Value
    $minutes = [int]$uptime.Groups['minutes'].Value
    $seconds = [int]$uptime.Groups['seconds'].Value
    if ($days -gt 99999 -or $hours -gt 23 -or $minutes -gt 59 -or $seconds -gt 59) { throw 'Invalid thermal return uptime.' }
    $fraction = $uptime.Groups['fraction'].Value
    [long]$ticks = if ($fraction.Length -eq 0) { 0 } else { [long]$fraction.PadRight(7, '0') }
    return [pscustomobject]@{
        captured_at_utc = [DateTime]::SpecifyKind($timestamp, [DateTimeKind]::Utc).ToString('O')
        timestamp_precision_ns = [long][Math]::Pow(10, 9 - $time.Groups['fraction'].Value.Length)
        system_uptime_100ns = (($days * 86400L + $hours * 3600L + $minutes * 60L + $seconds) * 10000000L) + $ticks
        system_uptime_precision_ns = [long][Math]::Pow(10, 9 - $fraction.Length)
    }
}

function Read-ThermalReturnClocks {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][object[]]$Samples,
        [Parameter(Mandatory)][DateTimeOffset]$StartedUtc,
        [Parameter(Mandatory)][DateTimeOffset]$CompletedUtc
    )
    if ($Text.Length -gt 128MB -or $Samples.Count -lt 1 -or $Samples.Count -gt 100000 -or $CompletedUtc -lt $StartedUtc) {
        throw 'Invalid thermal clock capture bounds.'
    }
    $blocks = [regex]::Matches($Text, '(?ms)^RTXMON_THERM_CLOCK_BEGIN\r?\n(?<body>.*?)^RTXMON_THERM_CLOCK_END\r?$')
    if ($blocks.Count -ne $Samples.Count -or
        [regex]::Matches($Text, '(?m)^RTXMON_THERM_CLOCK_BEGIN\r?$').Count -ne $blocks.Count -or
        [regex]::Matches($Text, '(?m)^RTXMON_THERM_CLOCK_END\r?$').Count -ne $blocks.Count -or
        [regex]::Matches($Text, '(?m)^RTXMON_NVAPI_THERM_V2 tid=').Count -ne $blocks.Count) {
        throw 'Thermal return clocks must cover every sample exactly once.'
    }
    $result = [Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $blocks.Count; $i++) {
        $body = $blocks[$i].Groups['body'].Value
        $headers = [regex]::Matches($body, '(?im)^RTXMON_NVAPI_THERM_V2 tid=0x(?<tid>[0-9a-f]{8}) channel=0x(?<channel>[0-9a-f]{8}) status=0x(?<status>[0-9a-f]{8})(?<words>(?: w[0-9]{2}=0x[0-9a-f]{8}){42})\r?$')
        if ($headers.Count -ne 1) { throw 'Thermal clock block must contain one complete bounded return.' }
        $sample = $Samples[$i]
        $header = $headers[0]
        $words = [regex]::Matches($header.Groups['words'].Value, 'w(?<index>[0-9]{2})=0x(?<value>[0-9a-f]{8})', 'IgnoreCase')
        if ($sample.sequence -ne ($i + 1) -or $sample.channel_index -ne [Convert]::ToInt32($header.Groups['channel'].Value, 16) -or
            $sample.thread_id -ine ('0x' + $header.Groups['tid'].Value) -or $sample.return_status -ine ('0x' + $header.Groups['status'].Value) -or
            $sample.raw_words.Count -ne 42) { throw 'Thermal clock sample identity differs from the validated observation.' }
        for ($word = 0; $word -lt 42; $word++) {
            if ([int]$words[$word].Groups['index'].Value -ne $word -or $sample.raw_words[$word] -ine ('0x' + $words[$word].Groups['value'].Value)) {
                throw 'Thermal clock raw buffer differs from the validated observation.'
            }
        }
        $clock = Convert-ThermalReturnClock -Text $body
        $utc = [DateTimeOffset]::Parse($clock.captured_at_utc, [Globalization.CultureInfo]::InvariantCulture)
        $precision = [TimeSpan]::FromTicks([long]($clock.timestamp_precision_ns / 100))
        if ($utc -lt $StartedUtc.Subtract($precision) -or $utc -gt $CompletedUtc.Add($precision)) { throw 'Thermal return clock lies outside supervision.' }
        if ($i -gt 0 -and ($clock.system_uptime_100ns -lt $result[$i - 1].system_uptime_100ns -or
            $utc -lt [DateTimeOffset]::Parse($result[$i - 1].captured_at_utc))) { throw 'Thermal return clocks regressed.' }
        $result.Add([pscustomobject]@{
            sequence = $sample.sequence
            channel_index = $sample.channel_index
            selected_raw_fixed_8 = $sample.selected_raw_fixed_8
            captured_at_utc = $clock.captured_at_utc
            timestamp_precision_ns = $clock.timestamp_precision_ns
            system_uptime_100ns = $clock.system_uptime_100ns
            system_uptime_precision_ns = $clock.system_uptime_precision_ns
        })
    }
    return $result.ToArray()
}
