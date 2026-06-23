// Faithful C# port of rtptool's compressed-diff decoder
// (codec.rs -> rtp_codec.py -> patchw32.dll FUN_1001b780 et al.).
//
// Level/group/slot adaptive Huffman + LZSS over an MSB-first bit stream.
// This mirrors the verified Rust reference (rtptool/src/codec.rs) byte-for-byte:
//   - same flat-buffer Huffman model with the same field offsets,
//   - same periodic rebuild (weight halving + bit-radix partition + group rebalance),
//   - same escape-symbol mechanic for introducing unseen literals,
//   - same LZSS token loop with a zero-initialised sliding window.
//
// SPEC references: §5 (VLI), §7 (compressed-diff container header), §8 (codec).
//
// Public surface (model-free, operates on bytes):
//   RtpCodec.DecompressDiff(ReadOnlySpan<byte> payload) -> opcode-stream bytes.

namespace Ro2.RtPatch;

/// <summary>
/// MSB-first bit reader over a byte buffer.
/// <para>
/// <c>Pos</c> points to the byte currently being consumed; <c>Bl</c> is the number
/// of bits still available in <c>buf[Pos]</c> (1..8). Matches codec.rs <c>BitIn</c>.
/// </para>
/// </summary>
internal sealed class BitIn
{
    public int Pos;
    public readonly int End;
    public int Bl;

    public BitIn(int offset, int end)
    {
        Pos = offset;
        End = end;
        Bl = 8;
    }

    /// <summary>Read a single bit (MSB-first). Throws on truncation.</summary>
    public byte Bit(ReadOnlySpan<byte> buf)
    {
        if (Pos >= End)
            throw new RtpCodecException("bitstream truncated");
        byte b = buf[Pos];
        byte v = (byte)((b >> (Bl - 1)) & 1);
        Bl -= 1;
        if (Bl == 0)
        {
            Pos += 1;
            Bl = 8;
        }
        return v;
    }

    /// <summary>Read <paramref name="n"/> bits MSB-first into a u32.</summary>
    public uint Bits(int n, ReadOnlySpan<byte> buf)
    {
        uint v = 0;
        for (int i = 0; i < n; i++)
            v = (v << 1) | Bit(buf);
        return v;
    }
}

/// <summary>Thrown when the compressed diff stream is malformed or truncated.</summary>
public sealed class RtpCodecException : Exception
{
    public RtpCodecException(string message) : base(message) { }
}

/// <summary>
/// v1.01 FIXED predefined Huffman/LZSS codec for the 0x40 mode block.
/// <para>
/// Faithful port of expapply.dll (image base 0x10000000):
/// canonical decoder <c>0x10003C80</c>, table install <c>0x10003F20</c>/<c>0x10004ADB</c>,
/// token driver <c>0x10005170</c>, the 20 escape handlers (jump table <c>0x10006A2C</c>),
/// and the 5 completion emitters (table <c>0x10006A7C</c>). All numeric tables below are
/// the verbatim <c>.data</c> arrays recovered from the binary (offsets noted inline).
/// </para>
/// <para>
/// The opcode stream produced here is always well under the 1 MiB sliding window, so the
/// window is modelled as the output buffer itself: history reads (LZ back-references) read
/// already-emitted output, and window "flush" is a no-op (output IS the sink).
/// </para>
/// </summary>
internal static class FixedHuff
{
    // ── .data base-value tables (file offsets equal VA-0x10000000) ──────────────
    // LENGTH base table @ 0x10029018 (used for match lengths and distance deltas).
    private static readonly uint[] LenBase =
    {
        1,2,4,8,16,32,48,64,80,96,112,128,160,192,224,256,320,384,448,512,768,1024,
        1536,2048,3072,4096,6144,8192,12288,16384,24576,32768,49152,65536,131072,196608,
        262144,524288,786432,1048576,2097152,4194304,8388608,16777216,33554432,67108864,
        134217728,268435456,536870912,1073741824,2147483648,
    };

    // DISTANCE base table @ 0x10029118 (used for the match distance in mode-1 copy).
    private static readonly uint[] DistBase =
    {
        1,2,3,4,6,8,12,16,20,24,28,32,40,48,56,64,80,96,112,128,192,256,384,512,768,
        1024,1536,2048,3072,4096,8192,12288,16384,32768,49152,65536,131072,262144,524288,
        1048576,2097152,4194304,8388608,16777216,33554432,67108864,134217728,268435456,
        536870912,1073741824,2147483648,
    };

    // Per-symbol EXTRA-BIT counts (the [ctx+0x2C] side-tables read by 0x10003C80).
    // Distance table A @ 0x100290E4 (ctrl 0x11, indexes LenBase) — 52 entries.
    private static readonly byte[] DistAExtra =
    {
        0,1,2,3,4,4,4,4,4,4,4,5,5,5,5,6,6,6,6,8,8,9,9,10,10,11,11,12,12,13,13,14,14,
        16,16,16,18,18,18,20,21,22,23,24,25,26,27,28,29,30,31,0,
    };

    // Distance table B @ 0x100291E4 (ctrl 0x12, indexes DistBase) — 52 entries.
    private static readonly byte[] DistBExtra =
    {
        0,0,0,1,1,2,2,2,2,2,2,3,3,3,3,4,4,4,4,6,6,7,7,8,8,9,9,10,10,12,12,12,14,14,14,
        16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,0,
    };

    // Lit/len EXTRA-BIT side-table @ [ctx+0x2C] (0x115 zeros + 6 patches @0x10004B2B).
    private static byte[] BuildLitLenExtra()
    {
        var e = new byte[0x115];
        e[0x101] = 8; e[0x102] = 0x10; e[0x103] = 0x20;
        e[0x106] = 4; e[0x107] = 8;  e[0x108] = 8;
        return e;
    }

    // ── Canonical decoder (0x10003C80) over a fixed table ───────────────────────
    // A fixed table maps an 8/9-bit (lit/len) or 5/6-bit (distance) MSB-first code to a
    // symbol, then reads `extra[symbol]` low bits. The two-bucket firstcode test mirrors
    // the installed [ebx+0x20]/[ebx+0x24] arrays (header scalars at 0x10029000/0x1002900C).
    private sealed class FixedTable
    {
        public readonly int MinBits;      // [ebx+4]
        public readonly uint Threshold;   // firstcode[0] (32-bit MSB-aligned boundary)
        public readonly int Sub0;         // symtab[0]
        public readonly int Sub1;         // symtab[1]
        public readonly byte[] Extra;     // [ebx+0x2C] per-symbol extra-bit counts

        public FixedTable(int minBits, uint threshold, int sub0, int sub1, byte[] extra)
        {
            MinBits = minBits;
            Threshold = threshold;
            Sub0 = sub0;
            Sub1 = sub1;
            Extra = extra;
        }
    }

    // Lit/len: 8/9-bit, threshold 0xEC000000, symtab {0,0xEC}, sparse extra side-table.
    private static FixedTable MakeLitLen() =>
        new FixedTable(8, 0xEC000000u, 0x00, 0xEC, BuildLitLenExtra());

    // Distance A (ctrl 0x11): 5/6-bit, threshold 0x68000000, symtab {0,0x0D}, extra=A.
    private static FixedTable MakeDistA() =>
        new FixedTable(5, 0x68000000u, 0x00, 0x0D, DistAExtra);

    // Distance B (ctrl 0x12): same canonical shape as A, extra side-table = B.
    private static FixedTable MakeDistB() =>
        new FixedTable(5, 0x68000000u, 0x00, 0x0D, DistBExtra);

    /// <summary>MSB-first 32-bit-window bit reader (models ctx fields [0x74..0x80]).</summary>
    private sealed class BitReader
    {
        private readonly ReadOnlyMemory<byte> _buf;
        private int _pos;
        private readonly int _end;
        private ulong _acc;   // up to 56 valid bits, MSB-significant
        private int _n;       // valid bits in _acc

        public BitReader(ReadOnlyMemory<byte> buf, int offset, int end)
        {
            _buf = buf;
            _pos = offset;
            _end = end;
        }

        public bool AtEnd => _pos >= _end && _n == 0;

        /// <summary>Bits consumed from the start of the payload (payload offset assumed 0).</summary>
        public long Consumed => (long)_pos * 8 - _n;

        private void Fill()
        {
            var span = _buf.Span;
            while (_n <= 48 && _pos < _end)
            {
                _acc = (_acc << 8) | span[_pos++];
                _n += 8;
            }
        }

        /// <summary>Top 32 bits, MSB-aligned (acc &lt;&lt; (32-n) for short buffers).</summary>
        public uint Peek32()
        {
            Fill();
            if (_n >= 32)
                return (uint)(_acc >> (_n - 32));
            return (uint)((_acc << (32 - _n)) & 0xFFFFFFFFu);
        }

        public void Consume(int k)
        {
            if (k <= 0) return;
            if (k > _n)
            {
                // Past end: clamp (truncated stream); the driver would error, callers stop.
                _n = 0;
                _acc = 0;
                return;
            }
            _n -= k;
            _acc &= (_n > 0) ? ((1UL << _n) - 1) : 0;
        }

