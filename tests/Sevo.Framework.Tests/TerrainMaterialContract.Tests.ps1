param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$RuntimeAssembly = 'src/Sevo.Framework.Runtime/bin/Release/netstandard2.1/Sevo.Framework.Runtime.dll'
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root '.refs/bepinex/Mono.Cecil.dll'))
$runtime = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($RuntimeAssembly, $Root))
$audit = $runtime.GetType('Sevo.Framework.Runtime.ContractAudit').GetMethod('EvaluateTerrainMaterialRegistration')
if ($null -eq $audit) { throw 'FAIL: terrain material registration audit is missing.' }

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $Root '.refs/game/Assembly-CSharp.dll'))
try {
    $module = $assembly.MainModule
    function Assert-Capability([bool]$Expected, [string]$Case) {
        $result = $audit.Invoke($null, @($module))
        if ($result.Available -ne $Expected) { throw "FAIL: $Case -- $($result.Reason)" }
        Write-Output "PASS: $Case"
    }

    Assert-Capability $true 'stock terrain table, count, byte fields and resource teardown'
    $module.Mvid = [Guid]::NewGuid()
    Assert-Capability $true 'unrelated MVID change'

    $voxel = $module.GetType('World.Terrain.VoxelNodeData')
    $secondary = $voxel.Fields | Where-Object Name -eq 'texId1'
    $secondary.FieldType = $module.TypeSystem.Int16
    Assert-Capability $false 'secondary packed material representation changed'
    $secondary.FieldType = $module.TypeSystem.Byte

    $publish = $module.GetType('World.WorldTerrainResources').Methods |
        Where-Object { $_.Name -eq 'LoadResources' -and $_.Parameters.Count -eq 1 }
    $count = $publish.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldstr' -and $_.Operand -eq '_TerrainMaterialCount' }
    $count.Operand = '_IncompatibleTerrainCount'
    Assert-Capability $false 'global material count binding changed'
    $count.Operand = '_TerrainMaterialCount'

    $awake = $module.GetType('ResourcesLoader').Methods | Where-Object Name -eq 'Awake'
    $normal = $awake.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldstr' -and $_.Operand -eq 'WORLD_NORMAL_TEX' }
    $normal.Operand = 'INCOMPATIBLE_TERRAIN_NORMAL'
    Assert-Capability $true 'texture override binding drift leaves stock-backed material registration available'
    $normal.Operand = 'WORLD_NORMAL_TEX'

    Assert-Capability $true 'restored terrain material contracts'
} finally { $assembly.Dispose() }
