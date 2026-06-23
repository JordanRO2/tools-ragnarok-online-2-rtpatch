// RTPatch v1.01 ("KX") container parser for RO2 patch files (*.RTP).
//
// OUR format is RTPatch file version 0x0101, magic "KX" (0x4B 0x58), produced by
// expapply.dll (Pocket Soft RTPatch 7.0.2.0). It is the same family as — but an
// OLDER version than — rtptool's v2.09 ("K*" = 0x4B 0x2A). rtptool's SPEC.md was
// used as a STRUCTURAL reference, but every byte offset below was confirmed by
// reverse-engineering the real v303 patch-server samples
// (clients/ClientArchive/_patch-server-mirror-v303/patches/*.RTP — 104 files,
// all parse cleanly).
//
// ── Confirmed v1.01 layout ──────────────────────────────────────────────────
//
// Fixed header (all little-endian unless noted):
//   0x00  u8[2]  magic        "KX"
//   0x02  u16    version      0x0101
//   0x04  u32    const        0x01000000
//   0x08  u32    formatTag    0x9F (builds <= 264) or 0x29F (builds >= 265)
//   0x0C  u32    const        0x00000030
//   0x10  u64    oldFilesetBytes
//   0x18  u64    newFilesetBytes
//   0x20  u64    bytesGrown
//   0x28  u64    cursor
//   0x30  u64    oldCount
//   0x38  u64    newCount
//   0x40  u64    maxCount
//   0x48  u64    reserved (0)
//
// Directory table @ 0x50:
//   0x50  u32    dictBodyLen
//   0x54  u32    0
//   0x58  u8     dirCount
//          then dirCount x { u8 len; u8 len (duplicate); u8 name[len] }   (ANSI, no NUL)
//
// Records: zero or more targets, each preceded by a 6-byte record separator
//   { u8 0x71; u8 var; u8 0xB0; u8 0x01; u16 0 }   (the 2nd byte varies per record),
// then the EOF terminator u16 0x0000 at end of file. A 256-byte file is a no-op
// (header + dictionary, no targets, terminator immediately after the dictionary).
//
// Per target:
//   path lp     { u8 len; u8 len (dup); u8 fullPath[len] }   (e.g. "Data\UI.VDK")
//   u8          dirIndex (into the directory table)
//   marker      ends in 0x01, immediately before the first entry descriptor:
//                 0x09 0x00 0x01  -> MODIFY (delta): OLD entry + NEW entry
//                 0x00 0x01 / 0x01 -> NEW (whole file): NEW entry only
//   entry(OLD)  only for MODIFY
//   0x01        1-byte separator between OLD and NEW entries (MODIFY only)
//   entry(NEW)
//   trailer     { u8 0x00; u32 payloadLen; u16 0 }   (9 bytes)
//   payload     payloadLen bytes (compressed diff for MODIFY; full new-file image
//               for NEW; payloadLen may be 0). Runs back-to-back to the next
//               separator or the EOF terminator.
//
// Entry descriptor:
//   name lp     { u8 len; u8 len (dup); u8 shortName[len] }   (e.g. "UI.VDK")
//   u8[4]       attribute prefix, observed 0xE1 0xC0 0x00 0x00 (0xE1 ?? 0x00 0x00)
//   u32 (BE)    file size, masked with 0x0FFFFFFF (top nibble is a flag)
//   meta        VARIABLE-LENGTH metadata/checksum block (per SPEC 6.3 the dual
//               rolling checksums w1/w2 + weak length bytes). Its internal layout
//               is not needed to locate payloads, so it is not decoded here; the
//               record boundary is resolved deterministically from the fixed
//               9-byte trailer + the next separator/EOF anchor. The descriptor end
//               (where the trailer begins) is found by scanning, because the meta
//               block has no length prefix.
//
// NOTE on the diff stream: unlike rtptool's v2.09, OUR v1.01 MODIFY payload does
// NOT begin with the 0xB59C bit-stream magic. Every sampled payload starts with
// the bytes 0x40 0xF8 ... (first 16 bits 0x40F8). The codec header layout differs
// and is the responsibility of RtpCodec (Codec.cs); Container.cs only delimits the
// raw payload bytes via PayloadOffset / PayloadLength.

namespace Ro2.RtPatch;

/// <summary>RTPatch record type (matches the rtptool nibble values).</summary>
public enum RecordType
{
    Eof = 1,
    Rename = 2,
    New = 3,
    Modify = 4,
    Mkdir = 5,
    Delete = 6,
}

