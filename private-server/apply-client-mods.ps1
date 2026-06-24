<#
.SYNOPSIS
  Turn a fresh, clean RO2 client into a private-server client: rename the two
  bootstrap exes to their true roles, repoint the patch host, and add the
  language gate — fully reproducible, idempotent, asserts old bytes before writing.

.DESCRIPTION
  Applies, on a FRESH copy of the b303 client:
    1. RO2Client.exe -> RO2Updater.exe   (the Updater)
    2. Launcher2.exe -> RO2Launcher.exe  (the Launcher / client patcher)
    3. byte-patches inside RO2Updater.exe:
         0x167D01  "Launcher2.exe" -> "RO2Launcher.exe"  (the sole launcher-spawn target; REQUIRED)
         0x1274F0  "RO2Client.exe" -> "RO2Updater.exe"   (self-update .exe name)
         0x1276F7  "RO2Client.exe" -> "RO2Updater.exe"   (registry self-path)
         0x14EEBE  "RO2Client.pdb" -> "RO2Updater.pdb"   (debug pdb; cosmetic)
    4. Launcher\String.tbl line 27 (field 26) -> the patch host
    5. RO2_option.ini  [NATION_CODE] LANGUAGE=1   (else the updater aborts "Need Language Pack")
    6. (-NoUac) flip the updater's manifest requireAdministrator -> asInvoker (runs without UAC)

  Offsets are FILE offsets, verified byte-for-byte against the pristine 2022 client
  (RO2Client.exe 1,559,880 B / Launcher2.exe 2,646,528 B). The launcher InternalName
  version-resource (utf-16 @0x1BFEEC) is intentionally left stale (shift-unsafe, cosmetic).
  Full RE: wiki launcher/renaming-updater-launcher-exes.md + launcher/update-patch-architecture.md.

.PARAMETER ClientRoot
  Path to the client folder (contains RO2Client.exe + Launcher2.exe, or the renamed pair).

.PARAMETER PatchHost
  host[:port] written to String.tbl line 27 (default 127.0.0.1:8080).

.PARAMETER NoUac
  Also patch the updater manifest to asInvoker so it runs without elevation
  (the HKLM version writes then silently no-op; the Version.dat file stays authoritative).

.EXAMPLE
  .\apply-client-mods.ps1 -ClientRoot 'D:\RO2-fresh' -PatchHost '127.0.0.1:8080'
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $ClientRoot,
  [string] $PatchHost = '127.0.0.1:8080',
  [switch] $NoUac
)
$ErrorActionPreference = 'Stop'
$iso = [System.Text.Encoding]::GetEncoding(28591)   # ISO-8859-1 (byte-exact)
function AB([string]$s){ [System.Text.Encoding]::ASCII.GetBytes($s) }

if (-not (Test-Path $ClientRoot)) { throw "ClientRoot not found: $ClientRoot" }
$ClientRoot = (Resolve-Path $ClientRoot).Path

function Rename-If([string]$from,[string]$to){
  $s = Join-Path $ClientRoot $from; $d = Join-Path $ClientRoot $to
  if (Test-Path $d) { Write-Host "  [skip] $from -> $to (target exists)"; return }
  if (-not (Test-Path $s)) { throw "missing $from in $ClientRoot" }
  Rename-Item $s $to; Write-Host "  [ok]   $from -> $to"
}

# Patch a file region in place. $Expect/$Write must be the SAME length (include any
# trailing NUL bytes you intend to consume in BOTH). Idempotent + asserts old bytes.
function Patch-Bytes([string]$Path,[int]$Off,[byte[]]$Expect,[byte[]]$Write,[string]$Label){
  if ($Expect.Length -ne $Write.Length) { throw "$Label: Expect/Write length mismatch" }
  $fs = [System.IO.File]::Open($Path,'Open','ReadWrite')
  try {
    $fs.Position = $Off
    $buf = New-Object byte[] $Write.Length; [void]$fs.Read($buf,0,$buf.Length)
    if ([System.Linq.Enumerable]::SequenceEqual([byte[]]$buf,[byte[]]$Write)) {
      Write-Host ("  [skip] {0} @0x{1:X}: already patched" -f $Label,$Off); return }
    if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]]$buf,[byte[]]$Expect)) {
      throw ("{0} @0x{1:X}: old bytes mismatch (got {2})" -f $Label,$Off,[BitConverter]::ToString($buf)) }
    $fs.Position = $Off; $fs.Write($Write,0,$Write.Length)
    Write-Host ("  [ok]   {0} @0x{1:X}" -f $Label,$Off)
  } finally { $fs.Dispose() }
}

