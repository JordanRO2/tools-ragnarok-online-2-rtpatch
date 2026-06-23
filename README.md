# rtpatch

A cross-platform C# / .NET 10 tool for **Ragnarok Online 2**'s `.RTP` patch files —
inspect them, list their contents, and **apply** them to reconstruct the patched game files.

`.RTP` files are produced by Pocket Soft **RTPatch** (the "ExaPatch" applier shipped as
`expapply.dll` with the RO2 launcher). They are binary delta patches: each one turns an old
fileset into a new one. This tool is a clean reimplementation of the **applier/decoder**,
verified to produce byte-identical output to the original `expapply.dll`.

## Status

The decoder is **byte-exact**. Validated by applying nine consecutive official patches
(v202–v210) to a full v201 client and comparing every output file's SHA-256 against the
output of the original `expapply.dll`:

```
30 / 30 file-decodes MATCH
```

That covers VDK archives from 14 KB to ~924 MB and MP3 audio, and exercises every codec
path: store / preset-Huffman / adaptive-Huffman (CLC-coded and by-length tree descriptions) /
control blocks, source-copy and LZ-from-output, the byte/word/dword additive strided runs,
the recent-offset rings, and degenerate trees.

> The patch **builder** is not included — it is closed and runs server-side. This tool only
> reads and applies existing patches.

## Build

```sh
dotnet build -c Release        # needs the .NET 10 SDK
```

The output binary is named `rtpatch`.

## Usage

```sh
rtpatch inspect <file.rtp>
    Print the container header (version, format tag, file counts) and a record summary.

rtpatch list <file.rtp>
    List every record: type, destination size, payload length, target path.

rtpatch apply <file.rtp> --source <dir> --output <dir> [--file <substr>]
    Apply the patch. For each MODIFY record it reads the old file from <dir>, decodes the
    delta, and writes the new file under <output>/ at the same relative path. --file limits
    to records whose path contains the given substring.
```

Example — apply patch 202 over a v201 client into a fresh tree:

```sh
rtpatch apply 00000202.RTP --source ./client --output ./client-v202
```

## Format

RO2 ships RTPatch container **version 0x0101** (magic `KX`). The container layout (header,
directory table, per-file records, the trailer/payload framing) is parsed by
[`Rtp/Container.cs`](Rtp/Container.cs); the compressed delta codec (Pocket Soft "DFC":
adaptive Huffman + LZ + source-referential delta ops) is implemented in
[`Rtp/Codec.cs`](Rtp/Codec.cs).

## Layout

| File | Purpose |
|---|---|
| `Program.cs` | CLI (`inspect` / `list` / `apply`) |
| `Rtp/Container.cs` | `.RTP` v1.01 container parser |
| `Rtp/Codec.cs` | the delta decoder (the validated `DecodeRecord`) |
| `Rtp/Opcodes.cs` | earlier exploratory opcode model (superseded by the decoder in `Codec.cs`) |