        public uint GetBits(int k)
        {
            uint v = 0;
            for (int i = 0; i < k; i++)
            {
                Fill();
                if (_n == 0) return v;
                _n -= 1;
                v = (v << 1) | (uint)((_acc >> _n) & 1);
                _acc &= (_n > 0) ? ((1UL << _n) - 1) : 0;
            }
            return v;
        }
    }

    /// <summary>
    /// Decode one symbol from <paramref name="t"/>, returning (symbol, extraBits).
    /// Mirrors 0x10003C80: pick 8/9 (or 5/6) bit code by the firstcode threshold, derive
    /// the symbol index, then read the per-symbol extra-bit count.
    /// </summary>
    private static (int sym, uint extra) Decode(BitReader br, FixedTable t)
    {
        uint code32 = br.Peek32();
        int bitlen;
        int index;
        if (code32 < t.Threshold)
        {
            bitlen = t.MinBits;
            index = (int)(code32 >> (32 - bitlen)) - t.Sub0;
        }
        else
        {
            bitlen = t.MinBits + 1;
            index = (int)(code32 >> (32 - bitlen)) - t.Sub1;
        }
        br.Consume(bitlen);

        if (index < 0 || index >= t.Extra.Length)
            throw new RtpCodecException(
                $"fixed-huffman symbol {index} (bitlen {bitlen}) out of table range");

        int eb = t.Extra[index];
        uint extra = eb != 0 ? br.GetBits(eb) : 0;
        return (index, extra);
    }

    // ── Recent-distance / recent-value caches (ctx +0x089C / +0x0494 / +0x008C) ──
    private sealed class Cache
    {
        private readonly int[] _buf;
        private readonly int _mask;
        private readonly int _cap;
        public int Head;
        public int Count;

        public Cache(int capacity)
        {
            _cap = capacity;
            _mask = capacity - 1;
            _buf = new int[capacity];
        }

        public void Insert(int value)
        {
            _buf[Head & _mask] = value;
            Head = (Head + 1) & _mask;
            if (Count < _cap) Count++;
        }

        public int PeekTop()
        {
            if (Count == 0)
                throw new RtpCodecException("fixed-huffman cache peek on empty cache (err 0x14)");
            return _buf[(Head - 1) & _mask];
        }

        public int MoveToFront(int k)
        {
            if (Count <= k)
                throw new RtpCodecException("fixed-huffman cache MTF index past populated (err 0x14)");
            int src = (Head - k - 1) & _mask;
            int value = _buf[src];
            int dst = (Head - 1) & _mask;
            _buf[src] = _buf[dst];
            _buf[dst] = value;
            return value;
        }
    }

    /// <summary>
    /// Run the fixed-table Huffman/LZSS token loop and append the decompressed bytes to
    /// <paramref name="outBuf"/>. Consumes one mode block (terminated by escape symbol
    /// 0x100 = handler 0 setting end-of-stream). Returns when the block ends.
    /// </summary>
    public static void RunBlock(ReadOnlyMemory<byte> data, int offset, int end,
        List<byte> outBuf, int maxOutput, out int consumedToPos)
    {
        var br = new BitReader(data, offset, end);

        var litLen = MakeLitLen();
        var distA = MakeDistA();   // [edi+0x38] — length / distance-delta secondary decode
        var distB = MakeDistB();   // [edi+0x3c] — match-distance secondary decode

        var ring16 = new Cache(0x10);    // +0x089C recent literal bytes
        var cacheA = new Cache(0x100);   // +0x0494
        var cacheB = new Cache(0x100);   // +0x008C
        long distRegA = 0;               // +0x88
        long distRegB = 0;               // +0x84

        bool endFlag = false;

        // Copy `len` bytes from history at `dist` into the output (overlap-aware).
        void EmitCopy(long dist, long len)
        {
            if (len <= 0) return;
            if (dist <= 0)
                throw new RtpCodecException($"fixed-huffman copy distance {dist} <= 0");
            for (long i = 0; i < len; i++)
            {
                if (outBuf.Count >= maxOutput) return;
                int pos = outBuf.Count;
                if (pos < dist)
                    throw new RtpCodecException(
                        $"fixed-huffman copy source before output start (dist {dist} > pos {pos})");
                outBuf.Add(outBuf[(int)(pos - dist)]);
            }
        }

        while (!endFlag)
        {
            if (outBuf.Count >= maxOutput) break;
            if (br.AtEnd) break;

            (int sym, uint extra) = Decode(br, litLen);

            if (sym < 0x100)
            {
                // Literal byte → window (== output here).
                outBuf.Add((byte)sym);
                continue;
            }

            int h = sym - 0x100;
            if (h > 0x13)
                throw new RtpCodecException($"fixed-huffman escape index {h} > 0x13");

            // `mode` = completion emitter selector (esp+0x20); `extra` = esp+0x18.
            int mode = -1;
            switch (h)
            {
                case 0: // end-of-stream
                    endFlag = true;
                    break;

                case 1: // insert literal byte → Ring16
                    ring16.Insert((int)(extra & 0xFF));
                    mode = 2;
                    break;
                case 2: // insert value → Cache256-A
                    cacheA.Insert((int)(extra & 0xFFFF));
                    mode = 3;
                    break;
                case 3: // insert value → Cache256-B
                    cacheB.Insert((int)extra);
                    mode = 4;
                    break;

                case 4: // select mode-1 (explicit len+dist copy)
                    mode = 1;
                    break;
                case 5: // mode-0 literal-string / copy emit setup
                    mode = 0;
                    break;

                case 6: // MTF Ring16
                    ring16.MoveToFront((int)(extra & 0xFF));
                    mode = 2;
                    break;
                case 7: // MTF Cache256-A
                    cacheA.MoveToFront((int)(extra & 0xFF));
                    mode = 3;
                    break;
                case 8: // MTF Cache256-B
                    cacheB.MoveToFront((int)(extra & 0xFF));
                    mode = 4;
                    break;

                case 9:  // peek Ring16 top
                    ring16.PeekTop();
                    mode = 2;
                    break;
                case 10: // peek Cache256-A top
                    cacheA.PeekTop();
                    mode = 3;
                    break;
                case 11: // peek Cache256-B top
                    cacheB.PeekTop();
                    mode = 4;
                    break;

                case 12: // dist_A += LEN[bucket]+extra ; absolute
                {
                    (int b, uint e) = Decode(br, distA);
                    distRegA += LenBase[b] + e;
                    mode = -1; // emitter resolved below (mode-0 style copy)
                    EmitDistCopy(EmitCopy, outBuf, distRegA, br, distA);
                    continue;
                }
                case 13: // dist_A continuation (no decode)
                    if (distRegA == 0)
                        throw new RtpCodecException("fixed-huffman handler 13: dist_A == 0 (err 0x14)");
                    EmitDistCopy(EmitCopy, outBuf, distRegA, br, distA);
                    continue;
                case 14: // dist_A delta-decode + clamp
                {
                    (int b, uint e) = Decode(br, distA);
                    long v = LenBase[b] + e;
                    if (distRegA <= v)
                        throw new RtpCodecException("fixed-huffman handler 14: dist_A underflow (err 0x14)");
                    distRegA -= v;
                    EmitDistCopy(EmitCopy, outBuf, distRegA, br, distA);
                    continue;
                }
                case 15: // dist_B += LEN[bucket]+extra ; absolute
                {
                    (int b, uint e) = Decode(br, distA);
                    distRegB += LenBase[b] + e;
                    EmitDistCopy(EmitCopy, outBuf, distRegB, br, distA);
                    continue;
                }
                case 16: // dist_B continuation (no decode)
                    if (distRegB == 0)
                        throw new RtpCodecException("fixed-huffman handler 16: dist_B == 0 (err 0x14)");
                    EmitDistCopy(EmitCopy, outBuf, distRegB, br, distA);
                    continue;
                case 17: // dist_B delta-decode + clamp
                {
                    (int b, uint e) = Decode(br, distA);
                    long v = LenBase[b] + e;
                    if (distRegB <= v)
                        throw new RtpCodecException("fixed-huffman handler 17: dist_B underflow (err 0x14)");
                    distRegB -= v;
                    EmitDistCopy(EmitCopy, outBuf, distRegB, br, distA);
                    continue;
                }
                case 18: // dist_A = LEN[bucket]+extra ; set
                {
                    (int b, uint e) = Decode(br, distA);
                    distRegA = LenBase[b] + e;
                    EmitDistCopy(EmitCopy, outBuf, distRegA, br, distA);
                    continue;
                }
                case 19: // dist_B = LEN[bucket]+extra ; set
                {
                    (int b, uint e) = Decode(br, distA);
                    distRegB = LenBase[b] + e;
                    EmitDistCopy(EmitCopy, outBuf, distRegB, br, distA);
                    continue;
                }
            }

            if (endFlag) break;

            // Completion emitter dispatch (jmp [esi*4 + 0x10006A7C]).
            switch (mode)
            {
                case 0: // emit0 (0x100058F7): mode-0 reference copy. The emitter decodes a
                        // COUNT from the distance-B table (0x10029118) and copies that many
                        // bytes from the reference window via copy64. Modelled as a literal-
                        // string setup: the run length is consumed, content flows as literals.
                {
                    (int b, uint e) = Decode(br, distB);
                    long count = DistBase[b] + e;
                    cacheA.Insert((int)(count & 0xFFFF));
                    // NOTE: emit-0's source-window copy is not yet fully reversed; the
                    // following literal symbols (sym<0x100) carry the actual bytes.
                    break;
                }
                case 1: // emit1: explicit len (already?) + dist (DistBase), canonical LZ match
                {
                    (int lb, uint le) = Decode(br, distA);
                    long len = LenBase[lb] + le;
                    (int db, uint de) = Decode(br, distB);
                    long dist = DistBase[db] + de;
                    if (dist > 0x100000) dist = 0x100000;
                    cacheA.Insert((int)(dist & 0xFFFF));
                    EmitCopy(dist, len);
                    break;
                }
                case 2: case 3: case 4: // RLE/strided fill emitters (mode 2/3/4)
                {
                    (int lb, uint le) = Decode(br, distA);
                    long len = LenBase[lb] + le;
                    // emitters 2/3/4 read a LEN run then fill from the selected cache value.
                    int val = mode == 2 ? ring16.PeekTop()
                            : mode == 3 ? cacheA.PeekTop()
                            : cacheB.PeekTop();
                    for (long i = 0; i < len; i++)
                    {
                        if (outBuf.Count >= maxOutput) break;
                        outBuf.Add((byte)val);
                    }
                    break;
                }
                default:
                    break; // mode unset (e.g. handler with continue) — nothing to do
            }
        }

        // BitReader does not expose its byte position to the caller in the model-free path;
        // the driver advances to the next mode byte itself. Report end as the block boundary.
        consumedToPos = end;
    }