Write-Host "== renames (updater first, frees the RO2Client.exe name) =="
Rename-If 'RO2Client.exe' 'RO2Updater.exe'
Rename-If 'Launcher2.exe' 'RO2Launcher.exe'

$upd = Join-Path $ClientRoot 'RO2Updater.exe'
Write-Host "== byte-patches (RO2Updater.exe) =="
# 0x167D01: "Launcher2.exe"(13) + 2 NUL -> "RO2Launcher.exe"(15)   [CRITICAL spawn repoint]
Patch-Bytes $upd 0x167D01 ((AB 'Launcher2.exe') + [byte[]]@(0,0)) (AB 'RO2Launcher.exe') 'spawn-target'
# 0x1274F0 / 0x1276F7: "RO2Client.exe"(13) + 1 NUL -> "RO2Updater.exe"(14)   [self refs]
Patch-Bytes $upd 0x1274F0 ((AB 'RO2Client.exe') + [byte[]]@(0)) (AB 'RO2Updater.exe') 'self-update-name'
Patch-Bytes $upd 0x1276F7 ((AB 'RO2Client.exe') + [byte[]]@(0)) (AB 'RO2Updater.exe') 'self-path'
# 0x14EEBE: "RO2Client.pdb"(13) + 1 NUL -> "RO2Updater.pdb"(14)   [cosmetic]
Patch-Bytes $upd 0x14EEBE ((AB 'RO2Client.pdb') + [byte[]]@(0)) (AB 'RO2Updater.pdb') 'pdb-name'

Write-Host "== String.tbl host repoint (line 27 / field 26) =="
$tbl = Join-Path $ClientRoot 'Launcher\String.tbl'
if (Test-Path $tbl) {
  $text = [System.IO.File]::ReadAllText($tbl,$iso)
  $parts = $text -split "`n"
  if ($parts.Count -le 26) { Write-Warning "String.tbl has < 27 lines; not repointed" }
  else {
    $cur = $parts[26]; $cr = if ($cur.EndsWith("`r")) {"`r"} else {""}
    $oldHost = $cur.TrimEnd("`r")
    if ($oldHost -eq $PatchHost) { Write-Host "  [skip] line 27 already '$PatchHost'" }
    else {
      $parts[26] = $PatchHost + $cr
      [System.IO.File]::WriteAllText($tbl, ($parts -join "`n"), $iso)   # byte-precise: only line 27 changes
      Write-Host "  [ok]   line 27 host: '$oldHost' -> '$PatchHost'"
    }
  }
} else { Write-Warning "String.tbl not found at $tbl" }

Write-Host "== RO2_option.ini (LANGUAGE gate) =="
$ini = Join-Path $ClientRoot 'RO2_option.ini'
[System.IO.File]::WriteAllText($ini, "[NATION_CODE]`r`nLANGUAGE=1`r`n", $iso)
Write-Host "  [ok]   $ini  ([NATION_CODE] LANGUAGE=1)"

if ($NoUac) {
  Write-Host "== manifest: requireAdministrator -> asInvoker (no UAC) =="
  $s = $iso.GetString([System.IO.File]::ReadAllBytes($upd))
  if ($s.Contains('asInvoker"')) { Write-Host "  [skip] manifest already asInvoker" }
  elseif ($s.Contains('requireAdministrator"')) {
    # 'requireAdministrator"'(21) -> 'asInvoker"'(10) + 11 spaces (valid XML whitespace) = 21 bytes; length-preserving
    $s2 = $s.Replace('requireAdministrator"', 'asInvoker"           ')
    [System.IO.File]::WriteAllBytes($upd, $iso.GetBytes($s2))
    Write-Host "  [ok]   manifest -> asInvoker (runs without elevation; HKLM writes no-op)"
  } else { Write-Warning "manifest 'requireAdministrator\"' not found; -NoUac skipped" }
}

Write-Host "`nDone. Client is private-server-ready:"
Write-Host "  $ClientRoot\RO2Updater.exe  (run it; spawns RO2Launcher.exe)"
Write-Host "  host = $PatchHost  (String.tbl line 27)"
if (-not $NoUac) { Write-Host "  (runs elevated / UAC; pass -NoUac to flip the manifest to asInvoker)" }
