param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$RuntimeAssembly = 'src/Sevo.Framework.Runtime/bin/Debug/netstandard2.1/Sevo.Framework.Runtime.dll'
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root '.refs/bepinex/Mono.Cecil.dll'))
$runtime = [Reflection.Assembly]::LoadFrom((Join-Path $Root $RuntimeAssembly))
$audit = $runtime.GetType('Sevo.Framework.Runtime.ContractAudit').GetMethod('EvaluateTerrainDepositNativeHighlights')
if ($null -eq $audit) { throw 'Missing native deposit highlight audit.' }

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $Root '.refs/game/Assembly-CSharp.dll'))
try {
    function Assert-Highlight([bool]$expected, [string]$label) {
        $result = $audit.Invoke($null, @($assembly.MainModule))
        if ($result.Available -ne $expected) { throw "FAIL $label - $($result.Reason)" }
        Write-Output "PASS $label"
    }

    Assert-Highlight $true 'stock highlight and separate widget copy'
    $assembly.MainModule.Mvid = [Guid]::NewGuid()
    Assert-Highlight $true 'unrelated assembly identity change'

    $scanner = $assembly.MainModule.GetType('World.Terrain.OreScannerResources')
    $schedule = $scanner.Methods | Where-Object Name -eq ScheduleFromGraph
    $ctor = $schedule.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq 'Newobj' -and $_.Next.Operand.Name -eq 'oreDatas'
    }
    $original = $ctor.OpCode
    $ctor.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
    Assert-Highlight $false 'widget job loses private ore array copy'
    $ctor.OpCode = $original

    $execute = $scanner.NestedTypes.Methods | Where-Object Name -eq Execute
    $skip = ($execute.Body.Instructions | Where-Object { $_.Operand.Name -eq 'transformId' } | Select-Object -First 1).Next
    $original = $skip.OpCode
    $skip.OpCode = [Mono.Cecil.Cil.OpCodes]::Brtrue
    Assert-Highlight $false 'widget job no longer skips zero transforms'
    $skip.OpCode = $original

    $register = $scanner.Methods | Where-Object Name -eq RegisterData
    $chain = $register.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Stfld' -and $_.Operand.Name -eq 'nextElementId' }
    $original = $chain.Previous.OpCode
    $chain.Previous.OpCode = [Mono.Cecil.Cil.OpCodes]::Ldc_I4_0
    Assert-Highlight $false 'registration chain no longer carries prior row'
    $chain.Previous.OpCode = $original

    $unexpectedCall = [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call,
        [Mono.Cecil.MethodReference]$register)
    $schedule.Body.Instructions.Insert(0, $unexpectedCall)
    Assert-Highlight $false 'another stock registration path could bypass ownership'
    $schedule.Body.Instructions.RemoveAt(0)

    Assert-Highlight $true 'restored highlight contracts'
}
finally { $assembly.Dispose() }