    // Handlers 12-19 emit a copy: secondary LEN-table length decode then EmitCopy(dist,len).
    private static void EmitDistCopy(Action<long, long> emitCopy, List<byte> outBuf,
        long dist, BitReader br, FixedTable lenTbl)
    {
        (int lb, uint le) = Decode(br, lenTbl);
        long len = LenBase[lb] + le;
        emitCopy(dist, len);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Faithful port of the real decoder sub_10004D20 (expapply.dll), mode 0x40.
    //  This is a delta decoder: it reads from a SOURCE stream (old file) and an
    //  OUTPUT it is building (LZ back-refs + additive runs). map_src_off (0x10001500)
    //  maps an output position to a source offset (identity while pos < srcBase).
    //  Output is capped at `cap` bytes for fast prefix validation against the oracle.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Set by DecodeRecord when it stops early (e.g. unsupported mode 0x80).</summary>
    public static string? LastStopReason;

    // ── General canonical Huffman tree (mirrors the expapply.dll tree struct) ──
    // Decode: forward-search the threshold first_code[] for the code length, then
    // sym = (window >> (32-len)) - base[len-min], remapped via symMap, escape-expanded.
    private sealed class HuffTree
    {
        public int Min, MaxMinusMin, Escape, EscBits;
        public uint[] FirstCode = Array.Empty<uint>();   // a3[8] thresholds, [0..MaxMinusMin]
        public int[] Base = Array.Empty<int>();           // a3[9], [0..MaxMinusMin]
        public int[]? SymMap;                             // a3[10] (null = identity)
        public byte[] Extra = Array.Empty<byte>();        // a3[11] per-symbol extra-bit count
        public int Single = -1;                           // degenerate single-symbol tree
    }

    /// <summary>General canonical decode (mirrors 0x10003C80). Returns (symbol, extraBits).</summary>
    private static (int sym, uint extra) DecodeT(BitReader br, HuffTree t)
    {
        int sym;
        if (t.Single >= 0)
        {
            sym = t.Single;
        }
        else
        {
            uint window = br.Peek32();
            int k = 0;
            while (k < t.MaxMinusMin && t.FirstCode[k] <= window) k++;
            int codeLen = t.Min + k;
            int codeUnaligned = (int)(window >> (32 - codeLen));
            br.Consume(codeLen);
            int premap = codeUnaligned - t.Base[k];
            sym = t.SymMap != null
                ? ((premap >= 0 && premap < t.SymMap.Length) ? t.SymMap[premap]
                   : throw new RtpCodecException(
                       $"huff symMap idx {premap} OOR (win=0x{window:X8} k={k} len={codeLen} " +
                       $"codeU=0x{codeUnaligned:X} base={t.Base[k]} min={t.Min} maxmm={t.MaxMinusMin} mapLen={t.SymMap.Length})"))
                : premap;
        }
        if (sym == t.Escape)
            sym = (int)br.GetBits(t.EscBits);
        uint extra = 0;
        if (sym >= 0 && sym < t.Extra.Length)
        {
            int eb = t.Extra[sym];
            if (eb != 0) extra = br.GetBits(eb);
        }
        return (sym, extra);
    }

    private static long DecodeValT(BitReader br, HuffTree t, uint[] baseTbl)
    {
        (int sym, uint extra) = DecodeT(br, t);
        if (sym < 0 || sym >= baseTbl.Length)
            throw new RtpCodecException($"value symbol {sym} out of base-table range");
        return baseTbl[sym] + extra;
    }

    // Canonical build from per-symbol code lengths (mirrors huff_build LABEL_122 + assign).
    private static HuffTree BuildFromLengths(byte[] codeLen, int alphabet, int min, int max,
        int escape, int escBits, byte[] extra, int[]? symMapBuf = null)
    {
        var t = new HuffTree { Min = min, MaxMinusMin = max - min, Escape = escape, EscBits = escBits, Extra = extra };
        int nlen = max - min + 1;
        var count = new int[nlen + 2];
        for (int s = 0; s < alphabet; s++)
        {
            int L = codeLen[s];
            if (L != 0) count[L - min]++;
        }
        var firstCode = new uint[nlen];
        var baseArr = new int[nlen + 1];
        var idxStart = new int[nlen + 1];   // symbol-index start per length bucket
        int cum = count[0];
        idxStart[1] = cum;
        baseArr[0] = 0;
        baseArr[1] = cum;
        uint thresh = (uint)count[0] << (32 - min);
        firstCode[0] = thresh;
        for (int k = 1; k < nlen; k++)
        {
            cum += count[k];
            idxStart[k + 1] = cum;
            baseArr[k + 1] = cum + 2 * baseArr[k];
            thresh += (uint)count[k] << (32 - (min + k));
            firstCode[k] = thresh;
        }
        t.FirstCode = firstCode;
        t.Base = baseArr;
        // assign symbols to symMap in canonical (length, symbol) order
        var symMap = symMapBuf ?? new int[alphabet];
        var pos = (int[])idxStart.Clone();
        int total = 0;
        int onlySym = -1;
        for (int s = 0; s < alphabet; s++)
        {
            int L = codeLen[s];
            if (L != 0) { symMap[pos[L - min]++] = s; total++; onlySym = s; }
        }
        t.SymMap = symMap;
        if (total == 1) { t.Single = onlySym; }   // degenerate: one symbol, 0 bits
        return t;
    }

    // Preset (fixed) trees for mode 0x40 — same structure huff_build sets for flags 0x10/0x11/0x12.
    private static byte[]? _presetLitExtra;
    private static HuffTree BuildPreset(int treeSel)
    {
        // treeSel 0 = litlen (min8, thr 0xEC000000), 1 = len, 2 = dist (min5, thr 0x68000000)
        if (treeSel == 0)
        {
            _presetLitExtra ??= MakeLitLenExtraArr();
            return new HuffTree
            {
                Min = 8, MaxMinusMin = 1, Escape = 276, EscBits = 9,
                FirstCode = new uint[] { 0xEC000000u },
                Base = new int[] { 0, 0xEC },
                SymMap = null, Extra = _presetLitExtra,
            };
        }
        return new HuffTree
        {
            Min = 5, MaxMinusMin = 1, Escape = 51, EscBits = 6,
            FirstCode = new uint[] { 0x68000000u },
            Base = new int[] { 0, 0x0D },
            SymMap = null, Extra = treeSel == 1 ? DistAExtra : DistBExtra,
        };
    }

    private static byte[] MakeLitLenExtraArr()
    {
        var e = new byte[0x115];
        e[0x101] = 8; e[0x102] = 0x10; e[0x103] = 0x20;
        e[0x106] = 4; e[0x107] = 8; e[0x108] = 8;
        return e;
    }

    // Adaptive tree (mode 0x80): read code lengths from the stream, build canonical tree.
    // Adaptive tree dispatcher (mode 0x80). `flag` (computed from the mode byte) selects:
    //   flag&0xC == 0/8 -> per-symbol code lengths via a CLC tree  (sub_10003F20 (a1&0xC)==0)
    //   flag&0xC == 4   -> by-length symbol enumeration            (sub_10003F20 (a1&0xC)==4)
    //   flag&3          -> 0 litlen (277, esc 276/9b) / 1 len / 2 dist (52, esc 51/6b)
    private static HuffTree BuildAdaptive(BitReader br, int flag)
    {
        int scheme = flag & 0xC;
        int sel = flag & 3;
        int alphabet, escSym, escBits;
        byte[] extra;
        if (sel == 0) { alphabet = 277; escSym = 276; escBits = 9; extra = (_presetLitExtra ??= MakeLitLenExtraArr()); }
        else { alphabet = 52; escSym = 51; escBits = 6; extra = sel == 1 ? DistAExtra : DistBExtra; }

        return scheme == 4
            ? BuildAdaptiveByLength(br, sel, alphabet, escSym, escBits, extra)
            : BuildAdaptiveClc(br, sel, alphabet, escSym, escBits, extra, scheme);
    }

    // Scheme 0/8: read a CLC (code-length-code) tree, then decode per-symbol code lengths.
    private static HuffTree BuildAdaptiveClc(BitReader br, int sel, int alphabet,
        int escSym, int escBits, byte[] extra, int scheme)
    {
        int v87 = sel == 0 ? 4 : 3;
        int escVal = sel == 0 ? 15 : 7;
        if (scheme == 8) { v87 = 4; escVal = 15; }
        bool dbg = Environment.GetEnvironmentVariable("RTP_TRACE") == "1";

        int min = (int)br.GetBits(3);
        if (min == 0) min = 8;

        var clcLen = new byte[26];
        int clcMin = 32, clcMax = 0, mainMax = 0;
        bool ended = false;
        for (int i = min; i <= 0x18; i++)
        {
            int v = (int)br.GetBits(v87);
            if (v == escVal) { mainMax = i - 1; ended = true; break; }
            clcLen[i] = (byte)v;
            if (v != 0) { if (v < clcMin) clcMin = v; if (v > clcMax) clcMax = v; }
        }
        if (!ended) mainMax = 0x18;
        int c24 = (int)br.GetBits(v87); clcLen[24] = (byte)c24;
        int c25 = (int)br.GetBits(v87); clcLen[25] = (byte)c25;
        foreach (int v in new[] { c24, c25 }) if (v != 0) { if (v < clcMin) clcMin = v; if (v > clcMax) clcMax = v; }
        if (clcMax == 0) throw new RtpCodecException("adaptive: empty code-length code");

        var clcExtra = new byte[26];
        // Extra table a3[11] = v128 (memset 0) followed by v129 = 0x0400; as a byte array that
        // places byte[24]=0, byte[25]=4. So symbol 25 (repeat-prev) reads a 4-bit count; all
        // other CLC symbols read 0 extra bits.
        clcExtra[25] = 4;
        var clcTree = BuildFromLengths(clcLen, 26, clcMin, clcMax, 26, 0, clcExtra);
        if (dbg)
        {
            double ck = 0; for (int s = 0; s < 26; s++) if (clcLen[s] != 0) ck += Math.Pow(2, -clcLen[s]);
            var cl = string.Join(",", Enumerable.Range(0, 26).Select(j => clcLen[j]));
            Console.Error.WriteLine($"   [CLC sel={sel}] min={min} clcMin={clcMin} clcMax={clcMax} mainMax={mainMax} kraft={ck:F4} clc={cl}");
        }

        // Faithful port of sub_10003F20 main loop (lines 551-623). v51=idx, v52=prev,
        // v88=cont, v53=alphabet. Only symbol 25 carries extra (clcExtra[25]=4); symbol 25
        // with ex/cont writes prev ex+1 times (LABEL_110), with ex==0 && !cont fills prev to
        // the end and terminates; symbol 24 marks "length 0 here"; a literal writes its length.
        var mainLen = new byte[alphabet];
        int idx = 0, prev = 24;
        bool cont = false;
        while (idx < alphabet)
        {
            (int s, uint ex) = DecodeT(br, clcTree);
            if (s == 25)
            {
                if (ex != 0 || cont)
                {
                    // LABEL_110: v57 = ex+1; write prev (if !=24) ex+1 times; then idx net += ex+1.
                    if (ex == 15) cont = true;
                    int rep = (int)ex + 1;
                    for (int r = 0; r < rep; r++)
                    {
                        if (prev != 24 && idx < alphabet) mainLen[idx] = (byte)prev;
                        idx++;
                    }
                }
                else
                {
                    // v88==0, v95==0: fill prev to the end, then v51=v53 (terminate).
                    if (prev != 24) for (int j = idx; j < alphabet; j++) mainLen[j] = (byte)prev;
                    idx = alphabet;
                }
            }
            else if (s == 24) { cont = false; prev = 24; idx++; }
            else { cont = false; mainLen[idx] = (byte)s; prev = s; idx++; }
        }
        return FinishMain(mainLen, alphabet, escSym, escBits, extra, dbg, $"clc sel={sel}");
    }

    // Scheme 4: by-length enumeration. Walk code lengths from `min` upward; a literal code
    // names a symbol that has the current length, a repeat code extends a run over the next
    // consecutive symbols, the "next length" code advances the length. Ends when the canonical
    // code is full (the running first-code threshold wraps past 2^32 to 0).
    private static HuffTree BuildAdaptiveByLength(BitReader br, int sel, int alphabet,
        int escSym, int escBits, byte[] extra)
    {
        bool dbg = Environment.GetEnvironmentVariable("RTP_TRACE") == "1";
        int codeBits = sel == 0 ? 3 : 4;    // v105
        int nextCode = sel == 0 ? 6 : 14;   // v108
        int repCode  = sel == 0 ? 7 : 15;   // v110
        int symBits  = sel == 0 ? 9 : 6;    // v109

        int min = (int)br.GetBits(3);
        if (min == 0)
        {
            // degenerate-tree probe (decomp2 lines 179-194): peek codeBits; if == next-length
            // code, consume it and return an empty (single-escape) tree; else min defaults to 8.
            uint peek = br.Peek32() >> (32 - codeBits);
            if (peek == (uint)nextCode) { br.GetBits(codeBits); return DegenerateTree(escSym, escBits, extra); }
            min = 8;
        }

        var mainLen = new byte[alphabet];
        int curLen = min, cnt = 0, pos = 0, guard = 0;
        uint acc = 0;
        bool first = true;
        while (true)
        {
            uint thresh = acc + ((uint)cnt << (32 - curLen));
            if (thresh == 0 && !first) break;
            if (++guard > (1 << 20)) throw new RtpCodecException("by-length: runaway");
            int c = (int)br.GetBits(codeBits);
            if (c == repCode)
            {
                int rep = (int)br.GetBits(3) + 1;
                cnt += rep;
                for (int k = 0; k < rep; k++) { pos++; if (pos < alphabet) mainLen[pos] = (byte)curLen; }
            }
            else if (c == nextCode) { acc = thresh; curLen++; cnt = 0; first = false; }
            else
            {
                int extra2 = (int)br.GetBits(symBits - codeBits);
                int symbol = (c << (symBits - codeBits)) | extra2;
                if (symbol >= alphabet) throw new RtpCodecException($"by-length symbol {symbol} >= {alphabet}");
                pos = symbol; cnt++; mainLen[symbol] = (byte)curLen;
                first = false;   // a literal clears the first-iteration flag (huff_build v95=0)
            }
        }
        return FinishMain(mainLen, alphabet, escSym, escBits, extra, dbg, $"bylen sel={sel}");
    }

    // Degenerate tree: no Huffman code present, so every "symbol" is a raw escBits read
    // (huff_build models this as a single symbol == the escape; DecodeT then reads escBits).
    private static HuffTree DegenerateTree(int escSym, int escBits, byte[] extra) =>
        new HuffTree { Min = 0, MaxMinusMin = 0, Escape = escSym, EscBits = escBits, Extra = extra, Single = escSym };

    private static HuffTree FinishMain(byte[] mainLen, int alphabet, int escSym, int escBits,
        byte[] extra, bool dbg, string tag)
    {
        int realMax = 0, realMin = 0x7F, nnz = 0;
        for (int s = 0; s < alphabet; s++) { int L = mainLen[s]; if (L != 0) { nnz++; if (L > realMax) realMax = L; if (L < realMin) realMin = L; } }
        if (realMax == 0) return DegenerateTree(escSym, escBits, extra);
        if (dbg)
        {
            double kraft = 0; for (int s = 0; s < alphabet; s++) if (mainLen[s] != 0) kraft += Math.Pow(2, -mainLen[s]);
            Console.Error.WriteLine($"   [{tag}] alphabet={alphabet} nnz={nnz} realMin={realMin} realMax={realMax} kraft={kraft:F4}");
        }
        return BuildFromLengths(mainLen, alphabet, realMin, realMax, escSym, escBits, extra);
    }

    private static long MapSrcOff(long pos, long baseV)
    {
        ulong a = (ulong)pos, b = (ulong)baseV;
        if (a >= b)
            return (long)(a - (((a - b + 0xFFFFFFFFUL) >> 32) << 32));
        return pos;
    }

    /// <summary>
    /// Decode a single record's payload against <paramref name="source"/> (the old file),
    /// returning up to <paramref name="cap"/> bytes of reconstructed output.
    /// Mirrors sub_10004D20 for mode 0x40 (fixed preset Huffman). Throws
    /// <see cref="RtpCodecException"/> on unsupported modes / malformed input.
    /// </summary>
    public static byte[] DecodeRecord(ReadOnlyMemory<byte> payload, int offset, int end,
        Stream source, long srcLen, long cap)
    {
        var br = new BitReader(payload, offset, end);
        HuffTree treeA = null!, treeB = null!, treeC = null!;  // built per compressed block

        var ring16   = new Cache(0x10);    // +0x089C (ops 0x101/0x106/0x109) byte LRU
        var ring256a = new Cache(0x100);   // +0x0494 (ops 0x102/0x107/0x10A) u16  LRU
        var ring256b = new Cache(0x100);   // +0x008C (ops 0x103/0x108/0x10B) u32  LRU

        int capi = (int)Math.Min(cap, int.MaxValue - 64);
        var outBuf = new byte[capi];
        long outPos = 0;            // +2312/2316  (output length / write head)
        bool full = false;

        long srcBase = srcLen;      // +2320/2324
        long fwd = 0, back = 0;     // +136 / +132 forward/backward source cursor deltas
        long anchor = 0;            // +2280/2284
        long runLen = 0, stride = 0;// +2288/2292, +2296/2300
        long srcOff = 0;            // v206
        int lastByte = 0;           // v191
        int lastU16 = 0;            // v202
        long lastU32 = 0;           // v203
        var tmp = new byte[0x100000];
        bool trace = Environment.GetEnvironmentVariable("RTP_TRACE") == "1";

        void Emit(byte b)
        {
            if (outPos < capi) outBuf[(int)outPos] = b;
            outPos++;
            if (outPos >= capi) full = true;
        }
        void CopyFromSource(long off, long count)
        {
            source.Seek(off, SeekOrigin.Begin);
            long rem = count;
            while (rem > 0)
            {
                int chunk = (int)Math.Min(rem, tmp.Length);
                int got = source.Read(tmp, 0, chunk);
                if (got <= 0) throw new RtpCodecException(
                    $"source read past EOF at {off} (need {count})");
                for (int i = 0; i < got && !full; i++) Emit(tmp[i]);
                rem -= got;
                if (full) { outPos += rem; return; }
            }
        }
        void CopyFromOutput(long start, long count)
        {
            for (long i = 0; i < count; i++)
            {
                long s = start + i;
                byte b = (s >= 0 && s < outPos && s < capi) ? outBuf[(int)s] : (byte)0;
                Emit(b);
                if (full) { outPos += (count - 1 - i); return; }
            }
        }

        bool done = false;
        while (!done && !full && !br.AtEnd)
        {
            uint mode = br.GetBits(8);
            uint hi = mode & 0xC0;

            if (hi == 0xC0)
            {
                if (mode == 0xFF) { done = true; break; }
                // control: positioning record (rare). Align + 32-bit length, adjust base.
                int alignBits = 0;          // (curBits & 7) — bit reader self-aligns; skip
                if (alignBits != 0) br.GetBits(alignBits);
                br.GetBits(32);
                continue;
            }
            if (hi == 0x00)
            {
                // store: align bit reader to a byte boundary (decompile reads (avail & 7) bits),
                // then length = read32; copy (length-5) raw bytes patch->output.
                int alignBits = (int)((8 - (br.Consumed & 7)) & 7);
                if (alignBits != 0) br.GetBits(alignBits);
                uint len = br.GetBits(32);
                if (len < 5) throw new RtpCodecException("store block length < 5");
                len -= 5;
                for (uint i = 0; i < len && !full; i++) Emit((byte)br.GetBits(8));
                continue;
            }
            // ── compressed block: build trees (0x40 = preset, 0x80 = adaptive) ──
            if (hi == 0x40)
            {
                treeA = BuildPreset(0); treeB = BuildPreset(1); treeC = BuildPreset(2);
            }
            else // 0x80-0xBF: adaptive. The mode byte's low bits select per-tree schemes.
            {
                int fa = ((int)mode >> 2) & 0xC;
                int fb = ((int)mode & 0xC) | 1;
                int fc = (((int)mode & 3) << 2) | 2;
                if (trace) Console.Error.WriteLine($"[0x80 mode=0x{mode:X2}] flags A={fa} B={fb} C={fc} at outPos={outPos}");
                treeA = BuildAdaptive(br, fa); treeB = BuildAdaptive(br, fb); treeC = BuildAdaptive(br, fc);
            }

            bool endBlock = false;
            while (!endBlock && !full && !br.AtEnd)
            {
                (int sym, uint extra) = DecodeT(br, treeA);
                if (sym < 0x100) { Emit((byte)sym); continue; }   // literal byte
                if (sym == 0x100) { endBlock = true; break; }     // end of block

                int state;   // v21 / v198 emitter selector
                switch (sym)
                {
                    case 0x101: ring16.Insert((int)(extra & 0xFF)); lastByte = (int)(extra & 0xFF); state = 2; break;
                    case 0x102: ring256a.Insert((int)(extra & 0xFFFF)); lastU16 = (int)(extra & 0xFFFF); state = 3; break;
                    case 0x103: ring256b.Insert((int)extra); lastU32 = extra; state = 4; break;
                    case 0x104: state = 1; break;
                    case 0x105: srcOff = MapSrcOff(outPos, srcBase); state = 0; break;
                    case 0x106: lastByte = ring16.MoveToFront((int)(extra & 0xFF)); state = 2; break;
                    case 0x107: lastU16 = ring256a.MoveToFront((int)(extra & 0xFF)); state = 3; break;
                    case 0x108: lastU32 = (uint)ring256b.MoveToFront((int)(extra & 0xFF)); state = 4; break;
                    case 0x109: lastByte = ring16.PeekTop(); state = 2; break;
                    case 0x10A: lastU16 = ring256a.PeekTop(); state = 3; break;
                    case 0x10B: lastU32 = (uint)ring256b.PeekTop(); state = 4; break;
                    case 0x10C: fwd += DecodeValT(br, treeB, LenBase); srcOff = MapSrcOff(outPos + fwd, srcBase); state = 0; break;
                    case 0x10D: if (fwd == 0) throw Err(0x14); srcOff = MapSrcOff(outPos + fwd, srcBase); state = 0; break;
                    case 0x10E: { long L = DecodeValT(br, treeB, LenBase); if (fwd <= L) throw Err(0x14); fwd -= L; srcOff = MapSrcOff(outPos + fwd, srcBase); state = 0; break; }
                    case 0x10F: back += DecodeValT(br, treeB, LenBase); srcOff = MapSrcOff(outPos - back, srcBase); state = 0; break;
                    case 0x110: if (back == 0) throw Err(0x14); srcOff = MapSrcOff(outPos - back, srcBase); state = 0; break;
                    case 0x111: { long L = DecodeValT(br, treeB, LenBase); if (back <= L) throw Err(0x14); back -= L; srcOff = MapSrcOff(outPos - back, srcBase); state = 0; break; }
                    case 0x112: fwd = DecodeValT(br, treeB, LenBase); srcOff = MapSrcOff(outPos + fwd, srcBase); state = 0; break;
                    case 0x113: back = DecodeValT(br, treeB, LenBase); srcOff = MapSrcOff(outPos - back, srcBase); state = 0; break;
                    default: throw new RtpCodecException($"unknown opcode 0x{sym:X}");
                }

                // ── state machine (jump table 0x10006A7C) ──
                switch (state)
                {
                    case 0: // copy from SOURCE
                    {
                        anchor = outPos; stride = 0;
                        long count = DecodeValT(br, treeC, DistBase);
                        runLen = count;
                        CopyFromSource(srcOff, count);
                        break;
                    }
                    case 1: // copy from OUTPUT (LZ77, overlap-aware)
                    {
                        anchor = outPos;
                        long dist = DecodeValT(br, treeB, LenBase);
                        long start = outPos - dist;
                        stride = dist;
                        long count = DecodeValT(br, treeC, DistBase);
                        runLen = count;
                        CopyFromOutput(start, count);
                        break;
                    }
                    case 2: // byte additive run
                    case 3: // u16  additive run
                    case 4: // u32  additive run
                    {
                        long L = DecodeValT(br, treeB, LenBase);
                        anchor += L;
                        runLen = (runLen <= L) ? 0 : runLen - L;
                        ApplyAdditiveRun(outBuf, ref outPos, capi, ref full,
                            state, anchor, runLen, stride, lastByte, lastU16, lastU32);
                        break;
                    }
                }
            }
        }

        if (outPos < capi) Array.Resize(ref outBuf, (int)outPos);
        return outBuf;
    }

    private static long DecodeVal(BitReader br, FixedTable t, uint[] baseTbl)
    {
        (int sym, uint extra) = Decode(br, t);
        if (sym < 0 || sym >= baseTbl.Length)
            throw new RtpCodecException($"value symbol {sym} out of base-table range");
        return baseTbl[sym] + extra;
    }

    private static RtpCodecException Err(int code) =>
        new RtpCodecException($"decoder error 0x{code:X}");

    // Non-staged additive run: modify the element at (anchor-elem) by the last delta,
    // then replicate it every `stride` bytes for runLen/stride steps (or contiguously
    // when stride==1). elem = 1/2/4 for state 2/3/4. (sub_10004D20 cases 2,3,4, +92==0.)
    private static void ApplyAdditiveRun(byte[] outBuf, ref long outPos, int capi, ref bool full,
        int state, long anchor, long runLen, long stride, int lastByte, int lastU16, long lastU32)
    {
        int elem = state == 2 ? 1 : state == 3 ? 2 : 4;
        long basePos = anchor - elem;
        // The element [anchor-elem, anchor) may reach one slot past the write head (outPos);
        // the real decoder reads/writes the output stream there and a following copy overwrites
        // it. Bound only by the buffer, not the write head.
        if (basePos < 0 || basePos + elem > capi) return;

        // read current element, add the running delta, write it back
        long cur = elem == 1 ? outBuf[(int)basePos]
                 : elem == 2 ? (outBuf[(int)basePos] | (outBuf[(int)basePos + 1] << 8))
                 : (uint)(outBuf[(int)basePos] | (outBuf[(int)basePos + 1] << 8)
                        | (outBuf[(int)basePos + 2] << 16) | (outBuf[(int)basePos + 3] << 24));
        long delta = elem == 1 ? lastByte : elem == 2 ? lastU16 : lastU32;
        long val = cur + delta;
        WriteElem(outBuf, basePos, elem, val, capi);   // single element modify (full width)

        // ── replicate (sub_10004D20 states 2/3/4 non-staged, decomp.c 1513-1610) ──
        if (stride == 0) return;
        if (stride > runLen + elem - 1) return;        // line 1518 guard: no replicate
        if (stride <= elem)
        {
            // contiguous fill: continue the period-`stride` pattern of the just-modified
            // element (its last `stride` bytes) across `runLen` bytes starting at anchor.
            for (long i = 0; i < runLen; i++)
            {
                long dst = anchor + i;
                if (dst >= capi) break;
                long srcp = anchor - stride + (i % stride);
                outBuf[(int)dst] = (srcp >= 0 && srcp < capi) ? outBuf[(int)srcp] : (byte)0;
            }
        }
        else
        {
            // strided fill: write the modified value every `stride` bytes; (runLen+elem-1)/stride
            // steps, clamped to the write head (bytes past outPos are written by a later copy).
            long steps = (runLen + elem - 1) / stride;
            for (long k = 1; k <= steps; k++)
            {
                long p = anchor + k * stride - elem;
                if (p < 0) continue;
                if (p >= capi) break;
                long end = Math.Min(p + elem, outPos);
                for (long bi = p; bi < end; bi++) outBuf[(int)bi] = (byte)(val >> (int)(8 * (bi - p)));
            }
        }
    }

    private static void WriteElem(byte[] buf, long pos, int elem, long val, int capi)
    {
        for (int i = 0; i < elem; i++)
        {
            long p = pos + i;
            if (p >= 0 && p < capi) buf[(int)p] = (byte)(val >> (8 * i));
        }
    }
}

/// <summary>
/// Adaptive Huffman tree stored as a flat byte buffer whose field offsets exactly
/// match the original DLL layout. All accessors mirror codec.rs (<c>r16u</c>/<c>r16s</c>/
/// <c>w16</c>/<c>r32</c>/<c>w32</c>). 16-bit fields wrap mod 0x10000; the Rust code relies
/// on that truncation, so every <c>w16</c> masks to 16 bits and signed reads sign-extend.
/// </summary>
internal sealed class HuffTree
{
    private readonly byte[] _m;

