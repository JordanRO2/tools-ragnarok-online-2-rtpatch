// Builder for RTPatch v1.01 ("KX") containers — the inverse of the applier we reversed.
//
// Tier 1: whole-file NEW records (create/replace a file) with STORE-mode (uncompressed)
// payloads. The official applier (expapply.dll / the launcher updater) reads these back;
// every output is verified by round-tripping through the real eh.exe.
//
// Format (confirmed against 00000293.RTP's "caustic" record + the parser sub_10007A90):
//   header  : 0x58 fixed bytes — magic, version, consts, formatTag, eight u64 totals
//             (all informational → 0 works), v47=trailing-map-len=0, v45=catalog-byte-len.
//   catalog : byte dirCount; dirCount x { u8 len; u8 len; ascii name }. records start at
//             0x58 + v45.
//   record  : u16 flags=0x5071; [0x10] 4-byte context; [0x20] path lp; [0x40] dirIndex
//             varint; OLD-count varint (0); NEW-count varint (1); then the NEW entry:
//             name lp; attr varint; size varint; X varint; Y varint; 10-byte signature;
//             9-byte trailer { 00, u32 LE payloadLen, 4x 00 }; payload.
//   payload : store block [0x00][BE32(N+5)][content] (decoder emits N bytes, self-stops).
//   EOF     : u16 0x0000.

using System.Buffers.Binary;

namespace Ro2.RtPatch;

/// <summary>Builds an RTPatch v1.01 container that creates whole files (store-mode payloads).</summary>
public sealed class RtpBuilder
{
    private readonly List<NewFile> _files = new();
    private sealed record NewFile(string Dir, string Name, byte[] Content);

    // Per-record fields confirmed constant across the sampled NEW records. Exposed so the
    // round-trip harness can probe alternatives if the applier turns out to validate them.
    private const ushort Flags = 0x5071;
    private static readonly byte[] ContextField = { 0xB0, 0x01, 0x00, 0x00 };
    private const ulong AttrValue = 0x01F80000;

    /// <summary>Queue a file to create. <paramref name="dir"/> is the directory portion
    /// (e.g. "Data" or "GSA\CREATECHARACTER"); <paramref name="name"/> the leaf file name.</summary>
    public void AddFile(string dir, string name, byte[] content)
        => _files.Add(new NewFile(dir.Replace('/', '\\').Trim('\\'), name, content));

    /// <summary>Serialize the container to bytes.</summary>
    public byte[] Build()
    {
        if (_files.Count == 0)
            throw new InvalidOperationException("no files queued");

        // Directory catalog: unique directories in first-seen order.
        var dirs = new List<string>();
        foreach (var f in _files)
            if (!dirs.Contains(f.Dir, StringComparer.OrdinalIgnoreCase))
                dirs.Add(f.Dir);

        byte[] catalog = BuildCatalog(dirs);

        var ms = new MemoryStream();
        WriteHeader(ms, catalog.Length);
        ms.Write(catalog, 0, catalog.Length);          // records start at 0x58 + catalog.Length
        foreach (var f in _files)
        {
            int dirIndex = dirs.FindIndex(d => string.Equals(d, f.Dir, StringComparison.OrdinalIgnoreCase));
            WriteRecord(ms, f, dirIndex);
        }
        WriteU16(ms, 0x0000);                            // EOF terminator
        return ms.ToArray();
    }

    private static byte[] BuildCatalog(List<string> dirs)
    {
        var ms = new MemoryStream();
        ms.WriteByte((byte)dirs.Count);
        foreach (var d in dirs)
            WriteLp(ms, d);
        return ms.ToArray();
    }

    private void WriteHeader(MemoryStream ms, int catalogLen)
    {
        ulong newBytes = 0;
        foreach (var f in _files) newBytes += (ulong)f.Content.Length;
        ulong count = (ulong)_files.Count;

        ms.WriteByte((byte)'K');
        ms.WriteByte((byte)'X');
        WriteU16(ms, 0x0101);          // 0x02 version
        WriteU32(ms, 0x01000000);      // 0x04 const (its u16@0x06 is the v42 flag word 0x0100)
        WriteU32(ms, 0x0000009F);      // 0x08 formatTag (informational; 0x9F as in real no-op 255.RTP)
        WriteU32(ms, 0x00000030);      // 0x0C const
        // The applier divides progress by these totals, so they must be real (0 => div-by-zero
        // once a record exists). Whole-file creates: old=0, new=bytesGrown=cursor=sum of sizes.
        WriteU64(ms, 0);               // 0x10 oldFilesetBytes
        WriteU64(ms, newBytes);        // 0x18 newFilesetBytes
        WriteU64(ms, newBytes);        // 0x20 bytesGrown
        WriteU64(ms, newBytes);        // 0x28 cursor
        WriteU64(ms, 0);               // 0x30 oldCount
        WriteU64(ms, count);           // 0x38 newCount
        WriteU64(ms, count);           // 0x40 maxCount
        WriteU64(ms, 0);               // 0x48 v47 trailing-record-map length (MUST be 0)
        WriteU64(ms, (ulong)catalogLen); // 0x50 v45 catalog byte length (STRICT: records start at 0x58+v45)
    }

