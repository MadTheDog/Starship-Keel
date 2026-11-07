param([ValidateSet('Debug','Release')][string]$Configuration='Release')
. (Join-Path $PSScriptRoot '../tools/Environment.ps1')
$sampleOutput=Assert-PlatformChild (Join-Path $PSScriptRoot ('artifacts/'+[Guid]::NewGuid().ToString('N'))) $PSScriptRoot
New-Item -ItemType Directory -Path $sampleOutput | Out-Null
foreach($sampleName in @('Alpha','Beta')) {
    $sampleProject=Join-Path $PSScriptRoot "Example.$sampleName/Example.$sampleName.csproj"
    & $PlatformDotnet build $sampleProject -c $Configuration --disable-build-servers -m:1 /p:UseSharedCompilation=false
    if($LASTEXITCODE) { throw "Sample build failed: $sampleName" }
    $samplePackage=Assert-PlatformChild (Join-Path $sampleOutput "Example.$sampleName") $PSScriptRoot
    $samplePayload=Join-Path $samplePackage 'plugin'
    New-Item -ItemType Directory -Path $samplePayload | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Example.$sampleName/bin/$Configuration/netstandard2.1/Sevo.Example.$sampleName.dll") -Destination $samplePayload
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Example.$sampleName/sevo.mod.json") -Destination $samplePackage
    Compress-Archive -Path (Join-Path $samplePackage '*') -DestinationPath (Join-Path $sampleOutput "Example.$sampleName-0.1.0.zip")
}
Write-Output "Sample packages (not installed): $sampleOutput"