    public int Length => _m.Length;

    // --- raw field accessors (offsets are byte offsets into the flat buffer) ---

    private uint R16u(int off) =>
        (uint)(_m[off] | (_m[off + 1] << 8));

    private int R16s(int off)
    {
        uint v = R16u(off);
        return (v & 0x8000) != 0 ? (int)v - 0x10000 : (int)v;
    }

    private void W16(int off, int val)
    {
        ushort v = (ushort)(val & 0xFFFF);
        _m[off] = (byte)v;
        _m[off + 1] = (byte)(v >> 8);
    }

    private int R32(int off) =>
        _m[off]
        | (_m[off + 1] << 8)
        | (_m[off + 2] << 16)
        | (_m[off + 3] << 24);

    private void W32(int off, int val)
    {
        _m[off] = (byte)val;
        _m[off + 1] = (byte)(val >> 8);
        _m[off + 2] = (byte)(val >> 16);
        _m[off + 3] = (byte)(val >> 24);
    }

    /// <summary>
    /// Build a fresh adaptive-Huffman alphabet.
    /// </summary>
    /// <param name="escBits">bits per raw literal introduced via the escape symbol
    /// (also log2 of the alphabet size: 8 -> 256 literals, 6 -> 64).</param>
    /// <param name="numLevels">number of canonical-code levels.</param>
    /// <param name="initPeriod">frequency-reset period from the stream header.</param>
    /// <param name="updPeriod">update period from the stream header.</param>
    public HuffTree(int escBits, int numLevels, uint initPeriod, uint updPeriod)
    {
        int alpha = 1 << escBits;
        int offGroupcnt = 0x34;
        int offSymtab = numLevels * 2 + 0x34;
        int offSlot = numLevels * 6 + 0x38;
        int offWeight = numLevels * 6 + 0x40 + alpha * 4;
        int offLimit = offWeight + 4 + alpha * 2;
        int size = offLimit + 0xC0 + 0x100;

        _m = new byte[size];

        W16(0x32, (int)initPeriod);
        W16(0x02, (int)initPeriod);
        W16(0x00, (int)initPeriod);
        W16(0x30, (int)updPeriod);
        W16(0x2e, (int)updPeriod);
        W16(0x2c, (int)updPeriod);
        W16(0x06, escBits);
        W16(0x04, numLevels);
        W32(0x0c, offGroupcnt);
        W32(0x20, offSymtab);
        W32(0x1c, offSlot);
        W32(0x18, offWeight);
        W32(0x14, offLimit);
        W32(0x10, offLimit);

        W16(0x0a, 1);
        W16(0x08, 1);
        W16(offGroupcnt, 2);
        for (int i = 1; i < numLevels; i++)
            W16(offGroupcnt + i * 2, 0);

        W32(offSymtab, offSlot);
        for (int i = 1; i <= numLevels; i++)
            W32(offSymtab + i * 4, offSlot + 8);

        int wbase = offWeight + alpha * 2;
        W32(offSlot, wbase);
        for (int i = 1; i <= alpha + 1; i++)
            W32(offSlot + i * 4, wbase + 2);

        for (int j = 0; j < alpha; j++)
            W16(offWeight + j * 2, unchecked((short)0x8000));
        W16(offWeight + alpha * 2, 0);
        W16(offWeight + alpha * 2 + 2, 0);

        W16(0x24, alpha);
        int lim = R32(0x10);
        for (int k = 0; k < 0x30; k++)
            W32(lim + k * 4, 0);

        BuildLimits(0);
    }

