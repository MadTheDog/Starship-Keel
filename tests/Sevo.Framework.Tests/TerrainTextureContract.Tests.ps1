param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$RuntimeAssembly = 'src/Sevo.Framework.Runtime/bin/Release/netstandard2.1/Sevo.Framework.Runtime.dll'
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root '.refs/bepinex/Mono.Cecil.dll'))
$runtime = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($RuntimeAssembly, $Root))
$audit = $runtime.GetType('Sevo.Framework.Runtime.ContractAudit').GetMethod('EvaluateTerrainTextureOverrides')
if ($null -eq $audit) { throw 'FAIL: terrain texture override audit is missing.' }
$visualAudit = $runtime.GetType('Sevo.Framework.Runtime.ContractAudit').GetMethod('EvaluateTerrainMaterialVisuals')

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $Root '.refs/game/Assembly-CSharp.dll'))
try {
    $module = $assembly.MainModule
    function Assert-Capability([bool]$Expected, [string]$Case) {
        $result = $audit.Invoke($null, @($module))
        if ($result.Available -ne $Expected) { throw "FAIL: $Case -- $($result.Reason)" }
        Write-Output "PASS: $Case"
    }

    Assert-Capability $true 'stock terrain layer publication and cleanup'
    $module.Mvid = [Guid]::NewGuid()
    Assert-Capability $true 'unrelated MVID change'

    $awake = $module.GetType('ResourcesLoader').Methods | Where-Object Name -eq 'Awake'
    $normal = $awake.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldstr' -and $_.Operand -eq 'WORLD_NORMAL_TEX' }
    $normal.Operand = 'INCOMPATIBLE_TERRAIN_NORMAL'
    Assert-Capability $false 'stock shader global changed'
    if (!$visualAudit.Invoke($null, @($module)).Available) { throw 'FAIL: texture binding drift disabled the independent scale and brightness capability.' }
    $normal.Operand = 'WORLD_NORMAL_TEX'

    $push = $module.GetType('World.WorldTerrainResources').Methods | Where-Object Name -eq 'PushMaterialDatas'
    $mip = $push.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'GetPixels'
    }
    $stockOp = $mip.OpCode
    $mip.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
    Assert-Capability $false 'stock diffuse mip read changed'
    $mip.OpCode = $stockOp

    $bake = $module.GetType('World.WorldTerrainResources').Methods | Where-Object Name -eq 'RegenerateMaterialGradients'
    $rebuild = $bake.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'RebuildPlanetTextures'
    }
    $stockOp = $rebuild.OpCode
    $rebuild.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
    Assert-Capability $false 'distant planet texture bake disconnected'
    $rebuild.OpCode = $stockOp

    $release = $module.GetType('World.WorldTerrainResources').Methods | Where-Object Name -eq 'UnloadResourcesOnQuitToTitle'
    $release.Name = 'DifferentUnloadBoundary'
    Assert-Capability $false 'native resource cycle cleanup changed'
    $release.Name = 'UnloadResourcesOnQuitToTitle'
    Assert-Capability $true 'restored terrain texture contracts'
} finally { $assembly.Dispose() }
