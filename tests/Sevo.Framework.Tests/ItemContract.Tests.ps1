param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$RuntimeAssembly = 'src/Sevo.Framework.Runtime/bin/Release/netstandard2.1/Sevo.Framework.Runtime.dll'
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root '.refs/bepinex/Mono.Cecil.dll'))
$runtime = [Reflection.Assembly]::LoadFrom((Join-Path $Root $RuntimeAssembly))
$audit = $runtime.GetType('Sevo.Framework.Runtime.ContractAudit').GetMethod('EvaluateItems')
if ($null -eq $audit) { throw 'FAIL: native item contract capability is missing.' }

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $Root '.refs/game/Assembly-CSharp.dll'))
try {
    function Assert-Capability([bool]$Expected, [string]$Case) {
        $result = $audit.Invoke($null, @($assembly.MainModule))
        if ($result.Available -ne $Expected) { throw "FAIL: $Case -- $($result.Reason)" }
        Write-Output "PASS: $Case"
    }
    Assert-Capability $true 'stock item resource and persistence contracts'
    $assembly.MainModule.Mvid = [Guid]::NewGuid()
    Assert-Capability $true 'unrelated MVID change'

    $module = $assembly.MainModule
    $load = $module.GetType('Game.ItemInstanceRenderResources').Methods | Where-Object Name -eq 'OnRepoLoaded'
    $publish = $load.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'RegisterRenderData' }
    $savedOpcode = $publish.OpCode
    $publish.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
    Assert-Capability $false 'missing stock item table population'
    $publish.OpCode = $savedOpcode

    $render = $module.GetType('Game.ItemInstanceData').Methods | Where-Object Name -eq 'SetRenderData'
    $binding = $render.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq 'Ldsflda' -and $_.Operand.Name -eq 'PrefabDatas'
    } | Select-Object -First 1
    $savedOperand = $binding.Operand
    $binding.Operand = $module.GetType('Game.ItemInstanceRenderResources').Fields | Where-Object Name -eq 'BrickPrefabDatas'
    Assert-Capability $false 'render path no longer exclusively uses the world-prefab table'
    $binding.Operand = $savedOperand

    $formatter = $module.GetType('MessagePack.Formatters.Game.ItemSaveDataFormatter')
    $codec = $formatter.Methods | Where-Object Name -eq 'Deserialize'
    $payload = $codec.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Stfld' -and $_.Operand.Name -eq 'data' }
    $savedOpcode = $payload.OpCode
    $payload.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
    Assert-Capability $false 'item codec loses the persistent identity payload'
    $payload.OpCode = $savedOpcode

    $item = $module.GetType('Game.ItemInstanceData')
    $save = $item.Methods | Where-Object Name -eq 'get_SaveData'
    $guard = $save.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Bne_Un_S' }
    $savedOperand = $guard.Previous.Operand
    $guard.Previous.Operand = [sbyte]($module.GetType('Game.WorldItemType').Fields |
        Where-Object Name -eq 'craftingParts').Constant
    Assert-Capability $false 'stock claims the payload for crafting parts'
    $guard.Previous.Operand = $savedOperand

    Assert-Capability $true 'restored contracts'
} finally { $assembly.Dispose() }