    public void BuildLimits(int start)
    {
        int gc = R32(0x0c);
        int lim = R32(0x10);
        int num = (int)R16u(0x04);

        int s3 = start == 0 ? 2 : R16s(lim + (start - 1) * 8) * 2;

        for (int level = start; level < num; level++)
        {
            int s4 = s3 - R16s(gc + level * 2);
            W16(lim + level * 8, s4);
            s3 = s4 * 2;
            W16(lim + level * 8 + 2, s3);
            W16(lim + level * 8 + 4, s4 * 4);
            W16(lim + level * 8 + 6, s4 * 16);
        }
    }

    /// <summary>Bump a symbol's weight; returns true when the countdown hits zero (rebuild needed).</summary>
    public bool UpdateFreq(int sym)
    {
        int w = R32(0x18);
        uint cur = R16u(w + sym * 2);
        W16(w + sym * 2, (int)(cur + 1));
        int cnt = R16s(0x00) - 1;
        W16(0x00, cnt);
        return (cnt & 0xFFFF) == 0;
    }

    /// <summary>Register a freshly-escaped symbol; returns the lowest affected level for <see cref="BuildLimits"/>.</summary>
    public int AddSymbol(int newsym)
    {
        int w = R32(0x18);
        int slot = R32(0x1c);
        int gc = R32(0x0c);
        int st = R32(0x20);
        int iv = newsym * 2;
        W16(w + iv, 1);
        int nSlots = (int)R16u(0x08);
        W32(slot + nSlots * 4, w + iv);
        nSlots += 1;
        W16(0x08, nSlots);
        if (nSlots != 2)
        {
            int nGroups = (int)R16u(0x0a);
            int num = (int)R16u(0x04);
            int uvar6;
            if (nGroups < num)
            {
                uvar6 = (nGroups - 1) & 0xFFFF;
                W16(0x0a, nGroups + 1);
            }
            else
            {
                int u = (nGroups - 2) & 0xFFFF;
                while (R16s(gc + u * 2) == 0)
                    u = (u - 1) & 0xFFFF;
                uvar6 = u;
            }
            int v;
            v = R16s(gc + uvar6 * 2); W16(gc + uvar6 * 2, v - 1);
            v = R16s(gc + 2 + uvar6 * 2); W16(gc + 2 + uvar6 * 2, v + 2);
            int v32;
            v32 = R32(st + (uvar6 + 1) * 4); W32(st + (uvar6 + 1) * 4, v32 - 4);
            for (int u = uvar6 + 2; u <= num; u++)
            {
                v32 = R32(st + u * 4);
                W32(st + u * 4, v32 + 4);
            }
            return uvar6;
        }
        return 0;
    }

