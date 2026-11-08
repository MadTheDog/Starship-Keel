param([string]$SourceGame = 'D:\SteamLibrary\steamapps\common\Starship EVO', [switch]$VerifyOnly)
. (Join-Path $PSScriptRoot 'Environment.ps1')
$game = Assert-PlatformChild (Join-Path $PlatformRoot 'lab/game')
$profile = Assert-PlatformChild (Join-Path $PlatformRoot 'lab/profile')
$evidence = Assert-PlatformChild (Join-Path $PlatformRoot 'local-evidence/lab-identity.json')
$company = 'SEVO Keel Hooks Lab016'; $product = 'SEVO HK Test'
$junction = Join-Path ([Environment]::GetFolderPath('UserProfile')) ('AppData/LocalLow/' + $company)

function Get-SerializedLayout([byte[]]$Bytes) {
    $stream = [IO.MemoryStream]::new($Bytes, $false); $reader = [IO.BinaryReader]::new($stream)
    function Read-BigEndian([int]$Length) {
        $buffer = $reader.ReadBytes($Length); [Array]::Reverse($buffer)
        if ($Length -eq 4) { return [BitConverter]::ToUInt32($buffer,0) }
        return [BitConverter]::ToInt64($buffer,0)
    }
    try {
        $metadata = Read-BigEndian 4; $size = Read-BigEndian 4; $version = Read-BigEndian 4; $offset = Read-BigEndian 4
        if ($version -notin @(21,22,23) -or $Bytes[16] -ne 0) { throw 'Unsupported serialized settings layout; do not guess an identity patch.' }
        $stream.Position = 20
        if ($version -ge 22) { $metadata=Read-BigEndian 4; $size=Read-BigEndian 8; $offset=Read-BigEndian 8; $null=Read-BigEndian 8 }
        if ($size -ne $Bytes.Length -or $offset -ge $size) { throw 'Invalid serialized settings header' }
        $unityBytes = [Collections.Generic.List[byte]]::new()
        while (($b=$reader.ReadByte()) -ne 0) { $unityBytes.Add($b); if($unityBytes.Count -gt 128){throw 'Invalid engine version field'} }
        $unity = [Text.Encoding]::ASCII.GetString($unityBytes.ToArray()); $null=$reader.ReadInt32()
        if($reader.ReadBoolean()){throw 'This narrow lab tool requires stripped type trees'}
        $typeCount=$reader.ReadInt32(); if($typeCount -lt 1 -or $typeCount -gt 4096){throw 'Invalid serialized type count'}
        $types=@(); for($i=0;$i -lt $typeCount;$i++){
            $class=$reader.ReadInt32(); $types+=$class; $null=$reader.ReadBoolean(); $null=$reader.ReadInt16()
            if($class -eq 114){$stream.Position+=16}; $stream.Position+=16
        }
        $objectCount=$reader.ReadInt32(); if($objectCount -lt 1 -or $objectCount -gt 1000000){throw 'Invalid serialized object count'}
        $players=@()
        for($i=0;$i -lt $objectCount;$i++){
            $stream.Position=($stream.Position+3) -band (-bnot 3)
            $id=$reader.ReadInt64(); $start=if($version -ge 22){$reader.ReadInt64()}else{$reader.ReadUInt32()}; $start+=$offset
            $length=$reader.ReadUInt32();$type=$reader.ReadInt32()
            if($type -lt 0 -or $type -ge $types.Count -or $start -lt $offset -or $start+$length -gt $size){throw 'Invalid serialized object range'}
            if($types[$type] -eq 129){$players+=@{Start=$start;Length=$length}}
        }
        if($players.Count -ne 1 -or $stream.Position -gt $offset){throw 'Expected exactly one structured PlayerSettings object'}
        return @{Unity=$unity;Version=$version;Player=$players[0];Size=$size;Offset=$offset}
    } finally {$reader.Dispose();$stream.Dispose()}
}
function Find-Unique([byte[]]$Bytes,[byte[]]$Pattern){
    $found=-1
    for($i=0;$i -le $Bytes.Length-$Pattern.Length;$i++){
        if($Bytes[$i] -ne $Pattern[0]){continue};$equal=$true
        for($j=1;$j -lt $Pattern.Length;$j++){if($Bytes[$i+$j] -ne $Pattern[$j]){$equal=$false;break}}
        if($equal){if($found -ge 0){throw 'Identity field is not unique'};$found=$i}
    }
    if($found -lt 0){throw 'Identity field not found'};return $found
}

