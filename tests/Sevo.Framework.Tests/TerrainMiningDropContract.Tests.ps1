param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$RuntimeAssembly = 'src/Sevo.Framework.Runtime/bin/Release/netstandard2.1/Sevo.Framework.Runtime.dll'
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root '.refs/bepinex/Mono.Cecil.dll'))
$runtime = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($RuntimeAssembly, $Root))
$audit = $runtime.GetType('Sevo.Framework.Runtime.ContractAudit').GetMethod('EvaluateTerrainMiningDrops')
if ($null -eq $audit) { throw 'FAIL: native terrain mining drop audit is missing.' }

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $Root '.refs/game/Assembly-CSharp.dll'))
try {
    $module = $assembly.MainModule
    function Assert-Capability([bool]$Expected, [string]$Case) {
        $result = $audit.Invoke($null, @($module))
        if ($result.Available -ne $Expected) { throw "FAIL: $Case -- $($result.Reason)" }
        Write-Output "PASS: $Case"
    }

    Assert-Capability $true 'stock managed terrain mining spawn and impulse'
    $module.Mvid = [Guid]::NewGuid()
    Assert-Capability $true 'unrelated MVID change'

    $mining = $module.GetType('World.Terrain.OctreeNodeResources').Methods | Where-Object Name -eq 'CheckIfDropRock'
    $spawn = $mining.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'SpawnRandomItem'
    }
    $stockOp = $spawn.OpCode
    $spawn.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
    Assert-Capability $false 'stock spawn call removed'
    $spawn.OpCode = $stockOp

    $impulsedItem = $spawn.Next.Next
    $stockLocal = $impulsedItem.Operand
    $impulsedItem.Operand = $mining.Body.Variables[6]
    Assert-Capability $false 'stock impulse no longer consumes the spawned item value'
    $impulsedItem.Operand = $stockLocal

    $randomSpawn = $module.GetType('Game.ItemInstanceData').Methods | Where-Object Name -eq 'SpawnRandomItem'
    $allocate = $randomSpawn.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'AllocateAllComponents'
    }
    $stockOp = $allocate.OpCode
    $allocate.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
    Assert-Capability $false 'stock spawn allocation changed'
    $allocate.OpCode = $stockOp
    $draw = $randomSpawn.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'GetFloatRange'
    }
    $scaleStore = $draw.Next
    $stockOp = $scaleStore.OpCode
    $scaleStore.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
    Assert-Capability $false 'native spawn no longer stores the seeded scale before registration'
    $scaleStore.OpCode = $stockOp

    $prefabDraw = $randomSpawn.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'GetIntRange'
    }
    $stockOp = $prefabDraw.OpCode
    $prefabDraw.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
    Assert-Capability $false 'native scale no longer follows the stock prefab RNG draw'
    $prefabDraw.OpCode = $stockOp
    Assert-Capability $true 'restored stock mining contracts'
} finally { $assembly.Dispose() }