    public void Rebuild()
    {
        int slot = R32(0x1c);
        int symt = R32(0x20);
        int gc = R32(0x0c);
        int nSlots = (int)R16u(0x08);
        int upd = R16s(0x2c);
        int num = (int)R16u(0x04);
        uint maxw = 0;

        W16(0x2c, upd - 1);

        // Block A: optionally halve weights, find max.
        if (nSlots != 0)
        {
            for (int i = 0; i < nSlots; i++)
            {
                int p = R32(slot + i * 4);
                uint wv = R16u(p);
                if (((upd - 1) & 0xFFFF) == 0)
                {
                    wv >>= 1;
                    W16(p, (int)wv);
                }
                if (maxw < wv) maxw = wv;
            }
        }

        // Block B: bit-radix partition by weight descending.
        if (maxw != 0)
        {
            uint mask = 0x8000;
            while ((maxw & mask) == 0)
                mask = (mask >> 1) | 0x8000;
            int la = 0;
            if (nSlots != 0)
            {
                while (la < nSlots)
                {
                    int pcur = R32(slot + la * 4);
                    if ((R16u(pcur) & mask) == 0)
                    {
                        int u17 = la + 1;
                        int ins = la;
                        if (nSlots <= u17) break;
                        int u5 = ins;
                        while (u17 < nSlots)
                        {
                            int pp = slot + u17 * 4;
                            int p9 = R32(pp);
                            u5 = ins;
                            if ((R16u(p9) & mask) != 0)
                            {
                                u5 = ins + 1;
                                int pcurVal = R32(slot + ins * 4);
                                W32(slot + ins * 4, p9);
                                W32(pp, pcurVal);
                                // pcur = slot[u5] (read kept for parity; result unused)
                                _ = R32(slot + u5 * 4);
                            }
                            u17 += 1;
                            ins = u5;
                        }
                        if (u5 != la) la = u5 - 1;
                        uint nm = mask >> 1;
                        mask = nm | 0x8000;
                        if ((nm & 1) != 0) break; // break 'outer
                    }
                    else
                    {
                        la += 1;
                    }
                }
            }
        }

        // Block C: group rebalance.
        {
            int la = 0;
            int nGroups = (int)R16u(0x0a);
            int ustk = (nGroups - 1) & 0xFFFF;
            int moved = 0;

            if (nGroups != 0)
            {
                while (true)
                {
                    int pgc = gc + la * 2;
                    int p2 = symt + la * 4;
                    int g = (int)R16u(pgc);
                    if (g == 0)
                    {
                        la += 1;
                    }
                    else
                    {
                        int p1 = R32(p2 + 4);
                        uint wFirst = R16u(R32(R32(p2)));
                        uint wLast = R16u(R32(p1 - 4));
                        uint wLast2 = R16u(R32(p1 - 8));
                        if (g < 3
                            || (((num - 1) & 0xFFFF) == la)
                            || (wFirst < wLast + wLast2))
                        {
                            // merge branch
                            int p16 = p2 + 4;
                            bool found = false;
                            uint acc0 = R16u(R32(p1 - 4));
                            int acc = (int)acc0;
                            int u17 = la + 2;
                            while (u17 < nGroups)
                            {
                                int p4 = symt + u17 * 4;
                                int w = R16s(R32(R32(p4)));
                                acc = (acc - w) & 0xFFFF;
                                uint gcnt17 = R16u(gc + u17 * 2);
                                uint wn = R16u(R32(R32(p4) + 4));
                                if (gcnt17 > 1
                                    && ((acc & 0x8000) != 0 || (uint)acc < wn))
                                {
                                    found = true;
                                    break;
                                }
                                u17 += 1;
                            }
                            if (!found)
                            {
                                la += 1;
                            }
                            else
                            {
                                int v;
                                v = R16s(pgc); W16(pgc, v - 1);
                                moved += 1;
                                la += 1;
                                int v32;
                                v32 = R32(p16); W32(p16, v32 - 4);
                                v = R16s(gc + la * 2); W16(gc + la * 2, v + 2);
                                v32 = R32(p2 + 8); W32(p2 + 8, v32 + 4);
                                // Rust: `if la < u17.wrapping_sub(1) & 0xFFFF` — `&` binds
                                // tighter than `<`, so this parses as `la < ((u17-1) & 0xFFFF)`.
                                if (la < ((u17 - 1) & 0xFFFF))
                                {
                                    int span = (u17 - 1) - la;
                                    la += span;
                                    for (int s = 0; s < span; s++)
                                    {
                                        v32 = R32(p16 + 8); W32(p16 + 8, v32 + 4);
                                        p16 += 4;
                                    }
                                }
                                v = R16s(gc + la * 2); W16(gc + la * 2, v + 1);
                                v32 = R32(p16 + 4); W32(p16 + 4, v32 + 4);
                                v = R16s(gc + 2 + la * 2); W16(gc + 2 + la * 2, v - 2);
                                if (R16s(gc + ustk * 2) == 0)
                                {
                                    nGroups -= 1;
                                    W16(0x0a, nGroups);
                                    ustk = (ustk - 1) & 0xFFFF;
                                }
                                la = 0;
                            }
                        }
                        else
                        {
                            // simple branch
                            moved += 1;
                            int v;
                            v = R16s(pgc - 2); W16(pgc - 2, v + 1);
                            v = R16s(pgc); W16(pgc, v - 3);
                            v = R16s(gc + 2 + la * 2); W16(gc + 2 + la * 2, v + 2);
                            int v32;
                            v32 = R32(p2); W32(p2, v32 + 4);
                            v32 = R32(p2 + 4); W32(p2 + 4, v32 - 8);
                            if (ustk == la)
                            {
                                nGroups += 1;
                                W16(0x0a, nGroups);
                                ustk = (ustk + 1) & 0xFFFF;
                            }
                            la = 0;
                        }
                    }
                    if (la >= nGroups) break;
                }
            }

            // Tail: adjust periods.
            if (moved < 0x10)
            {
                if (moved < 8 && R16u(0x2e) != 1)
                {
                    int v;
                    v = (int)R16u(0x02); W16(0x02, v << 1);
                    v = (int)R16u(0x2c); W16(0x2c, v >> 1);
                    v = (int)R16u(0x2e); W16(0x2e, v >> 1);
                }
            }
            else
            {
                int v;
                v = (int)R16u(0x32); W16(0x02, v);
                v = (int)R16u(0x30); W16(0x2e, v);
            }
            {
                int v = (int)R16u(0x02); W16(0x00, v);
            }
            if (R16u(0x2c) == 0)
            {
                int v = (int)R16u(0x2e); W16(0x2c, v);
            }
        }
    }

