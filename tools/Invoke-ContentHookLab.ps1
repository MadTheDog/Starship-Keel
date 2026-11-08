param(
    [ValidateSet('Baseline','Audit','Hooks')][string]$Mode = 'Audit',
    [ValidateRange(10,300)][int]$TimeoutSeconds = 75,
    [string[]]$ExpectedLog = @(),
    [switch]$Rendered,
    [switch]$FluidProbe,
    [switch]$CraftProbe,
    [ValidateSet('d3d11','d3d12')][string]$Graphics = 'd3d11',
    [string]$ProbeMaterial,
    [string[]]$ExpectedPlayerLog = @(),
    [ValidateSet('ITEMS','GATHERING','RECIPES','FLUIDS','ORE_GENERATION','ORE_SCANNER','TERRAIN_MATERIAL_VISUALS')][string[]]$DisableHook = @(),
    [switch]$DiagnosticJobCompletion,
    [switch]$ScanProbe,
    [switch]$FluidRoundtrip
)
. (Join-Path $PSScriptRoot 'Environment.ps1')
if ($FluidProbe -and ($Mode -ne 'Hooks' -or !$Rendered)) { throw 'The fluid world fixture requires rendered Hooks mode.' }
& (Join-Path $PSScriptRoot 'Prepare-ContentHookLab.ps1') -VerifyOnly | Out-Null
$game = Assert-PlatformChild (Join-Path $PlatformRoot 'lab/game')
$session = [guid]::NewGuid().ToString('N')
$state = Assert-PlatformChild (Join-Path $PlatformRoot "local-evidence/lab-runs/$session")
New-Item -ItemType Directory -Path $state | Out-Null
@{schemaVersion=1;profileId=[guid]::NewGuid().ToString('D');gameDirectory=$game} |
    ConvertTo-Json -Compress | Set-Content -Encoding utf8NoBOM -LiteralPath (Join-Path $state 'sevo-runtime-profile.json')
$executable = Assert-PlatformChild (Join-Path $game 'Starship EVO.exe')
$start = [Diagnostics.ProcessStartInfo]::new($executable)
$start.UseShellExecute = $false
$start.WorkingDirectory = $game
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.CreateNoWindow = $true
$start.Environment['SEVO_PLATFORM_STATE_DIR'] = $state
$start.Environment['SEVO_PLATFORM_SESSION'] = $session
$start.Environment['SteamAppId'] = '711980'
$start.Environment['SteamGameId'] = '711980'
foreach ($flag in @('ITEMS','GATHERING','RECIPES','FLUIDS','ORE_GENERATION','ORE_SCANNER','TERRAIN_MATERIAL_VISUALS')) {
    $start.Environment["SEVO_KEEL_EXPERIMENTAL_$flag"] = if ($Mode -eq 'Hooks' -and $flag -notin $DisableHook) { '1' } else { '0' }
}
if ($FluidProbe) {
    $start.Environment['SEVO_KEEL_FLUID_PROBE'] = '1'
    $start.Environment['SEVO_KEEL_FLUID_PROBE_START_SOLO'] = '1'
}
if (!$Rendered) { $start.ArgumentList.Add('-batchmode') }
if ($DiagnosticJobCompletion) { $start.Environment['SEVO_KEEL_FLUID_PROBE_DRAIN_STALLED_FRAME'] = '1' }
if ($FluidRoundtrip) { $start.Environment['SEVO_KEEL_FLUID_PROBE_ROUNDTRIP'] = '1' }
if ($ScanProbe) { $start.Environment['SEVO_KEEL_ORE_PROBE_SCAN'] = '1' }
if ($CraftProbe) { $start.Environment['SEVO_KEEL_RECIPE_PROBE_CRAFT'] = '1' }
if ($ProbeMaterial) { $start.Environment['SEVO_KEEL_ORE_PROBE_MATERIAL'] = $ProbeMaterial }
foreach ($argument in @("-force-$Graphics",'-screen-fullscreen','0','-screen-width','800','-screen-height','600','-logFile',(Join-Path $state 'player.log'))) {
    $start.ArgumentList.Add($argument)
}
$playerLog = Join-Path $state 'player.log'
$loaderLog = Join-Path $game 'BepInEx/LogOutput.log'
$process = [Diagnostics.Process]::Start($start)
$started = $process.StartTime
try {
    $statusPath = Join-Path $state 'runtime-status.json'
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path -LiteralPath $statusPath) {
            $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
            if ($status.session -eq $session -and $status.processId -eq $process.Id -and ($status.resourcesReady -or $status.failed)) {
                $status | ConvertTo-Json -Depth 8
                if ($status.failed) { throw $status.reason }
                while (($ExpectedLog.Count -gt 0 -or $ExpectedPlayerLog.Count -gt 0) -and !$process.HasExited -and [datetime]::UtcNow -lt $deadline) {
                    if (Test-Path -LiteralPath $loaderLog) {
                        $log = Get-Content -LiteralPath $loaderLog -Raw
                        if ($FluidProbe -and $log -match "KEEL_FLUID_PROBE FAIL[^\r\n]*session=$session") {
                            throw $Matches[0]
                        }
                        if ($log -match "KEEL_MACHINE_PROBE FAIL[^\r\n]*session=$session") {
                            throw $Matches[0]
                        }
                        $player = if (Test-Path -LiteralPath $playerLog) { Get-Content -LiteralPath $playerLog -Raw } else { "" }
                        $missing = @($ExpectedLog | Where-Object { !$log.Contains($_) })
                        $missing += @($ExpectedPlayerLog | Where-Object { !$player.Contains($_) })
                        if ($missing.Count -eq 0) { Write-Host ($ExpectedLog -join ", "); return }
                    }
                    Start-Sleep -Milliseconds 500
                }
                if ($ExpectedLog.Count -gt 0 -or $ExpectedPlayerLog.Count -gt 0) { throw "Expected probe evidence missing in $state." }
                return
            }
        }
        Start-Sleep -Milliseconds 500
    } until ($process.HasExited -or [datetime]::UtcNow -ge $deadline)
    if ($process.HasExited) { throw "Game exited with code $($process.ExitCode) before Keel readiness. See $state." }; throw "Keel readiness was not observed for $Mode. See $state."
} finally {
    if (!$process.HasExited) {
        if ($process.StartTime -ne $started -or $process.MainModule.FileName -ne $executable) { throw 'Owned process identity changed' }
        if (!$process.CloseMainWindow() -or !$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit(10000) | Out-Null }
    }
    $playerLog = Join-Path $state 'player.log'
$loaderLog = Join-Path $game 'BepInEx/LogOutput.log'
    if (Test-Path -LiteralPath $loaderLog) { Copy-Item -LiteralPath $loaderLog -Destination (Join-Path $state 'bepinex.log') }
    Write-Host "Evidence: $state"
    $process.Dispose()
}
