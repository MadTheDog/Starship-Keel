param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$RuntimeAssembly = 'src/Sevo.Framework.Runtime/bin/Debug/netstandard2.1/Sevo.Framework.Runtime.dll'
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root '.refs/bepinex/Mono.Cecil.dll'))
$runtime = [Reflection.Assembly]::LoadFrom((Join-Path $Root $RuntimeAssembly))
$audit = $runtime.GetType('Sevo.Framework.Runtime.ContractAudit').GetMethod('EvaluateModularReceiverRendering')
if ($null -eq $audit) { throw 'FAIL: modular receiver rendering contract capability is missing.' }
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $Root '.refs/game/Assembly-CSharp.dll'))
try {
    function Assert-Capability([bool]$Expected, [string]$Case) {
        $result = $audit.Invoke($null, @($assembly.MainModule))
        if ($result.Available -ne $Expected) { throw "FAIL: $Case -- $($result.Reason)" }
        Write-Output "PASS: $Case"
    }
    Assert-Capability $true 'native modular render contract'
    $assembly.MainModule.Mvid = [Guid]::NewGuid()
    Assert-Capability $true 'unrelated MVID change'
    $brick = $assembly.MainModule.GetType('VoxelEngine.Brick')
    $set = $brick.Methods | Where-Object Name -eq 'SetRenderData'
    $guard = $set.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Brtrue_S' } | Select-Object -First 1
    $savedOpcode = $guard.OpCode
    $guard.OpCode = [Mono.Cecil.Cil.OpCodes]::Brfalse_S
    Assert-Capability $false 'inverted nonzero geometry guard'
    $guard.OpCode = $savedOpcode
    $release = $brick.Methods | Where-Object Name -eq 'ReleaseRenderData'
    $free = $release.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'FreeIndex' } | Select-Object -First 1
    $savedOperand = $free.Operand
    $free.Operand = $set
    Assert-Capability $false 'missing primary data release'
    $free.Operand = $savedOperand
    $aabb = $assembly.MainModule.GetType('VoxelEngine.BrickInstanceData').Methods | Where-Object Name -eq 'GetAabb'
    $grid = $aabb.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'get_GridSize' } | Select-Object -First 1
    $savedOperand = $grid.Operand
    $grid.Operand = $set
    Assert-Capability $false 'missing native grid-size conversion'
    $grid.Operand = $savedOperand
    Assert-Capability $true 'restored contracts'
} finally { $assembly.Dispose() }
