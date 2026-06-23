using System;
using System.Collections.Generic;

namespace Ro2.RtPatch;

/// <summary>
/// SPEC §9 opcode-stream interpreter. Faithful port of rtptool's
/// <c>src/apply.rs</c> <c>run_opcodes</c>. Reconstructs a destination file from
/// a source buffer and a (already decompressed) opcode stream.
///
/// Single-source only: the public surface drops the <c>src_count</c> parameter
/// that apply.rs uses to gate the per-opcode source selector. Multi-source
/// records (SPEC §11) are representable but uncommon and are not supported here;
/// no leading <c>src VLI</c> is read for COPY/STORE.
/// </summary>
public static class RtpOpcodes
{
    // Opcode values from SPEC §9.
    private const byte OpEnd        = 0x01;
    private const byte OpSetSource  = 0x02;
    private const byte OpCopy       = 0x03;
    private const byte OpCopyGap    = 0x04;
    private const byte OpFlush      = 0x05;
    private const byte OpPoke1      = 0x06;
    private const byte OpPoke1xN    = 0x07;
    private const byte OpStore      = 0x08;
    private const byte OpTcopy      = 0x09;
    private const byte OpTcopyGap   = 0x0A;
    private const byte OpZfill      = 0x0B;
    private const byte OpZfillGap   = 0x0C;
    private const byte OpPoke1xNVar = 0x0D;
    private const byte OpPoke1xNB   = 0x0E;
    private const byte OpPoke16xN   = 0x0F;
    private const byte OpPoke32xN   = 0x10;
    private const byte OpFill1      = 0x11;
    private const byte OpFill2      = 0x12;
    private const byte OpFill4      = 0x13;
    private const byte OpFill1Gap   = 0x14;
    private const byte OpFill2Gap   = 0x15;
    private const byte OpFill4Gap   = 0x16;

