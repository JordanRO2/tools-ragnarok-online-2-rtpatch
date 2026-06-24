# RO2 private-server harness

Reproducible recipe to drive the **entire stock RO2 update/patch chain from your own
server**. A *fresh* b303 client + these scripts = the updater and launcher fetch and
apply your own patches from `127.0.0.1`, with no Gravity/WarpPortal servers involved.

The point of this folder: the modified client is **throwaway** — everything here
regenerates it from a clean copy. Full reverse engineering lives in the wiki:
[`launcher/update-patch-architecture.md`](https://github.com/JordanRO2/wiki-ragnarok-online-2/blob/main/launcher/update-patch-architecture.md)
(both stages + the version protocol) and
[`launcher/renaming-updater-launcher-exes.md`](https://github.com/JordanRO2/wiki-ragnarok-online-2/blob/main/launcher/renaming-updater-launcher-exes.md)
(the byte-patch reference).

## The chain (proven end-to-end)

```
RO2Updater.exe ──4× GET /Patch/Global/Launcher/MD5/...──► (self-check, no-op)
   └─spawns─► RO2Launcher.exe ──GET /Patch/Global/Client/Patch/ServerVersion.ini──►
                              ──GET /Patch/Global/Client/Patch/00000NNN.RTP──► apply (expapply.dll)
                              ──spawns─► Shipping\Rag2.exe          Version.dat 303→304
```

`RO2Updater.exe` = the stock `RO2Client.exe` (the updater); `RO2Launcher.exe` = the stock
`Launcher2.exe` (the launcher/client patcher). Both read the patch host from one line of
`Launcher\String.tbl`.

## Files

| File | What it does |
|---|---|
| `apply-client-mods.ps1` | renames the two exes, byte-patches the updater's spawn target + self-refs, repoints `String.tbl` line 27, writes `RO2_option.ini`. `-NoUac` flips the updater manifest to `asInvoker`. |
| `make-www.ps1` | builds the server document root: the four empty stage-1 `MD5` manifests + `ServerVersion.ini` + your `.RTP`. |
| `patchsrv.py` | minimal **HTTP/1.1** static server (HTTP/1.1 is required, see below). |

## Reproduce from a fresh client

> Requires: a clean b303 client copy (with `RO2Client.exe` + `Launcher2.exe`),
> Python 3, and the parent `dotnet` tool for building `.RTP`s.

```powershell
# 0) take a FRESH copy of the client (never reuse a mutated one)
Copy-Item -Recurse 'D:\RO2-clean' 'D:\RO2-test'

# 1) turn it into a private-server client (renames + patches + host + language gate)
.\apply-client-mods.ps1 -ClientRoot 'D:\RO2-test' -PatchHost '127.0.0.1:8080'
#    add -NoUac to run the updater without elevation (HKLM writes then no-op)

# 2) build an incremental .RTP with the parent tool (here: add a marker file).
#    The filename must be the version it bumps the client TO (303 -> 304 = 00000304.RTP).
cd ..                     # repo root (Ro2.RtPatch.csproj)
"hello from our server" | Set-Content marker.txt
dotnet run -- build --add 'Data\PATCHED_BY_OUR_SERVER.TXT=marker.txt' -o '.\00000304.RTP'
cd private-server

# 3) build the server www tree (advertise 304; publish the .RTP)
.\make-www.ps1 -WwwRoot 'D:\ro2-www' -RtpPath '..\00000304.RTP' `
    -GameVersion 304 -PatchVersion 304 -RtpVersion 304

# 4) (test only) set the client BELOW the advertised version so it actually patches
$iso=[Text.Encoding]::GetEncoding(28591)
[IO.File]::WriteAllText('D:\RO2-test\Version.dat',"UserGameVersion=00000303;`r`nUserPatchVersion=00000303; ",$iso)

# 5) start the server
python .\patchsrv.py 'D:\ro2-www' 8080

# 6) in another shell, run the updater from the client root (as admin unless you used -NoUac)
Start-Process 'D:\RO2-test\RO2Updater.exe' -Verb RunAs
```

Watch `patchsrv.log`: four `MD5` `200`s → the launcher's `ServerVersion.ini` → `00000304.RTP`,
and `D:\RO2-test\Version.dat` flips to `00000304`. (`Shipping\Rag2.exe` is the real game; in a
bare test tree it just won't be found after the patch — harmless.)

## Rules that bite (from the RE)

- **HTTP/1.1 is mandatory.** Over HTTP/1.0 the launcher's body read returns empty and it
  decides "already up to date". `patchsrv.py` already sets `HTTP/1.1`.
- **Versions must ADVANCE the game version.** The launcher only downloads when
  `localGame < serverGame && localPatch < serverPatch` (RO2 keeps game==patch==build). Advertising
  only a higher *patch* version is a silent no-op. So bump both (`-GameVersion`/`-PatchVersion`).
- **All four stage-1 `MD5` files must be `200` + `Content-Length: 0`.** A 404 (or a missing
  Content-Length) sends the updater into a WinINET range-retry and it never spawns the launcher.
- **`RO2_option.ini` needs a non-zero `[NATION_CODE] LANGUAGE`** or the updater aborts with
  "Need Language Pack, Please Restart". `apply-client-mods.ps1` writes `LANGUAGE=1`; serve the
  versioned manifests under `MD5/1/` to match.
- **The launcher must be spawnable under the name the updater expects.** The stock updater
  appends the literal `\Launcher2.exe`; we patch that to `\RO2Launcher.exe` so the renamed pair
  is self-consistent (`apply-client-mods.ps1` does this — patch `spawn-target`).

## `.RTP` building

`.RTP` files are made by the parent tool (`dotnet run -- build ...`, store-mode whole-file
patches the official `expapply.dll` accepts). See the repo root `README.md`.
