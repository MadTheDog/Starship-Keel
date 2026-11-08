param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$RuntimeAssembly = 'src/Sevo.Framework.Runtime/bin/Release/netstandard2.1/Sevo.Framework.Runtime.dll'
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root '.refs/bepinex/Mono.Cecil.dll'))
$runtime = [Reflection.Assembly]::LoadFrom((Join-Path $Root $RuntimeAssembly))
$audit = $runtime.GetType('Sevo.Framework.Runtime.ContractAudit').GetMethod('EvaluateItems')
if ($null -eq $audit) { throw 'FAIL: native item contract capability is missing.' }
$visualAudit = $runtime.GetType('Sevo.Framework.Runtime.ContractAudit').GetMethod('EvaluateItemVisuals')
if ($null -eq $visualAudit) { throw 'FAIL: item representation contract capability is missing.' }
$surfaceAudit = $runtime.GetType('Sevo.Framework.Runtime.ContractAudit').GetMethod('EvaluateTintedItemSurface')
if ($null -eq $surfaceAudit) { throw 'FAIL: tinted item surface contract capability is missing.' }

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
    function Assert-VisualCapability([bool]$Expected, [string]$Case) {
        $result = $visualAudit.Invoke($null, @($module))
        if ($result.Available -ne $Expected) { throw "FAIL: $Case -- $($result.Reason)" }
        Write-Output "PASS: $Case"
    }
    Assert-VisualCapability $true 'item representation accepts unrelated MVID change'
    $preview = $module.GetType('VoxelEngine.ItemPreview').Methods | Where-Object Name -eq 'ComputeGeoItem'
    $submission = $preview.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'SetRenderData' -and $_.Operand.DeclaringType.FullName -eq 'Game.ItemInstanceData'
    }
    $savedOpcode = $submission.OpCode
    $submission.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
    Assert-VisualCapability $false 'preview no longer uses the native item renderer'
    Assert-Capability $true 'preview contract drift leaves basic item registration available'
    $submission.OpCode = $savedOpcode
    $packedNormal = $module.GetType('World.PropMeshData').Fields | Where-Object Name -eq 'normal'
    $packedNormal.Name = 'changedNormalLayout'
    Assert-VisualCapability $false 'native GPU vertex layout changed'
    $packedNormal.Name = 'normal'
    Assert-VisualCapability $true 'restored item representation contracts'

    $surface = $surfaceAudit.Invoke($null, @($module))
    if (!$surface.Available) { throw "FAIL: stock tinted material and unrelated MVID -- $($surface.Reason)" }
    $rockMode = $module.GetType('Game.WorldPropRenderType').Fields | Where-Object Name -eq 'rock'
    $originalMode = $rockMode.Constant
    $rockMode.Constant = 91
    $surface = $surfaceAudit.Invoke($null, @($module))
    if ($surface.Available) { throw 'FAIL: changed native material code was admitted.' }
    Assert-VisualCapability $true 'material drift leaves stock-backed item visuals available'
    $rockMode.Constant = $originalMode
    $surface = $surfaceAudit.Invoke($null, @($module))
    if (!$surface.Available) { throw "FAIL: restored tinted material -- $($surface.Reason)" }
    Write-Output 'PASS: tinted item material admits the matching shader contract only'
} finally { $assembly.Dispose() }
