// RTPatch APPLY-side per-file rolling signature (Pocket Soft custom dual-accumulator).
//
// Ground truth: expapply.dll sub_1000E420 (core) + sub_10001540 (validator), reversed
// from the real v303 patch binary. The applier uses this 10-byte signature to decide
// skip-vs-decode per record: if the file already on disk matches a record's NEW
// (target) signature, sub_1000BED0 returns 30 and the decode is skipped (the file is
// kept as-is). Reproducing that is what makes a chained in-place rebuild byte-exact —
// otherwise a redundant delta (whose OLD state the on-disk file is no longer in) is
// applied against the wrong source and corrupts the file.
//
// 10-byte signature layout (little-endian), exactly as sub_1000E420 reads/writes it:
//   byte 0      : cA = fileLen % 31  (0x1F)   [+ 0x80 "variant" bit when aFlag set]
//   byte 1      : cB = fileLen % 30  (0x1E)
//   bytes 2..5  : A  = u32 LE, masked 0x7FFFFFFF (31-bit)
//   bytes 6..9  : B  = u32 LE, masked 0x3FFFFFFF (30-bit)
//
// Per-byte step (rotate-left-8 inside the reduced bit field):
//   xa = b ^ A;  A = ((xa << 8) | (xa >> 23)) & 0x7FFFFFFF
//   xb = b ^ B;  B = ((xb << 8) | (xb >> 22)) & 0x3FFFFFFF
//
// Verified empirically: computing this over v292 Data\UI.VDK reproduces the NEW
// signature stored in patch 00000293.RTP byte-for-byte (modulo the 0x80 variant bit
// on byte 0 — see Matches).

namespace Ro2.RtPatch;

/// <summary>RTPatch APPLY-side 10-byte rolling file signature (skip-vs-decode check).</summary>
public static class RtpSignature
{
    /// <summary>Number of bytes in a stored signature.</summary>
    public const int Length = 10;

    /// <summary>Compute the 10-byte signature over an entire stream (whole-file block).</summary>
    public static byte[] Compute(Stream s)
    {
        uint a = 0, b = 0;
        long n = 0;
        byte[] buf = new byte[1 << 20];
        int read;
        while ((read = s.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                byte v = buf[i];
                uint xa = (uint)(v ^ a); a = ((xa << 8) | (xa >> 23)) & 0x7FFFFFFFu;
                uint xb = (uint)(v ^ b); b = ((xb << 8) | (xb >> 22)) & 0x3FFFFFFFu;
            }
            n += read;
        }

        var o = new byte[Length];
        o[0] = (byte)(n % 31);
        o[1] = (byte)(n % 30);
        o[2] = (byte)a; o[3] = (byte)(a >> 8); o[4] = (byte)(a >> 16); o[5] = (byte)(a >> 24);
        o[6] = (byte)b; o[7] = (byte)(b >> 8); o[8] = (byte)(b >> 16); o[9] = (byte)(b >> 24);
        return o;
    }

    /// <summary>
    /// Compare a computed signature against a stored one. Byte 0's high bit (0x80) is a
    /// "variant" flag the applier ORs into the stored value at finalize time; it is not
    /// part of the rolling state, so it is ignored in the comparison.
    /// </summary>
    public static bool Matches(ReadOnlySpan<byte> computed, ReadOnlySpan<byte> stored)
    {
        if (computed.Length < Length || stored.Length < Length)
            return false;
        if ((computed[0] & 0x7F) != (stored[0] & 0x7F))
            return false;
        for (int i = 1; i < Length; i++)
            if (computed[i] != stored[i])
                return false;
        return true;
    }
}
