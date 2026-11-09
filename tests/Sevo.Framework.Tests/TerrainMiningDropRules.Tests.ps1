param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$RuntimeAssembly = 'src/Sevo.Framework.Runtime/bin/Release/netstandard2.1/Sevo.Framework.Runtime.dll'
)
$ErrorActionPreference = 'Stop'
foreach ($reference in @(
    '.refs/bepinex/Mono.Cecil.dll', '.refs/bepinex/0Harmony.dll',
    '.refs/game/UnityEngine.CoreModule.dll', '.refs/game/Unity.Collections.dll',
    '.refs/game/Unity.Mathematics.dll', '.refs/game/UnityEngine.MathematicsModule.dll', '.refs/game/Assembly-CSharp.dll'
)) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $Root $reference))
}
$runtimePath = [IO.Path]::GetFullPath($RuntimeAssembly, $Root)
[void][Reflection.Assembly]::LoadFrom((Join-Path (Split-Path -Parent $runtimePath) 'Sevo.Framework.Sdk.dll'))
$runtime = [Reflection.Assembly]::LoadFrom($runtimePath)
$type = $runtime.GetType('Sevo.Framework.Runtime.TerrainMiningDropRegistration')
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$seal = $type.GetMethod('Seal', $flags)

[Sevo.Framework.Runtime.TerrainMiningDropRegistration]::Register('test.mining', 'one', 'test.terrain:conflict', 'test.items:ore', [single]1)
[Sevo.Framework.Runtime.TerrainMiningDropRegistration]::Register('test.compatibility', 'two', 'test.terrain:conflict', 'test.items:ore', [single]2)
[Sevo.Framework.Runtime.TerrainMiningDropRegistration]::Register('test.mining', 'three', 'test.terrain:unique', 'test.items:ore', [single]3)

try {
    [Sevo.Framework.Runtime.TerrainMiningDropRegistration]::Register('test.mining', 'three', 'test.terrain:other', 'test.items:ore', [single]3)
    throw 'FAIL: duplicate declaration key was accepted.'
}
catch {
    if ($_.Exception.InnerException.Message -notmatch 'Duplicate terrain mining drop') { throw }
}
Write-Output 'PASS: duplicate namespaced declarations are rejected'

$selected = $seal.Invoke($null, @())
if ($selected.Count -ne 1 -or !$selected.ContainsKey('test.terrain:unique') -or $selected.ContainsKey('test.terrain:conflict')) {
    throw 'FAIL: conflicting material declarations did not retain stock while allowing a disjoint rule.'
}
Write-Output 'PASS: conflicting material rules retain stock and disjoint rules are available'
