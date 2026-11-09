param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$RuntimeAssembly = 'src/Sevo.Framework.Runtime/bin/Release/netstandard2.1/Sevo.Framework.Runtime.dll'
)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root '.refs/bepinex/Mono.Cecil.dll'))
$runtime = [Mono.Cecil.AssemblyDefinition]::ReadAssembly([IO.Path]::GetFullPath($RuntimeAssembly, $Root))
try {
    $registration = $runtime.MainModule.GetType('Sevo.Framework.Runtime.TerrainMaterialRegistration')
    $register = @($registration.Methods | Where-Object { $_.Name -eq 'Register' -and $_.IsPublic -and $_.IsStatic })
    $stock = $register | Where-Object { $_.Parameters.Count -eq 4 }
    $textured = $register | Where-Object { $_.Parameters.Count -eq 5 }
    if ($null -eq $stock -or $stock.ReturnType.FullName -ne 'System.Void' -or
        ($stock.Parameters.ParameterType.FullName -join ',') -ne 'System.String,System.String,System.Int32,System.String') {
        throw 'FAIL: binary-compatible stock-backed terrain registration is missing.'
    }
    Write-Output 'PASS: existing public terrain registration signature retained'
    if ($null -eq $textured -or $textured.ReturnType.FullName -ne 'System.Void' -or
        $textured.Parameters[4].ParameterType.FullName -ne 'System.Func`1<Sevo.Framework.Runtime.TerrainTextureLayers>') {
        throw 'FAIL: new material registration cannot request a validated pair of texture layers.'
    }
    Write-Output 'PASS: textured material registration uses the existing layer definition'
} finally { $runtime.Dispose() }