    /// <summary>
    /// Apply a MODIFY opcode stream to <paramref name="source"/>, returning the
    /// reconstructed destination of exactly <paramref name="dstSize"/> bytes
    /// (zero-initialised output buffer).
    /// </summary>
    public static byte[] Apply(ReadOnlySpan<byte> opcodeStream, byte[] source, int dstSize)
    {
        if (dstSize < 0)
            throw new ArgumentOutOfRangeException(nameof(dstSize), "destination size must be non-negative");

        var r = new OpReader(opcodeStream);
        ReadOnlySpan<byte> src = source;

        // apply.rs uses a growable Vec then truncates to dest_size. We over-size
        // the working buffer to tolerate writes past dstSize that the opcode
        // stream may transiently perform (mirrors the Rust grow()), then trim.
        var output = new List<byte>(dstSize);
        output.AddRange(new byte[dstSize]);

        int outPos = 0;                       // write cursor
        long pokePos = 0;                     // poke cursor (running, signed)
        var gaps = new List<(int Off, int Len)>();
        var templates = new List<(int Off, int Cnt)>();

        while (true)
        {
            if (!r.TryByte(out byte opByte))
                break; // stream exhausted without END (apply.rs: next_opcode -> None)

            switch (opByte)
            {
                case OpEnd:
                    goto done;

                case OpSetSource:
                {
                    _ = r.Vli();          // src index (ignored, single-source)
                    outPos = 0;
                    pokePos = 0;
                    break;
                }

                case OpCopy:
                case OpCopyGap:
                {
                    int advance = opByte == OpCopyGap ? (int)r.Vli() : 0;
                    // (no src VLI: single-source)
                    int srcOff = (int)r.Vli();
                    int cnt = (int)r.Vli();
                    DoCopy(src, output, ref outPos, gaps, advance, srcOff, cnt);
                    break;
                }

                case OpFlush:
                {
                    if (dstSize > outPos)
                        gaps.Add((outPos, dstSize - outPos));
                    foreach (var (off, ln) in gaps)
                    {
                        Grow(output, off + ln);
                        ReadOnlySpan<byte> bytes = r.Bytes(ln);
                        for (int i = 0; i < ln; i++)
                            output[off + i] = bytes[i];
                    }
                    gaps.Clear();
                    break;
                }

                case OpPoke1:
                {
                    long seek = r.Vli();
                    long delta = S8(r.Byte());
                    pokePos += seek;                 // NOTE: no reset for 0x06
                    PokeDelta(output, pokePos, 1, delta);
                    break;
                }

                case OpPoke1xN:
                case OpPoke1xNB:
                {
                    pokePos = 0;
                    long delta = S8(r.Byte());
                    int count = (int)r.Vli();
                    for (int i = 0; i < count; i++)
                    {
                        pokePos += r.Vli();
                        PokeDelta(output, pokePos, 1, delta);
                    }
                    break;
                }

                case OpStore:
                {
                    // (no src VLI: single-source)
                    int srcOff = (int)r.Vli();
                    int cnt = (int)r.Vli();
                    templates.Add((srcOff, cnt));
                    break;
                }

                case OpTcopy:
                case OpTcopyGap:
                {
                    int advance = opByte == OpTcopyGap ? (int)r.Vli() : 0;
                    int idx = (int)r.Vli();
                    if (idx < 0 || idx >= templates.Count)
                        throw new InvalidOperationException($"TCOPY: template index {idx} out of range");
                    var (srcOff, cnt) = templates[idx];
                    DoCopy(src, output, ref outPos, gaps, advance, srcOff, cnt);
                    break;
                }

                case OpZfill:
                case OpZfillGap:
                {
                    int seek = opByte == OpZfillGap ? (int)r.Vli() : 0;
                    int count = (int)r.Vli();
                    DoFill(output, ref outPos, gaps, seek, ReadOnlySpan<byte>.Empty, count);
                    break;
                }

                case OpPoke1xNVar:
                {
                    pokePos = 0;
                    int count = (int)r.Vli();
                    for (int i = 0; i < count; i++)
                    {
                        pokePos += r.Vli();
                        long delta = S8(r.Byte());
                        PokeDelta(output, pokePos, 1, delta);
                    }
                    break;
                }

                case OpPoke16xN:
                {
                    pokePos = 0;
                    ReadOnlySpan<byte> d = r.Bytes(2);
                    long delta = (short)(d[0] | (d[1] << 8));
                    int count = (int)r.Vli();
                    for (int i = 0; i < count; i++)
                    {
                        pokePos += r.Vli();
                        PokeDelta(output, pokePos, 2, delta);
                    }
                    break;
                }

                case OpPoke32xN:
                {
                    pokePos = 0;
                    ReadOnlySpan<byte> d = r.Bytes(4);
                    long delta = (int)(d[0] | (d[1] << 8) | (d[2] << 16) | (d[3] << 24));
                    int count = (int)r.Vli();
                    for (int i = 0; i < count; i++)
                    {
                        pokePos += r.Vli();
                        PokeDelta(output, pokePos, 4, delta);
                    }
                    break;
                }

                case OpFill1:
                case OpFill1Gap:
                {
                    int seek = opByte == OpFill1Gap ? (int)r.Vli() : 0;
                    ReadOnlySpan<byte> pat = r.Bytes(1);
                    int count = (int)r.Vli();
                    DoFill(output, ref outPos, gaps, seek, pat, count);
                    break;
                }

                case OpFill2:
                case OpFill2Gap:
                {
                    int seek = opByte == OpFill2Gap ? (int)r.Vli() : 0;
                    ReadOnlySpan<byte> pat = r.Bytes(2);
                    int count = (int)r.Vli();
                    DoFill(output, ref outPos, gaps, seek, pat, count);
                    break;
                }

                case OpFill4:
                case OpFill4Gap:
                {
                    int seek = opByte == OpFill4Gap ? (int)r.Vli() : 0;
                    ReadOnlySpan<byte> pat = r.Bytes(4);
                    int count = (int)r.Vli();
                    DoFill(output, ref outPos, gaps, seek, pat, count);
                    break;
                }

                default:
                    throw new InvalidOperationException(
                        $"unknown opcode 0x{opByte:x2} at stream offset {r.Pos - 1}");
            }
        }

    done:
        // apply.rs: output.truncate(dest_size).
        if (output.Count > dstSize)
            output.RemoveRange(dstSize, output.Count - dstSize);
        else if (output.Count < dstSize)
            output.AddRange(new byte[dstSize - output.Count]);

        return output.ToArray();
    }

    // ---- helpers (mirror apply.rs nested fns) --------------------------------

    private static void Grow(List<byte> output, int n)
    {
        if (n > output.Count)
            output.AddRange(new byte[n - output.Count]);
    }

