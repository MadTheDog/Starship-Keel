param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$RuntimeAssembly = 'src/Sevo.Framework.Runtime/bin/Debug/netstandard2.1/Sevo.Framework.Runtime.dll'
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root '.refs/bepinex/Mono.Cecil.dll'))
$runtime = [Reflection.Assembly]::LoadFrom((Join-Path $Root $RuntimeAssembly))
$audit = $runtime.GetType('Sevo.Framework.Runtime.ContractAudit').GetMethod('EvaluateTerrainDepositScanner')
if ($null -eq $audit) { throw 'Missing terrain deposit scanner audit.' }

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $Root '.refs/game/Assembly-CSharp.dll'))
try {
    function Assert-Scanner([bool]$expected, [string]$label) {
        $result = $audit.Invoke($null, @($assembly.MainModule))
        if ($result.Available -ne $expected) { throw "FAIL $label - $($result.Reason)" }
        Write-Output "PASS $label"
    }

    Assert-Scanner $true 'stock pulse and resident voxel query'
    $assembly.MainModule.Mvid = [Guid]::NewGuid()
    Assert-Scanner $true 'unrelated assembly identity change'

    $node = $assembly.MainModule.GetType('World.Terrain.OctreeNodeData')
    $width = $node.Fields | Where-Object Name -eq VOXEL_WIDTH
    $width.Constant = 18
    Assert-Scanner $false 'native interior sample layout change'
    $width.Constant = 16

    $frame = $assembly.MainModule.GetType('Manager.GameloopManager').Methods | Where-Object Name -eq OnAfterRendering
    $ready = $frame.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Stsfld' -and $_.Operand.Name -eq 'jobFrameState' }
    $original = $ready.Previous.OpCode
    $ready.Previous.OpCode = [Mono.Cecil.Cil.OpCodes]::Ldc_I4_1
    Assert-Scanner $false 'native completed-frame boundary change'
    $ready.Previous.OpCode = $original

    $job = $assembly.MainModule.GetType('World.Terrain.OctreeNodeJobs/Job_GeoCompute')
    $geometry = $job.Methods | Where-Object Name -eq ComputeGeoState
    $coordinate = $geometry.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Stfld' -and $_.Operand.Name -eq 'coordinate' }
    $coordinateZ = $coordinate.Previous.Previous
    $originalVariable = $coordinateZ.Operand
    $coordinateZ.Operand = $coordinateZ.Previous.Operand
    Assert-Scanner $false 'native scanner record no longer identifies originating voxel'
    $coordinateZ.Operand = $originalVariable
    Assert-Scanner $true 'restored scanner contracts'
}
finally { $assembly.Dispose() }