/// <summary>
/// One source or destination file version descriptor (SPEC 6.3).
/// </summary>
public sealed class RtpEntry
{
    /// <summary>Short (8.3-style) file name, e.g. "UI.VDK".</summary>
    public string Name = string.Empty;

    /// <summary>File size (4-byte big-endian field masked with 0x0FFFFFFF).</summary>
    public long Size;

    /// <summary>
    /// First rolling checksum word (SPEC 6.3 "w1", 31-bit). In v1.01 the raw
    /// little-endian value of the first 4 bytes of the meta checksum region.
    /// </summary>
    public uint W1;

    /// <summary>
    /// Second rolling checksum word (SPEC 6.3 "w2", 30-bit), as raw little-endian.
    /// </summary>
    public uint W2;

    /// <summary>
    /// 4-byte attribute prefix preceding the size (observed 0xE1 0xC0 0x00 0x00).
    /// Kept for fidelity; the high byte is the file-attribute/flags marker.
    /// </summary>
    public uint AttrPrefix;

    /// <summary>Raw bytes of the variable-length metadata/checksum block.</summary>
    public byte[] Meta = Array.Empty<byte>();
}

/// <summary>One patch record (one target file operation).</summary>
public sealed class RtpRecord
{
    public RecordType Type;

    /// <summary>Full relative path as stored in the record, e.g. "Data\UI.VDK".</summary>
    public string Path = string.Empty;

    /// <summary>Index into <see cref="RtpPatch.Dirs"/> for the directory context.</summary>
    public int DirIndex;

    /// <summary>Source (old) file size; 0 for whole-file NEW records.</summary>
    public long SrcSize;

    /// <summary>Destination (new) file size.</summary>
    public long DstSize;

    /// <summary>Byte offset into <see cref="RtpPatch.Raw"/> where the payload begins.</summary>
    public int PayloadOffset;

    /// <summary>Payload length in bytes (compressed diff for MODIFY, raw image for NEW; may be 0).</summary>
    public int PayloadLength;

    public List<RtpEntry> Src = new();
    public List<RtpEntry> Dst = new();

    /// <summary>True when this record carries an inline compressed diff to apply.</summary>
    public bool HasDiff => Type == RecordType.Modify && PayloadLength > 0;
}

/// <summary>A parsed RTPatch v1.01 ("KX") container.</summary>
public sealed class RtpPatch
{
    public ushort Version;
    public ushort Flags;
    public uint FormatTag;

    /// <summary>
    /// "Extra mode" flag (alternate timestamp / path fields) — never observed set
    /// in v1.01 samples, retained for shared-contract compatibility.
    /// </summary>
    public bool ExtraMode;

    public List<string> Dirs = new();
    public List<RtpRecord> Records = new();
    public byte[] Raw = Array.Empty<byte>();

    // ── Decoded fixed-header fields (v1.01-specific, beyond the shared contract) ──
    public ulong OldFilesetBytes;
    public ulong NewFilesetBytes;
    public ulong BytesGrown;
    public ulong Cursor;
    public ulong OldCount;
    public ulong NewCount;
    public ulong MaxCount;
}

/// <summary>Parser for the RTPatch v1.01 ("KX") container.</summary>
public static class RtpContainer
{
    private const ushort ExpectedVersion = 0x0101;