    // ── one whole-file NEW record ──
    private void WriteRecord(MemoryStream ms, NewFile f, int dirIndex)
    {
        string fullPath = f.Dir.Length > 0 ? f.Dir + "\\" + f.Name : f.Name;
        byte[] content = f.Content;
        byte[] payload = StorePayload(content);
        byte[] sig;
        using (var cs = new MemoryStream(content, writable: false))
            sig = RtpSignature.Compute(cs);
        // Set the "variant" bit (byte0 high bit). The applier derives its accumulation flag
        // from this bit; with it set it takes the plain path (no tail quirk), which is exactly
        // what RtpSignature.Compute models — so our stored signature matches what it recomputes.
        sig[0] |= 0x80;

        WriteU16(ms, Flags);                  // u16 flags = 0x5071
        ms.Write(ContextField, 0, 4);         // [0x10] 4-byte context field
        WriteLp(ms, fullPath);                // [0x20] full relative path
        WriteVarint(ms, (ulong)dirIndex);     // [0x40] dirIndex into the catalog
        WriteVarint(ms, 0);                   // OLD entry count = 0
        WriteVarint(ms, 1);                   // NEW entry count = 1

        // NEW entry #0
        WriteLp(ms, f.Name);                  // leaf name
        WriteVarint(ms, AttrValue);           // attribute word (struct+4)
        WriteVarint(ms, (ulong)content.Length); // file size (struct+24) — the verifier's length
        WriteVarint(ms, 0);                   // X: old-fileset byte offset (struct+8); 0 for from-scratch
        WriteVarint(ms, 0);                   // Y: new-fileset byte offset (struct+16); 0 for from-scratch
        ms.Write(sig, 0, RtpSignature.Length);// 10-byte rolling signature of the content

        // 9-byte trailer: 00, u32 LE payloadLen, 4x 00
        ms.WriteByte(0x00);
        WriteU32(ms, payload.Length);
        WriteU32(ms, 0);

        ms.Write(payload, 0, payload.Length);
    }

    // store-mode payload: [0x00][BE32(N+5)][content][0xFF]. The store block (mode 0x00 +
    // 4-byte length + N data) makes the decoder emit N raw bytes; the trailing 0xFF is the
    // control "end of stream" mode byte, so the block loop stops instead of reading past the
    // payload (which the applier reports as error 16).
    internal static byte[] StorePayload(byte[] content)
    {
        var p = new byte[5 + content.Length + 1];
        p[0] = 0x00;
        uint len = checked((uint)content.Length + 5);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(1), len);
        Array.Copy(content, 0, p, 5, content.Length);
        p[^1] = 0xFF;  // control: done
        return p;
    }

    // leading-byte big-endian varint (inverse of sub_100198A0)
    internal static void WriteVarint(Stream s, ulong v)
    {
        // (extraBytes, leadMask, leadShift) by value range
        if (v < 0x80UL)                  { s.WriteByte((byte)v); return; }
        int extra;
        byte lead;
        if      (v < 0x4000UL)            { extra = 1; lead = 0x80; }
        else if (v < 0x200000UL)          { extra = 2; lead = 0xC0; }
        else if (v < 0x10000000UL)        { extra = 3; lead = 0xE0; }
        else if (v < 0x800000000UL)       { extra = 4; lead = 0xF0; }
        else if (v < 0x40000000000UL)     { extra = 5; lead = 0xF8; }
        else if (v < 0x2000000000000UL)   { extra = 6; lead = 0xFC; }
        else if (v < 0x100000000000000UL) { extra = 7; lead = 0xFE; }
        else                              { extra = 8; lead = 0xFF; }
        int leadBits = extra == 7 || extra == 8 ? 0 : 7 - extra; // bits carried in the lead byte
        byte leadVal = (byte)(lead | (leadBits == 0 ? 0 : (byte)(v >> (8 * extra))));
        s.WriteByte(leadVal);
        for (int i = extra - 1; i >= 0; i--)  // magnitude big-endian
            s.WriteByte((byte)(v >> (8 * i)));
    }

    // ── primitives ──
    private static void WriteU16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }
    private static void WriteU32(Stream s, long v) { for (int i = 0; i < 4; i++) s.WriteByte((byte)(v >> (8 * i))); }
    private static void WriteU64(Stream s, ulong v) { for (int i = 0; i < 8; i++) s.WriteByte((byte)(v >> (8 * i))); }

    private static void WriteLp(Stream s, string value)
    {
        if (value.Length > 0xFE) throw new ArgumentException($"name too long: {value}");
        s.WriteByte((byte)value.Length);
        s.WriteByte((byte)value.Length);
        foreach (char c in value) s.WriteByte((byte)c);
    }
}
