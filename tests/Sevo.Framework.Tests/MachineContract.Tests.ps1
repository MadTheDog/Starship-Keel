param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$RuntimeAssembly = 'src/Sevo.Framework.Runtime/bin/Release/netstandard2.1/Sevo.Framework.Runtime.dll'
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root '.refs/bepinex/Mono.Cecil.dll'))
$runtime = [Reflection.Assembly]::LoadFrom((Join-Path $Root $RuntimeAssembly))
$audit = $runtime.GetType('Sevo.Framework.Runtime.ContractAudit').GetMethod('EvaluateMachineRecipes')
if ($null -eq $audit) { throw 'FAIL: machine recipe contract audit is missing.' }

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $Root '.refs/game/Assembly-CSharp.dll'))
try {
    $module = $assembly.MainModule
    function Assert-Capability([bool]$Expected, [string]$Case) {
        $result = $audit.Invoke($null, @($module))
        if ($result.Available -ne $Expected) { throw "FAIL: $Case -- $($result.Reason)" }
        Write-Output "PASS: $Case"
    }

    Assert-Capability $true 'stock Arc Furnace and Crusher bridge contracts'
    $module.Mvid = [Guid]::NewGuid()
    Assert-Capability $true 'unrelated assembly identity change'

    $furnace = $module.GetType('VoxelEngine.Crafting.FurnaceCommandData')
    $update = $furnace.Methods | Where-Object Name -eq 'UpdateOnJob'
    $powerRate = $update.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldc_R4' -and $_.Operand -eq 400.0 }
    if (@($powerRate).Count -ne 1) { throw 'FAIL: cannot locate native machine power rate for mutation.' }
    $original = $powerRate.Operand
    $powerRate.Operand = [single]650.0
    Assert-Capability $true 'native power balance changes retain the same accounting contract'
    $powerRate.Operand = $original
    $gate = $update.Body.Instructions | Where-Object {
        $_.OpCode.Code -in 'Ble_Un', 'Ble_Un_S' -and $_.Previous.OpCode.Code -eq 'Ldc_R4' -and
        $_.Previous.Operand -eq 1.0 -and $_.Previous.Previous.Operand.Name -eq 'get_completion'
    }
    if (@($gate).Count -ne 1) { throw 'FAIL: cannot locate native Arc Furnace completion branch for mutation.' }
    $original = $gate.OpCode
    $gate.OpCode = [Mono.Cecil.Cil.OpCodes]::Blt_Un_S
    Assert-Capability $false 'Arc Furnace must not commit at the managed completion ceiling'
    $gate.OpCode = $original

    $crusher = $module.GetType('VoxelEngine.Crafting.CrusherCommandData')
    $update = $crusher.Methods | Where-Object Name -eq 'UpdateOnJob'
    $gate = $update.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq 'Cgt' -and $_.Previous.OpCode.Code -eq 'Ldc_R4' -and
        $_.Previous.Operand -eq 1.0 -and $_.Previous.Previous.Operand.Name -eq 'get_completion'
    }
    if (@($gate).Count -ne 1) { throw 'FAIL: cannot locate native Crusher completion comparison for mutation.' }
    $original = $gate.OpCode
    $gate.OpCode = [Mono.Cecil.Cil.OpCodes]::Clt
    Assert-Capability $false 'Crusher must not commit below the managed completion ceiling'
    $gate.OpCode = $original

    $loop = $module.GetType('Manager.GameloopManager')
    $after = $loop.Methods | Where-Object Name -eq 'OnAfterRendering'
    $complete = $after.Body.Instructions | Where-Object { $_.Operand.Name -eq 'CompleteJobs' }
    $commands = $after.Body.Instructions | Where-Object { $_.Operand.Name -eq 'ExecuteCommandsJob' }
    $original = $complete.Operand
    $complete.Operand = $commands.Operand
    $commands.Operand = $original
    Assert-Capability $false 'native commit callback must follow job completion and disposal'
    $commands.Operand = $complete.Operand
    $complete.Operand = $original

    $ui = $module.GetType('UI.UI_Crusher')
    $awake = $ui.Methods | Where-Object Name -eq 'Awake'
    $outputLock = $awake.Body.Instructions | Where-Object { $_.Operand.Name -eq 'SetIsBlocked' }
    if (@($outputLock).Count -ne 1) { throw 'FAIL: cannot locate native output lock for mutation.' }
    $original = $outputLock.Previous.OpCode
    $outputLock.Previous.OpCode = [Mono.Cecil.Cil.OpCodes]::Ldc_I4_0
    Assert-Capability $false 'pending Crusher output must stay locked against manual takeout'
    $outputLock.Previous.OpCode = $original

    $request = $module.GetType('VoxelEngine.ItemRequestData')
    $match = $request.Methods | Where-Object { $_.Name -eq 'IsMatching' -and $_.Parameters.Count -eq 2 }
    $oreContext = $match.Body.Instructions | Where-Object {
        $_.Operand.Name -eq 'contextualDataId' -and $_.Operand.DeclaringType.FullName -eq 'Game.ItemInstanceData'
    } | Select-Object -Skip 1 -First 1
    if ($null -eq $oreContext) { throw 'FAIL: cannot locate ore context comparison for mutation.' }
    $original = $oreContext.Operand
    $oreContext.Operand = $module.GetType('Game.ItemInstanceData').Fields | Where-Object Name -eq 'prefabId'
    Assert-Capability $false 'ore conveyor requests must match material context independently of prefab'
    $oreContext.Operand = $original

    $save = $furnace.Methods | Where-Object Name -eq 'GetSaveData'
    $snapshot = $save.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq 'Ldfld' -and $_.Operand.Name -eq 'inputItemId'
    } | Select-Object -First 1
    $original = $snapshot.Operand
    $snapshot.Operand = $furnace.Fields | Where-Object Name -eq 'outputItemId'
    Assert-Capability $false 'unfinished native work must persist the input instead of pending output'
    $snapshot.Operand = $original

    Assert-Capability $true 'restored native contracts'
} finally { $assembly.Dispose() }