    /// <summary>Parse a complete .RTP file into an <see cref="RtpPatch"/>.</summary>
    /// <param name="data">Full file bytes.</param>
    /// <exception cref="InvalidDataException">Thrown on a structural mismatch.</exception>
    public static RtpPatch Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 0x5A)
            throw new InvalidDataException($"File too small to be an RTPatch container ({data.Length} bytes).");

        if (data[0] != (byte)'K' || data[1] != (byte)'X')
            throw new InvalidDataException(
                $"Bad magic: expected \"KX\", got 0x{data[0]:X2}{data[1]:X2}.");

        var patch = new RtpPatch { Raw = data };
        patch.Version = ReadU16(data, 0x02);
        if (patch.Version != ExpectedVersion)
            throw new InvalidDataException($"Unsupported version 0x{patch.Version:X4} (expected 0x{ExpectedVersion:X4}).");

        // 0x04 const 0x01000000, 0x0C const 0x00000030 — not validated strictly.
        patch.FormatTag = ReadU32(data, 0x08);
        patch.Flags = 0;
        patch.ExtraMode = false;

        patch.OldFilesetBytes = ReadU64(data, 0x10);
        patch.NewFilesetBytes = ReadU64(data, 0x18);
        patch.BytesGrown = ReadU64(data, 0x20);
        patch.Cursor = ReadU64(data, 0x28);
        patch.OldCount = ReadU64(data, 0x30);
        patch.NewCount = ReadU64(data, 0x38);
        patch.MaxCount = ReadU64(data, 0x40);

        // ── Directory table @ 0x50 ──
        int p = 0x50;
        // u32 dictBodyLen, u32 0
        p += 8;
        int dirCount = data[p];
        p += 1;
        for (int i = 0; i < dirCount; i++)
        {
            int len = data[p];
            // duplicated length byte at p+1
            int start = p + 2;
            int end = start + len;
            if (end > data.Length)
                throw new InvalidDataException($"Directory entry {i} runs past end of file @0x{p:X}.");
            patch.Dirs.Add(Latin1(data, start, len));
            p = end;
        }

        ParseRecords(data, p, patch);
        return patch;
    }

    private static void ParseRecords(byte[] data, int p, RtpPatch patch)
    {
        int n = data.Length;

        while (p < n - 1)
        {
            // EOF terminator: a bare u16 0x0000.
            if (data[p] == 0 && data[p + 1] == 0)
                break;

            if (!IsSeparator(data, p))
                throw new InvalidDataException(
                    $"Expected record separator @0x{p:X}, found {Hex(data, p, 6)}.");
            p += 6;

            // Target path lp.
            if (!TryReadLp(data, p, out string path, out int afterPath))
                throw new InvalidDataException($"Bad target path @0x{p:X}.");
            p = afterPath;

            int dirIndex = data[p];
            p += 1;

            // Marker ends in 0x01 immediately before the first entry descriptor.
            // A leading 0x09 byte signals a MODIFY (delta) record (OLD + NEW entries);
            // otherwise it is a whole-file NEW record (NEW entry only).
            bool isDelta = data[p] == 0x09;

            // Skip marker bytes up to the first valid entry descriptor.
            int markerLimit = Math.Min(n, p + 8);
            int entryPos = p;
            while (entryPos < markerLimit && !IsEntryDescriptor(data, entryPos))
                entryPos++;
            if (entryPos >= markerLimit)
                throw new InvalidDataException($"No entry descriptor after marker @0x{p:X} (path '{path}').");

            var record = new RtpRecord
            {
                Type = isDelta ? RecordType.Modify : RecordType.New,
                Path = path,
                DirIndex = dirIndex,
            };

            // First entry.
            RtpEntry first = ReadEntry(data, entryPos, out int afterFirst);
            int afterEntries;

            if (isDelta)
            {
                record.Src.Add(first);
                record.SrcSize = first.Size;

                // The OLD and NEW entries are separated by a single 0x01 byte inside
                // the metadata region; locate the NEW entry by scanning for that byte
                // followed by a valid descriptor.
                int mp = afterFirst;
                while (mp < n && !(data[mp] == 0x01 && IsEntryDescriptor(data, mp + 1)))
                    mp++;
                if (mp >= n)
                    throw new InvalidDataException($"Missing NEW entry for MODIFY '{path}'.");

                RtpEntry second = ReadEntry(data, mp + 1, out afterEntries);
                record.Dst.Add(second);
                record.DstSize = second.Size;
            }
            else
            {
                record.Dst.Add(first);
                record.DstSize = first.Size;
                afterEntries = afterFirst;
            }

            // ── Trailer + payload ──
            // Trailer = { u8 0x00; u32 payloadLen; u16 0 }. The metadata block of the
            // last entry has no length prefix, so we locate the trailer by scanning
            // for the fixed shape whose declared payload ends exactly on the next
            // record boundary (separator + valid path) or on the EOF terminator.
            if (!TryFindTrailer(data, afterEntries, out int payloadOffset, out int payloadLength))
                throw new InvalidDataException($"Could not locate payload trailer for '{path}'.");

            record.PayloadOffset = payloadOffset;
            record.PayloadLength = payloadLength;
            patch.Records.Add(record);

            p = payloadOffset + payloadLength;
        }
    }

    /// <summary>
    /// Scan forward from <paramref name="from"/> for the 9-byte trailer
    /// { 0x00, u32 len, u16 0 } whose payload ends on a valid record boundary.
    /// </summary>
    private static bool TryFindTrailer(byte[] data, int from, out int payloadOffset, out int payloadLength)
    {
        int n = data.Length;
        for (int hp = from; hp <= n - 9; hp++)
        {
            if (data[hp] != 0x00)
                continue;
            // u16 reserved at hp+5 must be 0.
            if (data[hp + 5] != 0 || data[hp + 6] != 0)
                continue;

            long len = ReadU32(data, hp + 1);
            long end = hp + 9 + len;
            if (end < 0 || end > n)
                continue;

            if (IsRecordBoundary(data, (int)end))
            {
                payloadOffset = hp + 9;
                payloadLength = (int)len;
                return true;
            }
        }

        payloadOffset = 0;
        payloadLength = 0;
        return false;
    }

    /// <summary>True at the EOF terminator (u16 0) or at a separator followed by a path lp.</summary>
    private static bool IsRecordBoundary(byte[] data, int q)
    {
        int n = data.Length;
        if (q == n - 2 && data[q] == 0 && data[q + 1] == 0)
            return true;
        return IsSeparator(data, q) && TryReadLp(data, q + 6, out _, out _);
    }

    /// <summary>
    /// Record separator: { 0x71, var, 0xB0, 0x01, 0x00, 0x00 }. The second byte
    /// varies per record (record-type/flags); the rest is invariant across samples.
    /// </summary>
    private static bool IsSeparator(byte[] data, int q)
    {
        return q + 6 <= data.Length
            && data[q] == 0x71
            && data[q + 2] == 0xB0
            && data[q + 3] == 0x01
            && data[q + 4] == 0x00
            && data[q + 5] == 0x00;
    }

    /// <summary>
    /// An entry descriptor starts with a length-prefixed short name followed by the
    /// 4-byte attribute prefix 0xE1 ?? 0x00 0x00.
    /// </summary>
    private static bool IsEntryDescriptor(byte[] data, int p)
    {
        if (!TryReadLp(data, p, out _, out int q))
            return false;
        return q + 8 <= data.Length
            && data[q] == 0xE1
            && data[q + 2] == 0x00
            && data[q + 3] == 0x00;
    }

    /// <summary>Read a full entry descriptor; <paramref name="end"/> is the byte after its metadata block.</summary>
    private static RtpEntry ReadEntry(byte[] data, int p, out int end)
    {
        if (!TryReadLp(data, p, out string name, out int q))
            throw new InvalidDataException($"Bad entry name @0x{p:X}.");

        uint attrPrefix = ReadU32(data, q);
        q += 4;

        // 4-byte big-endian size with the top nibble masked off.
        uint sizeRaw = ReadU32Be(data, q);
        long size = sizeRaw & 0x0FFFFFFF;
        q += 4;

        // The metadata/checksum block is variable-length and has no length prefix.
        // Its end is resolved by the caller via the trailer/separator anchors, so we
        // do not consume it here; we expose the first 8 bytes (w1/w2) for fidelity
        // and leave Meta empty (callers locate boundaries structurally).
        var entry = new RtpEntry
        {
            Name = name,
            Size = size,
            AttrPrefix = attrPrefix,
            W1 = q + 4 <= data.Length ? ReadU32(data, q) : 0,
            W2 = q + 8 <= data.Length ? ReadU32(data, q + 4) : 0,
            Meta = Array.Empty<byte>(),
        };

        end = q;
        return entry;
    }

    /// <summary>Read a duplicated-length-prefixed ANSI string: { len, len, bytes[len] }.</summary>
    private static bool TryReadLp(byte[] data, int p, out string value, out int end)
    {
        value = string.Empty;
        end = p;
        if (p + 2 > data.Length)
            return false;

        int len = data[p];
        if (len == 0 || len > 0x40 || data[p + 1] != len)
            return false;

        int start = p + 2;
        int stop = start + len;
        if (stop > data.Length)
            return false;

        for (int i = start; i < stop; i++)
        {
            byte b = data[i];
            if (b < 0x20 || b >= 0x7F)
                return false; // printable ANSI only — keeps scans from false-matching binary
        }

        value = Latin1(data, start, len);
        end = stop;
        return true;
    }

    // ── Little helpers ──
    private static ushort ReadU16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));

    private static uint ReadU32(byte[] d, int o) =>
        (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));

    private static uint ReadU32Be(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    private static ulong ReadU64(byte[] d, int o) =>
        ReadU32(d, o) | ((ulong)ReadU32(d, o + 4) << 32);

    private static string Latin1(byte[] d, int o, int len)
    {
        var sb = new System.Text.StringBuilder(len);
        for (int i = 0; i < len; i++)
            sb.Append((char)d[o + i]);
        return sb.ToString();
    }

    private static string Hex(byte[] d, int o, int len)
    {
        var sb = new System.Text.StringBuilder(len * 3);
        for (int i = 0; i < len && o + i < d.Length; i++)
            sb.Append(d[o + i].ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