if (!$VerifyOnly) {
    if(Test-Path -LiteralPath $game){throw 'Lab game already exists; preserve it and use -VerifyOnly.'}
    $SourceGame=[IO.Path]::GetFullPath($SourceGame)
    if($SourceGame.StartsWith($PlatformRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Source must be the explicitly selected read-only game, not this lab'}
    New-Item -ItemType Directory -Path $game,$profile | Out-Null
    & robocopy $SourceGame $game /E /COPY:DAT /R:0 /W:0 /XD BepInEx 'Starship EVO_BackUpThisFolder_ButDontShipItWithYourGame' /XF winhttp.dll doorstop_config.ini .doorstop_version changelog.txt /NFL /NDL /NJH /NJS /NP
    if($LASTEXITCODE -gt 7){throw 'Read-only source copy failed'}
    $sourceSettings=Join-Path $SourceGame 'Starship EVO_Data/globalgamemanagers'
    $bytes=[IO.File]::ReadAllBytes($sourceSettings);$originalHash=(Get-FileHash -LiteralPath $sourceSettings).Hash
    $layout=Get-SerializedLayout $bytes; $patched=$bytes.Clone()
    foreach($pair in @(@('Moonfire Entertainment',$company),@('Starship EVO',$product))){
        $old=[Text.Encoding]::UTF8.GetBytes($pair[0]);$new=[Text.Encoding]::UTF8.GetBytes($pair[1])
        if($old.Length -ne $new.Length){throw 'Identity replacements must preserve byte length'}
        $pattern=[BitConverter]::GetBytes([int]$old.Length)+$old;$start=(Find-Unique $bytes $pattern)+4
        if($start -lt $layout.Player.Start -or $start+$old.Length -gt $layout.Player.Start+$layout.Player.Length){throw 'Identity field is outside structured PlayerSettings'}
        [Array]::Copy($new,0,$patched,$start,$new.Length)
    }
    $after=Get-SerializedLayout $patched
    if($after.Size -ne $layout.Size -or $after.Unity -ne $layout.Unity){throw 'Patched settings structure changed'}
    $targetSettings=Assert-PlatformChild (Join-Path $game 'Starship EVO_Data/globalgamemanagers')
    [IO.File]::WriteAllBytes($targetSettings,$patched)
    [IO.File]::WriteAllText((Join-Path $game 'Starship EVO_Data/app.info'),$company+"`n"+$product,[Text.UTF8Encoding]::new($false))
    if((Get-FileHash -LiteralPath $sourceSettings).Hash -ne $originalHash){throw 'Original settings unexpectedly changed'}
    @{company=$company;product=$product;unity=$layout.Unity;source=$SourceGame;originalSha256=$originalHash;labSha256=(Get-FileHash -LiteralPath $targetSettings).Hash;game=$game;profile=$profile;junction=$junction;prepared=$true} | ConvertTo-Json | Set-Content -LiteralPath $evidence
}
$identity=Get-Content -LiteralPath $evidence -Raw | ConvertFrom-Json
if($identity.company -ne $company -or $identity.product -ne $product -or $identity.prepared -ne $true){throw 'Lab identity record mismatch'}
if((Get-FileHash -LiteralPath (Join-Path $game 'Starship EVO_Data/globalgamemanagers')).Hash -ne $identity.labSha256){throw 'Lab settings checksum changed'}
$appInfo=Assert-PlatformChild (Join-Path $game 'Starship EVO_Data/app.info')
$expectedAppInfo=[Text.Encoding]::UTF8.GetBytes($company+"`n"+$product)
if(!(Test-Path -LiteralPath $appInfo -PathType Leaf)){throw 'Lab app.info is missing. Do not launch the lab.'}
if(![Linq.Enumerable]::SequenceEqual([byte[]][IO.File]::ReadAllBytes($appInfo),[byte[]]$expectedAppInfo)){throw 'Lab app.info company/product identity mismatch. Do not launch the lab.'}
$item=Get-Item -LiteralPath $junction -Force -ErrorAction SilentlyContinue
$linked=$item -and $item.LinkType -eq 'Junction' -and @($item.Target).Count -eq 1 -and [IO.Path]::GetFullPath([string]$item.Target) -eq $profile
[pscustomobject]@{Game=$game;Profile=$profile;Company=$company;Product=$product;Unity=$identity.unity;Junction=$junction;ProfileJunctionVerified=[bool]$linked;ReadyForRuntime=[bool]$linked} | ConvertTo-Json
if($VerifyOnly -and !$linked){throw 'A dedicated LocalLow profile junction has not been verified. Do not launch the lab.'}
