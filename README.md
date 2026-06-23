# RO2 RTPatch Tool

A C# / **.NET 10** tool for **Ragnarok Online 2**'s `.RTP` patch files: inspect
them, list their contents, and **apply** them to reconstruct the patched game
files. Cross-platform command-line tool.

`.RTP` files are produced by Pocket Soft **RTPatch** (the "ExaPatch" applier
shipped as `expapply.dll` with the RO2 launcher). They are binary delta patches:
each one turns an old fileset into the next version. This tool is a clean
reimplementation of the **applier/decoder**, verified to produce byte-identical
output to the original `expapply.dll`.

## Features

- **Apply — byte-for-byte 1:1.** For each record the patch carries, the old file
  is read from a source tree, the delta is decoded, and the new file is written
  out. The result is **byte-identical** to what the original `expapply.dll`
  produces (verified by SHA256, see [Validation](#validation)).
- **Inspect / list** the container without applying it: header (version, format
  tag, file counts) and a per-record summary (type, destination size, payload
  length, target path).
- **Self-contained decoder.** The full Pocket Soft "DFC" codec (adaptive Huffman
  + LZ + source-referential delta ops) is reimplemented in managed C# with no
  dependency on the proprietary DLL.

> The patch **builder** is not included — it is closed and runs server-side. This
> tool only reads and applies existing patches.

## Validation

The decoder is **byte-exact**. Validated by applying nine consecutive official
patches (v202–v210) to a full v201 client and comparing every output file's
SHA-256 against the output of the original `expapply.dll`:

```
30 / 30 file-decodes MATCH
```

That covers VDK archives from 14 KB to ~924 MB and MP3 audio, and exercises every
codec path: store / preset-Huffman / adaptive-Huffman (CLC-coded and by-length
tree descriptions) / control blocks, source-copy and LZ-from-output, the
byte/word/dword additive strided runs, the recent-offset rings, and degenerate
trees.

## Building

The repository is a single .NET 10 project (`Ro2.RtPatch.csproj`):

```sh
dotnet build -c Release        # needs the .NET 10 SDK
```

The output binary is named `rtpatch`.

## Command-line interface

| Command   | Arguments                                              | Description                                          |
|-----------|--------------------------------------------------------|------------------------------------------------------|
| `inspect` | `<file.rtp>`                                            | Print the container header and a record summary      |
| `list`    | `<file.rtp>`                                            | List every record: type, destination size, payload length, target path |
| `apply`   | `<file.rtp> --source <dir> --output <dir> [--file <substr>]` | Apply the patch: decode each record from `<source>` and write the new file under `<output>` at the same relative path |

The `--file <substr>` flag on `apply` limits work to records whose target path
contains the given substring.

### Examples

```sh
# Inspect / list a patch
rtpatch inspect 00000202.RTP
rtpatch list    00000202.RTP

# Apply patch 202 over a v201 client into a fresh tree
rtpatch apply 00000202.RTP --source ./client --output ./client-v202

# Apply only the records touching a single archive
rtpatch apply 00000202.RTP --source ./client --output ./out --file ITEM.VDK
```

## Supported format

### RTPatch container (version 0x0101)

- Magic `KX`, container version `0x0101` (the build RO2 ships)
- Directory table + per-file records (add / modify), self-terminating payloads
- Parsed by [`Rtp/Container.cs`](Rtp/Container.cs)

### Pocket Soft DFC delta codec

- MSB-first bit stream; canonical Huffman trees for literal/length/distance
- Store / preset-tree / adaptive-tree (CLC-coded and by-length) block modes
- Source-copy, LZ-from-output, and byte/word/dword additive strided runs
- Recent-offset rings and degenerate single-symbol trees
- Implemented byte-exact in [`Rtp/Codec.cs`](Rtp/Codec.cs)

## License

Released under the [MIT License](LICENSE).

## Legal Disclaimer

This project is an unofficial, open-source personal tool developed solely for
educational and research purposes. It is completely non-profit and is not intended
to infringe upon any copyrights. Ragnarok Online 2, along with all related assets,
data formats, and intellectual property, are the sole property of Gravity Co., Ltd.
and WarpPortal. RTPatch is a product of Pocket Soft, Inc. This tool is provided
"as is" without any warranty, and the author assumes no liability for its use.