    private static void DoCopy(ReadOnlySpan<byte> src, List<byte> output, ref int outPos,
        List<(int Off, int Len)> gaps, int advance, int srcOff, int cnt)
    {
        if (advance > 0)
        {
            gaps.Add((outPos, advance));
            outPos += advance;
        }
        Grow(output, outPos + cnt);
        // Clamp the copy to the available source bytes (apply.rs: src_end.min(src.len)).
        int srcEnd = Math.Min(srcOff + cnt, src.Length);
        int copyLen = Math.Max(0, srcEnd - srcOff);
        if (copyLen > 0 && srcOff >= 0)
        {
            for (int i = 0; i < copyLen; i++)
                output[outPos + i] = src[srcOff + i];
        }
        outPos += cnt;
    }

    private static void DoFill(List<byte> output, ref int outPos,
        List<(int Off, int Len)> gaps, int seek, ReadOnlySpan<byte> pattern, int count)
    {
        if (seek > 0)
        {
            gaps.Add((outPos, seek));
            outPos += seek;
        }
        Grow(output, outPos + count);
        // Output buffer is zero-initialised; an all-zero pattern (incl. ZFILL's
        // empty pattern) is a no-op, matching apply.rs.
        if (!IsEmptyOrAllZero(pattern))
        {
            int plen = pattern.Length;
            for (int i = 0; i < count; i++)
                output[outPos + i] = pattern[i % plen];
        }
        outPos += count;
    }

    private static bool IsEmptyOrAllZero(ReadOnlySpan<byte> p)
    {
        if (p.IsEmpty) return true;
        foreach (byte b in p)
            if (b != 0) return false;
        return true;
    }

    private static void PokeDelta(List<byte> output, long pos, int width, long delta)
    {
        if (pos < 0) return;
        int p = checked((int)pos);
        Grow(output, p + width);
        long cur = width switch
        {
            1 => (sbyte)output[p],
            2 => (short)(output[p] | (output[p + 1] << 8)),
            4 => (int)(output[p] | (output[p + 1] << 8) | (output[p + 2] << 16) | (output[p + 3] << 24)),
            _ => 0,
        };
        if (width != 1 && width != 2 && width != 4) return;
        long v = unchecked(cur + delta);
        switch (width)
        {
            case 1:
                output[p] = (byte)v;
                break;
            case 2:
                output[p] = (byte)v;
                output[p + 1] = (byte)(v >> 8);
                break;
            case 4:
                output[p] = (byte)v;
                output[p + 1] = (byte)(v >> 8);
                output[p + 2] = (byte)(v >> 16);
                output[p + 3] = (byte)(v >> 24);
                break;
        }
    }

    private static long S8(byte b) => b >= 128 ? b - 256 : b;

    // ---- opcode-stream reader (VLI per SPEC §5, same as the codec) -----------

    private ref struct OpReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public OpReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        public int Pos => _pos;

        public bool TryByte(out byte b)
        {
            if (_pos >= _data.Length)
            {
                b = 0;
                return false;
            }
            b = _data[_pos++];
            return true;
        }

        public byte Byte()
        {
            if (_pos >= _data.Length)
                throw new InvalidOperationException("opcode stream truncated");
            return _data[_pos++];
        }

        public ReadOnlySpan<byte> Bytes(int n)
        {
            if (n < 0 || _pos + n > _data.Length)
                throw new InvalidOperationException("opcode stream truncated");
            var s = _data.Slice(_pos, n);
            _pos += n;
            return s;
        }

        /// <summary>
        /// Variable-length integer (SPEC §5). Lead byte holds sign (bit 7) and a
        /// unary continuation count starting at bit 6; continuation bytes are the
        /// low little-endian bytes; the lead byte's remaining low bits are the
        /// high part.
        /// </summary>
        public long Vli()
        {
            ulong b = Byte();
            bool sign = (b & 0x80) != 0;
            int count = 0;
            ulong mask = 0x40;
            while (mask > 0 && (b & mask) != 0)
            {
                count++;
                mask >>= 1;
            }

            ulong val;
            if (count == 0)
            {
                val = b & 0x3F;
            }
            else
            {
                ulong extractMask = (0x40UL >> count) - 1;
                ulong high = b & extractMask;
                ulong tail = 0;
                for (int i = 0; i < count; i++)
                    tail |= (ulong)Byte() << (8 * i);
                val = (high << (8 * count)) | tail;
            }

            return sign ? -(long)val : (long)val;
        }
    }
}
