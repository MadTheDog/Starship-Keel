param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$RuntimeAssembly = 'src/Sevo.Framework.Runtime/bin/Release/netstandard2.1/Sevo.Framework.Runtime.dll',
    [string]$SdkAssembly = 'src/Sevo.Framework.Sdk/bin/Release/netstandard2.1/Sevo.Framework.Sdk.dll'
)
$ErrorActionPreference = 'Stop'
foreach ($reference in @(
    '.refs/bepinex/Mono.Cecil.dll', '.refs/bepinex/0Harmony.dll',
    '.refs/game/UnityEngine.CoreModule.dll', '.refs/game/Unity.Collections.dll',
    '.refs/game/UnityEngine.MathematicsModule.dll', '.refs/game/Assembly-CSharp.dll'
)) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $Root $reference))
}
[void][Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($SdkAssembly, $Root))
$runtime = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($RuntimeAssembly, $Root))
$material = $runtime.GetType('Sevo.Framework.Runtime.TerrainMaterialRegistration')
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$admission = $material.GetMethod('GetWorldAdmissionFailure', $flags)
$hasState = $material.GetMethod('HasPersistentTerrainMaterialState', $flags)

$fixture = Join-Path $Root ('.cache/terrain-material-admission-' + [Guid]::NewGuid().ToString('N'))
$game = Join-Path $fixture 'game'
$state = Join-Path $fixture 'profile'
[void](New-Item -ItemType Directory -Path $game, $state)
$profileId = [Guid]::NewGuid()
$marker = Join-Path $state 'sevo-runtime-profile.json'
$registry = Join-Path $state 'terrain-material-registry.json'
try {
    @{ schemaVersion = 1; profileId = $profileId.ToString('D'); gameDirectory = $game } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath $marker -Encoding utf8
    $session = [Sevo.Framework.Runtime.ProfileProtocol]::Open($state, $game, 'test')
    if ($hasState.Invoke($null, @($session)) -or $admission.Invoke($null, @($session, $false))) {
        throw 'FAIL: empty profile was blocked.'
    }
    Write-Output 'PASS: stock profile can enter a world with the material capability disabled'

    $kind = [Sevo.Framework.Sdk.ResourceKind]::TerrainMaterial
    $spaces = [Sevo.Framework.Sdk.ResourceSpace[]]@(
        [Sevo.Framework.Sdk.ResourceSpace]::new($kind, 255, [int[]]@(), [int[]](0..53))
    )
    $allocator = [Sevo.Framework.Sdk.ContentAllocator]::new($spaces, $profileId)
    $first = [Sevo.Framework.Sdk.ContentRequest]::new('test.terrain:ridge', $kind, 1)
    $second = [Sevo.Framework.Sdk.ContentRequest]::new('test.terrain:ore', $kind, 1)
    $ids = $allocator.Apply([Sevo.Framework.Sdk.ContentRequest[]]@($first, $second))
    $oldRidge = $ids['test.terrain:ridge']
    $oldOre = $ids['test.terrain:ore']
    $restored = [Sevo.Framework.Sdk.ContentAllocator]::FromJson($allocator.ToJson(), $spaces)
    $ids = $restored.Apply([Sevo.Framework.Sdk.ContentRequest[]]@($second))
    $mapping = [Sevo.Framework.Sdk.RegistryJson]::Read($restored.ToJson()).Mappings |
        Where-Object Key -eq 'test.terrain:ridge'
    if ($oldRidge -lt 54 -or $oldRidge -ge 255 -or $oldOre -eq $oldRidge -or $ids['test.terrain:ore'] -ne $oldOre -or
        !$mapping.Tombstone -or $mapping.Id -ne $oldRidge) {
        throw 'FAIL: persisted terrain IDs or tombstones changed.'
    }
    Write-Output 'PASS: assigned IDs stay outside stock range and removed IDs remain reserved'

    $restored.ToJson() | Set-Content -LiteralPath $registry -Encoding utf8
    if (!$hasState.Invoke($null, @($session)) -or
        $admission.Invoke($null, @($session, $false)) -notmatch 'restore' -or
        $admission.Invoke($null, @($session, $true)) -notmatch 'solo play') {
        throw 'FAIL: inactive or remote terrain material state was admitted.'
    }
    Write-Output 'PASS: an existing terrain registry blocks local opt-out and remote entry'

    'invalid registry' | Set-Content -LiteralPath $registry -Encoding utf8
    if (!$hasState.Invoke($null, @($session)) -or $admission.Invoke($null, @($session, $false)) -notmatch 'validate') {
        throw 'FAIL: corrupt terrain registry was admitted.'
    }
    Write-Output 'PASS: corrupt registry counts as persistent state and blocks entry'
}
finally {
    foreach ($file in @($marker, $registry)) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
    foreach ($directory in @($state, $game, $fixture)) {
        if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory }
    }
}
