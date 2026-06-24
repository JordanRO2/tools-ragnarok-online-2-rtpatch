<#
.SYNOPSIS
  Build the server www tree the repointed RO2 updater + launcher fetch from.

.DESCRIPTION
  Lays out, under -WwwRoot:
    Patch/Global/Launcher/MD5/MD5List.bin              (0 bytes)  ┐ stage-1: updater self-check.
    Patch/Global/Launcher/MD5/MD5FileList.txt          (0 bytes)  │ empty manifests => the updater
    Patch/Global/Launcher/MD5/<Lang>/MD5List.bin       (0 bytes)  │ downloads nothing and proceeds
    Patch/Global/Launcher/MD5/<Lang>/MD5FileList.txt   (0 bytes)  ┘ to spawn the launcher.
    Patch/Global/Client/Patch/ServerVersion.ini                   stage-2: advertised client version.
    Patch/Global/Client/Patch/<RtpVersion>.RTP         (optional)  the incremental patch to apply.

  For the launcher to actually patch (decision "case 4"), BOTH the advertised game and
  patch versions must EXCEED the client's local Version.dat (RO2 keeps game==patch==build).
  The .RTP is named after the version it bumps the client TO (e.g. 303 -> 304 => 00000304.RTP).
  Build a .RTP with the parent tool:  dotnet run -- build --add <rel>=<file> -o 00000304.RTP

.PARAMETER WwwRoot     server document root (created if missing).
.PARAMETER GameVersion advertised ServerGameVersion  (default 304).
.PARAMETER PatchVersion advertised ServerPatchVersion (default 304).
.PARAMETER RtpPath     optional path to a pre-built .RTP to publish.
.PARAMETER RtpVersion  the .RTP sequence number / target version (default 304) -> 00000304.RTP.
.PARAMETER Lang        NATION_CODE LANGUAGE used by the client (default 1); the versioned MD5 subdir.

.EXAMPLE
  .\make-www.ps1 -WwwRoot 'D:\ro2-www' -RtpPath '.\00000304.RTP' -GameVersion 304 -PatchVersion 304 -RtpVersion 304
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $WwwRoot,
  [int] $GameVersion = 304,
  [int] $PatchVersion = 304,
  [string] $RtpPath,
  [int] $RtpVersion = 304,
  [int] $Lang = 1
)
$ErrorActionPreference = 'Stop'
$iso = [System.Text.Encoding]::GetEncoding(28591)

$md5  = Join-Path $WwwRoot 'Patch\Global\Launcher\MD5'
$md5L = Join-Path $md5 $Lang
$cli  = Join-Path $WwwRoot 'Patch\Global\Client\Patch'
$null = New-Item -ItemType Directory -Force $md5, $md5L, $cli

# stage-1: four byte-empty manifests, served 200 + Content-Length: 0
foreach ($p in @(
    (Join-Path $md5  'MD5List.bin'),     (Join-Path $md5  'MD5FileList.txt'),
    (Join-Path $md5L 'MD5List.bin'),     (Join-Path $md5L 'MD5FileList.txt'))) {
  [System.IO.File]::WriteAllBytes($p, (New-Object byte[] 0))
}

# stage-2: advertised version (must exceed the client's local Version.dat to trigger a download)
$svi = Join-Path $cli 'ServerVersion.ini'
[System.IO.File]::WriteAllText($svi,
  ("ServerGameVersion={0:D8}`r`nServerPatchVersion={1:D8}`r`n" -f $GameVersion, $PatchVersion), $iso)

# the incremental .RTP (filename = the version it bumps the client TO)
if ($RtpPath) {
  if (-not (Test-Path $RtpPath)) { throw "RtpPath not found: $RtpPath" }
  $dst = Join-Path $cli ("{0:D8}.RTP" -f $RtpVersion)
  Copy-Item (Resolve-Path $RtpPath).Path $dst -Force
  Write-Host "  rtp : $RtpPath -> $dst"
} else {
  Write-Warning "no -RtpPath: ServerVersion.ini advertises $PatchVersion but no 00000$($RtpVersion).RTP is published; the launcher will 404 the download."
}

# launcher news web views: big news at "/" (407x274) + top banner at /notice/indexad.html (407x95)
$notice  = Join-Path $WwwRoot 'notice'
$null    = New-Item -ItemType Directory -Force $notice
$newsDir = Join-Path $PSScriptRoot 'news'
if (Test-Path (Join-Path $newsDir 'index.html'))   { Copy-Item (Join-Path $newsDir 'index.html')   (Join-Path $WwwRoot 'index.html')  -Force }
if (Test-Path (Join-Path $newsDir 'indexad.html')) { Copy-Item (Join-Path $newsDir 'indexad.html') (Join-Path $notice  'indexad.html') -Force }
Write-Host "  news: / (407x274) + /notice/indexad.html (407x95)"

Write-Host "www ready: $WwwRoot   (server game/patch = $GameVersion/$PatchVersion, lang = $Lang)"
Write-Host "start it:  python `"$PSScriptRoot\patchsrv.py`" `"$WwwRoot`" 8080"
