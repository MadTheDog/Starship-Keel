[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LogPath,
    [Parameter(Mandatory)][string]$Session,
    [int]$SourceId,
    [switch]$RequireLoaded,
    [switch]$RequireDespawnReset
)

$ErrorActionPreference = 'Stop'
$records = @(
    foreach ($line in Get-Content -LiteralPath $LogPath) {
        $match = [regex]::Match($line, 'KEEL_LOGIC_OUTPUT\|([^|]+)\|([0-9]+)\|([a-z-]+)\|(.*)$')
        if ($match.Success -and $match.Groups[1].Value -ceq $Session) {
            [pscustomobject]@{
                SourceId = [int]$match.Groups[2].Value
                Event = $match.Groups[3].Value
                Detail = $match.Groups[4].Value.Trim()
            }
        }
    }
)
if (!$PSBoundParameters.ContainsKey('SourceId')) {
    $ids = @($records | Select-Object -ExpandProperty SourceId -Unique)
    if ($ids.Count -ne 1) { throw "Expected exactly one probe source for session '$Session'." }
    $SourceId = $ids[0]
}
$trace = @($records | Where-Object SourceId -eq $SourceId)
$failure = $trace | Where-Object Event -eq 'fail' | Select-Object -First 1
if ($failure) { throw "Native logic fixture failed. $($failure.Detail)" }

$expected = [ordered]@{
    arm = $null
    baseline = $null
    high = 'src=1,1,7.25;job=1,1,1;gate=0,1,1'
    'pulse-low' = 'src=1,1,7.25;job=1,1,1;gate=0,0,1'
    low = 'src=0,1,2.5;job=0,1,0;gate=1,0,0'
    'held-high' = 'src=1,1,7.25;job=1,1,1;gate=0,0,1'
    pass = $null
}
$reset = @($trace | Where-Object Event -eq 'despawn')
if ($RequireDespawnReset -or $reset.Count -gt 0) { $expected.Add('despawn', 'signal=0,pulse=0,value=0') }
$last = -1
foreach ($name in $expected.Keys) {
    $matches = @(for ($i = 0; $i -lt $trace.Count; $i++) { if ($trace[$i].Event -ceq $name) { $i } })
    if ($matches.Count -ne 1 -or $matches[0] -le $last) { throw "Missing, repeated, or out-of-order evidence '$name'." }
    $last = $matches[0]
    if ($null -ne $expected[$name] -and $trace[$last].Detail -cne $expected[$name]) {
        throw "Unexpected native state for '$name'. Actual $($trace[$last].Detail)"
    }
}
$arm = $trace | Where-Object Event -eq 'arm'
if ($arm.Detail -cnotin @('new', 'loaded')) { throw 'Expected a new or restored probe placement.' }
if ($RequireLoaded -and $arm.Detail -cne 'loaded') { throw 'Expected a restored probe placement.' }
$baseline = $trace | Where-Object Event -eq 'baseline'
if ($baseline.Detail -cnotmatch '^src=0,0,0;job=0,0,0;gate=[01],[01],[01]$') { throw 'Expected native source and gate jobs to start at zero.' }

[pscustomobject]@{
    Session = $Session
    SourceId = $SourceId
    Placement = $arm.Detail
    Channels = 'Native signal inversion, pulse and value threshold observed'
    DespawnReset = if ($reset.Count -eq 1) { 'observed' } else { 'unobserved' }
}