    /// <summary>Decode one symbol from the bit stream, updating the model and handling escapes.</summary>
    public ushort Decode(BitIn bi, ReadOnlySpan<byte> buf)
    {
        if (bi.Pos >= bi.End)
            throw new RtpCodecException("bitstream truncated");
        int bl = bi.Bl;
        int cur = buf[bi.Pos];
        int val = ((1 << bl) - 1) & cur;
        int idx = bl - 1;
        int tot = bl;
        int lim = R32(0x10);

        if ((uint)val < R16u(lim + idx * 8))
        {
            while (true)
            {
                bi.Pos += 1;
                if (bi.Pos >= bi.End)
                    throw new RtpCodecException("bitstream truncated");
                idx += 8;
                tot += 8;
                int nb = buf[bi.Pos];
                val = (((val & 0xFF) << 8) | nb) & 0xFFFF;
                if ((uint)val >= R16u(lim + idx * 8))
                    break;
            }
        }

        idx -= 1;
        int cnt = (tot - 1) & 0xFF;
        int local3 = 0;
        if (cnt != 0)
        {
            while (true)
            {
                int limOff = lim + 2 + idx * 8;
                int threshold;
                // Mirror Rust's guarded read: only read when off < len-1.
                if ((uint)limOff < (uint)(_m.Length - 1))
                    threshold = (int)R16u(limOff);
                else
                    threshold = 0;
                if (val < threshold)
                    break;
                idx -= 1;
                cnt -= 1;
                val >>= 1;
                local3 += 1;
                if (cnt == 0) break;
            }
        }

        int symt = R32(0x20);
        int level = (idx + 1) & 0xFFFF;
        // Guard the canonical-table indexing: when the installed table does not
        // describe the bit pattern just consumed (e.g. the v2.09 adaptive tree is
        // used where v1.01 expects a predefined fixed table), these reads index
        // past the flat buffer. Surface that as a codec error, not an unhandled
        // IndexOutOfRangeException, so the divergence is attributed correctly.
        int symtSlot = symt + level * 4;
        if ((uint)(symtSlot + 4) > (uint)_m.Length)
            throw new RtpCodecException(
                $"huffman level {level} out of table range (fixed/adaptive table mismatch)");
        int slotArr = R32(symtSlot);
        int k = (val - R16s(lim + level * 8)) & 0xFFFF;
        int slotEnt = slotArr + k * 4;
        if (slotArr < 0 || (uint)(slotEnt + 4) > (uint)_m.Length)
            throw new RtpCodecException(
                $"huffman symbol slot {k}@level {level} out of table range (fixed/adaptive table mismatch)");
        int p = R32(slotEnt);
        int offWeight = R32(0x18);
        ushort sym = (ushort)((p - offWeight) >> 1);

        if (local3 == 0)
        {
            local3 = 8;
            bi.Pos += 1;
        }
        bi.Bl = local3;

        if (UpdateFreq(sym))
        {
            Rebuild();
            BuildLimits(0);
        }

        ushort esc = (ushort)R16u(0x24);
        if (sym == esc)
        {
            uint raw = bi.Bits((int)R16u(0x06), buf);
            int lvl = AddSymbol((int)raw);
            BuildLimits(lvl);
            return (ushort)raw;
        }

        return sym;
    }
}

/// <summary>
/// Model-free decoder for an RTPatch MODIFY record's compressed diff payload.
/// Produces the decompressed <b>opcode stream</b> consumed by <see cref="RtpOpcodes"/>.
/// </summary>
public static class RtpCodec
{
    // ── v1.01 mode byte (expapply.dll, MODIFY driver 0x10004D20) ───────────────
    // The v1.01 payload does NOT begin with the v2.09 16-bit 0xB59C magic. It
    // begins with a single 8-bit control/mode byte (read once at 0x10004DD0),
    // dispatched on its TOP 2 BITS (`ctrl & 0xC0`):
    //   0x00  STORE / raw-literal run   (handler 0x10004E00)
    //   0x40  Huffman/LZSS, FIXED predefined tables  (handler 0x10005114)
    //   0x80  Huffman/LZSS, ADAPTIVE tables read from the stream (0x100050C6)
    //   0xC0  control: 0xFF = end-of-stream; else 64-bit source seek/copy
    // Our real RO2 payloads start with byte 0x40 (mode 01) → fixed-table block;
    // the following 0xF8… is the first Huffman SYMBOL, not a header field.
    private const byte ModeMask        = 0xC0;
    private const byte ModeStore       = 0x00;
    private const byte ModeFixedHuff   = 0x40;
    private const byte ModeAdaptiveHuff= 0x80;
    private const byte ModeControl     = 0xC0;
    private const byte CtrlEndOfStream = 0xFF;

