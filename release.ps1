# Yeni surum yayinlar: sunucuda derler, zip + manifest gunceller, GitHub'a gonderir, sunucuya kurar.
# Kullanim:  .\release.ps1 -Version 1.2.0.0 -Changelog "Aciklama" -Password <ssh sifresi>
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Changelog,
    [Parameter(Mandatory)][string]$Password,
    [string]$Server = "sureyya@192.168.10.76",
    [string]$HostKey = "SHA256:rxfQIuLYU3j12/MivwQLmHsGEvs31VXziubNT5VoUus",
    [string]$RemoteRoot = "/home/sureyya/iptv",
    [string]$TargetAbi = "12.4.0.0",
    # Yalnizca GitHub'a yayinla; sunucu yeni surumu depodan kendisi alsin (Jellyfin otomatik guncelleme).
    [switch]$NoInstall
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$plink = "C:\Program Files\PuTTY\plink.exe"; $pscp = "C:\Program Files\PuTTY\pscp.exe"
$utf8 = New-Object Text.UTF8Encoding $false
function Remote([string]$cmd) { & $plink -batch -ssh -hostkey $HostKey -pw $Password $Server $cmd; if ($LASTEXITCODE -ne 0) { throw "Uzak komut basarisiz: $cmd" } }

# 1) Surum numarasi
$csproj = "$repo\Jellyfin.Plugin.XtreamStrm\Jellyfin.Plugin.XtreamStrm.csproj"
$c = [IO.File]::ReadAllText($csproj)
$c = [regex]::Replace($c, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version</AssemblyVersion>")
$c = [regex]::Replace($c, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$Version</FileVersion>")
[IO.File]::WriteAllText($csproj, $c, $utf8)

# 2) Sunucuda derle
Remote "rm -rf $RemoteRoot/Jellyfin.Plugin.XtreamStrm"
& $pscp -batch -q -r -hostkey $HostKey -pw $Password "$repo\Jellyfin.Plugin.XtreamStrm" "${Server}:$RemoteRoot/"
Remote "cd $RemoteRoot/Jellyfin.Plugin.XtreamStrm && sed -i 's/\r`$//' build.sh && sh build.sh 2>&1 | grep -E 'error|\.dll' ; test -f out/Jellyfin.Plugin.XtreamStrm.dll"

# 3) Zip + manifest
$tmp = Join-Path $env:TEMP "xtream-strm-$Version"; New-Item -ItemType Directory -Force $tmp | Out-Null
& $pscp -batch -q -hostkey $HostKey -pw $Password "${Server}:$RemoteRoot/Jellyfin.Plugin.XtreamStrm/out/Jellyfin.Plugin.XtreamStrm.dll" $tmp
$zip = "$repo\releases\xtream-strm_$Version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$tmp\Jellyfin.Plugin.XtreamStrm.dll" -DestinationPath $zip
$md5 = (Get-FileHash $zip -Algorithm MD5).Hash.ToLower()
$stamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

$manifestPath = "$repo\manifest.json"
$m = [IO.File]::ReadAllText($manifestPath)
if ($m.Contains("`"version`": `"$Version`"")) { throw "manifest.json icinde $Version zaten var" }
$entry = @"
    "versions": [
      {
        "version": "$Version",
        "changelog": "$($Changelog.Replace('"', "'"))",
        "targetAbi": "$TargetAbi",
        "sourceUrl": "https://raw.githubusercontent.com/sureyyasayin/xtream/main/releases/xtream-strm_$Version.zip",
        "checksum": "$md5",
        "timestamp": "$stamp"
      },
"@
$m = $m.Replace("    `"versions`": [", $entry.TrimEnd())
[IO.File]::WriteAllText($manifestPath, $m, $utf8)

# 4) GitHub (git uyarilari stderr'e yazar; PowerShell 5.1 bunlari hata saymasin)
$ErrorActionPreference = 'Continue'
Push-Location $repo
git add -A
git commit -q -m "$Version`: $Changelog"
$env:GCM_INTERACTIVE = 'always'
git push origin main
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "git push basarisiz" }
Pop-Location

if ($NoInstall) { "Yayinlandi (yalnizca GitHub): $Version (md5 $md5)"; return }

# 5) Sunucuya kur (derlenen DLL ile; zip icerigiyle aynidir) ve Jellyfin'i yeniden baslat
$meta = @"
{
  "category": "General",
  "changelog": "$($Changelog.Replace('"', "'"))",
  "description": "Xtream film, dizi ve canli TV kanallarini Jellyfin'e ekler.",
  "guid": "7b1f6c3e-52a4-4d0e-9a65-3c8e0f4d2b71",
  "name": "Xtream STRM",
  "overview": "Xtream film, dizi ve canli TV kanallarini Jellyfin'e ekler",
  "owner": "sureyyasayin",
  "targetAbi": "$TargetAbi",
  "timestamp": "$stamp",
  "version": "$Version",
  "status": "Active",
  "autoUpdate": true,
  "assemblies": []
}
"@
$metaFile = "$tmp\meta.json"; [IO.File]::WriteAllText($metaFile, $meta.Replace("`r", ""), $utf8)
& $pscp -batch -q -hostkey $HostKey -pw $Password $metaFile "${Server}:$RemoteRoot/Jellyfin.Plugin.XtreamStrm/out/meta.json"
$pd = "$RemoteRoot/config/jellyfin/data/plugins"
Remote "docker stop jellyfin >/dev/null && rm -rf $pd/Xtream\ STRM_* && mkdir -p '$pd/Xtream STRM_$Version' && cp $RemoteRoot/Jellyfin.Plugin.XtreamStrm/out/Jellyfin.Plugin.XtreamStrm.dll $RemoteRoot/Jellyfin.Plugin.XtreamStrm/out/meta.json '$pd/Xtream STRM_$Version/' && docker start jellyfin >/dev/null && sleep 25 && docker logs --since 1m jellyfin 2>&1 | grep -E 'Loaded plugin: Xtream|FTL|Startup complete' | cut -c1-200"
"Yayinlandi: $Version (md5 $md5)"