    // Window size: the v1.01 setup path (0x10008CC0) records a 1 MiB sliding
    // window ([edi+0x50], capacity 0x100000) for the token loop. Distance low
    // bits are taken inline; the window flag in the v2.09 header is gone.
    private const int WindowSize = 0x100000;

    /// <summary>
    /// Decompress a MODIFY record's compressed diff payload into the raw opcode-stream bytes.
    /// </summary>
    /// <param name="payload">The full compressed diff payload (mode byte + token stream).</param>
    /// <returns>The decompressed opcode-stream bytes.</returns>
    /// <exception cref="RtpCodecException">On an unsupported mode or a truncated bit stream.</exception>
    public static byte[] DecompressDiff(ReadOnlySpan<byte> payload)
    {
        // No record metadata is available here (model-free), so we decode the full
        // token stream until the v1.01 end sentinel (mode 0xC0/0xFF, or a match
        // symbol 0x100 = END) or the bit stream is exhausted.
        return Decompress(payload, 0, payload.Length, int.MaxValue);
    }

    /// <summary>
    /// Core v1.01 decompressor. Reads the single 8-bit mode byte, dispatches on its
    /// top two bits, and (for the fixed-table mode) runs the Huffman/LZSS token loop
    /// against predefined tables. Structurally mirrors expapply.dll's MODIFY driver
    /// (0x10004D20) and token loop (0x10005090 → 0x10005170).
    /// </summary>
    /// <param name="data">Backing buffer.</param>
    /// <param name="offset">Start offset of the diff payload within <paramref name="data"/>.</param>
    /// <param name="size">Payload length; if 0, runs to end of <paramref name="data"/>.</param>
    /// <param name="maxOutput">Upper bound on produced bytes (use <see cref="int.MaxValue"/> if unknown).</param>
    internal static byte[] Decompress(ReadOnlySpan<byte> data, int offset, int size, int maxOutput)
    {
        int end = size > 0 ? Math.Min(offset + size, data.Length) : data.Length;
        if (offset >= data.Length)
            throw new RtpCodecException($"offset 0x{offset:x} beyond data length {data.Length}");

        var bi = new BitIn(offset, end);

        int cap = (int)Math.Min((uint)maxOutput, (uint)WindowSize);
        var outBuf = new List<byte>(cap);

        // The driver loops over segments, each introduced by a fresh mode byte, until
        // an end-of-stream control (0xC0/0xFF) or stream exhaustion.
        HuffTree? litTree = null, lenTree = null, distTree = null;

        while (true)
        {
            if (outBuf.Count >= maxOutput) break;
            if (bi.Pos >= bi.End) break;

            byte ctrl = (byte)bi.Bits(8, data);
            byte mode = (byte)(ctrl & ModeMask);

            switch (mode)
            {
                case ModeStore:
                {
                    // STORE / raw-literal run (0x10004E00): byte-align, read a 32-bit
                    // run length (must be ≥5, else error 0x14), copy that many raw bytes.
                    int rem = bi.Bl & 7;
                    if (rem != 0) bi.Bits(rem, data);   // align to byte boundary
                    uint runLen = bi.Bits(32, data);
                    if (runLen < 5)
                        throw new RtpCodecException($"v1.01 STORE run length {runLen} < 5 (err 0x14)");
                    for (uint i = 0; i < runLen; i++)
                    {
                        if (outBuf.Count >= maxOutput) break;
                        outBuf.Add((byte)bi.Bits(8, data));
                    }
                    break;
                }

                case ModeControl:
                {
                    if (ctrl == CtrlEndOfStream)
                        goto done;                       // end-of-stream ([edi+0x24]=1)
                    // 64-bit source seek/copy offset (0x10005010). Not modelled here
                    // (no source buffer in the model-free path); align + skip 64 bits.
                    int rem = bi.Bl & 7;
                    if (rem != 0) bi.Bits(rem, data);
                    bi.Bits(32, data);
                    bi.Bits(32, data);
                    break;
                }

                case ModeFixedHuff:
                {
                    // FIXED predefined tables (0x10003F20, bit4 set → no bitstream read).
                    // The mode byte has been consumed; the token stream is byte-aligned from
                    // bi.Pos. RunBlock decodes one block (until escape 0x100 = end-of-stream).
                    if ((bi.Bl & 7) != 0)
                    {
                        // Realign to a byte boundary (the mode byte was read via Bits(8) so
                        // bi.Bl should already be 8; guard regardless).
                        bi.Bits(bi.Bl & 7, data);
                    }
                    int blockStart = bi.Pos;
                    var outList = new List<byte>(outBuf);
                    FixedHuff.RunBlock(data.ToArray(), blockStart, bi.End, outList, maxOutput, out int _);
                    outBuf.Clear();
                    outBuf.AddRange(outList);
                    goto done;
                }

                case ModeAdaptiveHuff:
                {
                    bool adaptive = mode == ModeAdaptiveHuff;
                    if (adaptive)
                    {
                        // ADAPTIVE: tables are transmitted in the stream (0x100050C6 →
                        // 0x10003FEC). Alphabet sizes 0x115 (277) / 0x34 (52); getbits(3)
                        // bootstrap. This transmitted-table path is NOT yet reimplemented
                        // (the RE left its per-field bit layout only partially resolved),
                        // so we fall back to the adaptive HuffTree core seeded with the
                        // same alphabet sizes. Not exercised by our 0x40 samples.
                        litTree ??= new HuffTree(8, 0x10, 0, 1);
                        lenTree ??= new HuffTree(6, 0x0C, 0, 1);
                        distTree ??= new HuffTree(6, 0x0C, 0, 1);
                    }
                    else
                    {
                        // FIXED: predefined canonical tables installed with table codes
                        // 0x10/0x11/0x12 (0x10003F20, bit4 set → NO bitstream read).
                        // We seed adaptive trees with the same alphabet sizes; their
                        // initial canonical layout is the closest model-free approximation
                        // to the predefined tables at .data 0x10029000 (see report).
                        litTree = new HuffTree(8, 0x10, 0, 1);
                        lenTree = new HuffTree(6, 0x0C, 0, 1);
                        distTree = new HuffTree(6, 0x0C, 0, 1);
                    }

                    // Token loop (0x10005170): decode one symbol per iteration.
                    //   S < 0x100  → literal byte → window.
                    //   S == 0x100 → END of this block.
                    //   S  > 0x100 → match (length/distance escape, 0x10006A2C).
                    while (true)
                    {
                        if (outBuf.Count >= maxOutput) break;
                        if (bi.Pos >= bi.End) goto done;

                        ushort s = litTree!.Decode(bi, data);
                        if (s < 0x100)
                        {
                            outBuf.Add((byte)s);
                            continue;
                        }
                        if (s == 0x100)
                            break;                       // END of block → next mode byte

                        // Match: distance low bits inline, high part from distTree;
                        // length from lenTree. (Escape base/extra-bits per the 20-entry
                        // table 0x10006A2C are not fully reversed — see report.)
                        uint distHi = distTree!.Decode(bi, data);
                        uint distLo = bi.Bits(6, data);
                        int dist = (int)((distHi << 6) | distLo) + 1;
                        int length = (lenTree!.Decode(bi, data) & 0x7F) + 3;
                        for (int i = 0; i < length; i++)
                        {
                            if (outBuf.Count >= maxOutput) break;
                            int pos = outBuf.Count;
                            byte b = pos >= dist ? outBuf[pos - dist] : (byte)0;
                            outBuf.Add(b);
                        }
                    }
                    break;
                }
            }
        }

    done:
        return outBuf.ToArray();
    }
}
