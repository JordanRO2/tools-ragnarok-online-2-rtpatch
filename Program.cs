// rtpatch — CLI for RO2 RTPatch v1.01 ("KX") patch files (*.RTP).
//
// Subcommands:
//   inspect <file.rtp>
//   list    <file.rtp>
//   extract <file.rtp> --out <dir> [--file <substr>]
//   apply   <file.rtp> --source <dir> --output <dir> [--file <substr>] [--no-checksum]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ro2.RtPatch;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                Usage();
                return 1;
            }

            string cmd = args[0].ToLowerInvariant();
            string[] rest = args.Skip(1).ToArray();

            return cmd switch
            {
                "inspect" => CmdInspect(rest),
                "list"    => CmdList(rest),
                "extract" => CmdExtract(rest),
                "apply"   => CmdApply(rest),
                "build"   => CmdBuild(rest),
                "decode-test" => CmdDecodeTest(rest),
                "-h" or "--help" or "help" => Usage(),
                _ => UnknownCommand(cmd),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"error: unknown command '{cmd}'");
        Usage();
        return 1;
    }

    private static int Usage()
    {
        Console.WriteLine(
@"rtpatch — RO2 RTPatch (.RTP) tool

Usage:
  rtpatch inspect <file.rtp>
  rtpatch list    <file.rtp>
  rtpatch extract <file.rtp> --out <dir> [--file <substr>]
  rtpatch apply   <file.rtp> --source <dir> --output <dir> [--file <substr>] [--no-checksum]
  rtpatch build   <out.rtp> --add <relpath>=<contentfile> [--add ...]   (create whole-file patches)");
        return 0;
    }

    // ── build (create a .RTP that adds whole files, store-mode payloads) ───────
    private static int CmdBuild(string[] args)
    {
        string outFile = RequirePositional(args, "output.rtp");
        var builder = new RtpBuilder();
        int added = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != "--add" || i + 1 >= args.Length) continue;
            string spec = args[++i];
            int eq = spec.IndexOf('=');
            if (eq < 0) throw new ArgumentException($"--add expects <relpath>=<contentfile>, got '{spec}'");
            string rel = spec[..eq].Replace('/', '\\').Trim('\\');
            byte[] content = File.ReadAllBytes(spec[(eq + 1)..]);
            int slash = rel.LastIndexOf('\\');
            string dir = slash >= 0 ? rel[..slash] : string.Empty;
            string name = slash >= 0 ? rel[(slash + 1)..] : rel;
            builder.AddFile(dir, name, content);
            Console.WriteLine($"  + {rel}  ({content.Length} bytes)");
            added++;
        }
        if (added == 0)
            throw new ArgumentException("nothing to build: pass --add <relpath>=<contentfile> (repeatable)");

        byte[] rtp = builder.Build();
        File.WriteAllBytes(outFile, rtp);
        Console.WriteLine($"[built] {outFile}: {rtp.Length} bytes, {added} file(s)");
        return 0;
    }

    // ── inspect ──────────────────────────────────────────────────────────────
    private static int CmdInspect(string[] args)
    {
        string file = RequirePositional(args, "file.rtp");
        RtpPatch patch = LoadPatch(file);

        Console.WriteLine($"File       : {Path.GetFullPath(file)}");
        Console.WriteLine($"Size       : {patch.Raw.Length} bytes");
        Console.WriteLine($"Version    : 0x{patch.Version:X4}");
        Console.WriteLine($"Flags      : 0x{patch.Flags:X4}");
        Console.WriteLine($"FormatTag  : 0x{patch.FormatTag:X}");
        Console.WriteLine($"ExtraMode  : {patch.ExtraMode}");
        Console.WriteLine($"OldCount   : {patch.OldCount}");
        Console.WriteLine($"NewCount   : {patch.NewCount}");
        Console.WriteLine($"MaxCount   : {patch.MaxCount}");
        Console.WriteLine($"OldBytes   : {patch.OldFilesetBytes}");
        Console.WriteLine($"NewBytes   : {patch.NewFilesetBytes}");
        Console.WriteLine($"Dirs       : {patch.Dirs.Count}");
        foreach (string d in patch.Dirs)
            Console.WriteLine($"             - {d}");
        Console.WriteLine($"Records    : {patch.Records.Count}");

        var byType = patch.Records
            .GroupBy(r => r.Type)
            .OrderBy(g => g.Key.ToString());
        foreach (var g in byType)
            Console.WriteLine($"             {g.Key,-8}: {g.Count()}");

        return 0;
    }

    // ── list ─────────────────────────────────────────────────────────────────
    private static int CmdList(string[] args)
    {
        string file = RequirePositional(args, "file.rtp");
        RtpPatch patch = LoadPatch(file);

        Console.WriteLine($"{"type",-8} {"dstSize",12} {"payloadLen",12}  path");
        Console.WriteLine(new string('-', 8 + 1 + 12 + 1 + 12 + 2 + 40));
        foreach (RtpRecord r in patch.Records)
        {
            Console.WriteLine($"{r.Type,-8} {r.DstSize,12} {r.PayloadLength,12}  {r.Path}");
        }
        Console.WriteLine();
        Console.WriteLine($"{patch.Records.Count} record(s).");
        return 0;
    }

    // ── extract ──────────────────────────────────────────────────────────────
    private static int CmdExtract(string[] args)
    {
        string file = RequirePositional(args, "file.rtp");
        var opts = ParseOptions(args);
        string outDir = RequireOption(opts, "out");
        string? filter = GetOption(opts, "file");

        RtpPatch patch = LoadPatch(file);
        Directory.CreateDirectory(outDir);

        int ok = 0, failed = 0, skipped = 0;
        foreach (RtpRecord r in patch.Records)
        {
            if (r.Type != RecordType.Modify)
                continue;
            if (filter != null && !r.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (r.PayloadLength == 0)
            {
                Console.WriteLine($"[skip] {r.Path}: empty payload");
                skipped++;
                continue;
            }

            string name = SafeFileName(r.Path) + ".opcodes";
            string outPath = Path.Combine(outDir, name);
            try
            {
                ReadOnlySpan<byte> payload = patch.Raw.AsSpan(r.PayloadOffset, r.PayloadLength);
                byte[] opcodes = RtpCodec.DecompressDiff(payload);
                File.WriteAllBytes(outPath, opcodes);
                Console.WriteLine($"[ok]   {r.Path}: {r.PayloadLength} -> {opcodes.Length} opcode bytes  ({name})");
                ok++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[fail] {r.Path}: {ex.GetType().Name}: {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"extract: {ok} ok, {failed} failed, {skipped} skipped.");
        return failed > 0 ? 3 : 0;
    }

    // ── apply ────────────────────────────────────────────────────────────────
    private static int CmdApply(string[] args)
    {
        string file = RequirePositional(args, "file.rtp");
        var opts = ParseOptions(args);
        string srcDir = RequireOption(opts, "source");
        string outDir = RequireOption(opts, "output");
        string? filter = GetOption(opts, "file");
        bool noChecksum = opts.ContainsKey("no-checksum"); // --no-checksum disables the skip-when-target-matches check

        RtpPatch patch = LoadPatch(file);

        int ok = 0, failed = 0, skipped = 0, missing = 0, deleted = 0;
        foreach (RtpRecord r in patch.Records)
        {
            // MODIFY carries a delta against the old file; NEW carries the whole new
            // file; DELETE removes a file. Everything else is not handled here.
            if (r.Type != RecordType.Modify && r.Type != RecordType.New && r.Type != RecordType.Delete)
                continue;
            if (filter != null && !r.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            string rel = r.Path.Replace('\\', Path.DirectorySeparatorChar);
            string srcPath = Path.Combine(srcDir, rel);
            string dstPath = Path.Combine(outDir, rel);

            // DELETE: the file is removed in the new version. Drop it from the output tree
            // if present (in-place patching); never produce it.
            if (r.Type == RecordType.Delete)
            {
                if (File.Exists(dstPath)) File.Delete(dstPath);
                Console.WriteLine($"[del]  {r.Path}: removed");
                deleted++;
                continue;
            }

            // A record's delta is applied against whatever is on disk at the target
            // path: for MODIFY that is the old file (required). A NEW record is the
            // new-side fileset entry — its payload may be a true create (no source) OR
            // a delta against the file already present (e.g. a prior patch added it),
            // so decode against the existing file when present and an empty source
            // otherwise. This mirrors the official applier's source selection.
            bool haveSrc = File.Exists(srcPath);
            if (r.Type == RecordType.Modify && !haveSrc)
            {
                Console.WriteLine($"[miss] {r.Path}: source not found at {srcPath}");
                missing++;
                continue;
            }
            if (r.PayloadLength == 0)
            {
                Console.WriteLine($"[skip] {r.Path}: empty payload");
                skipped++;
                continue;
            }

            // Skip-when-source-matches-target: expapply.dll computes the on-disk file's
            // rolling signature and, if it already equals the record's NEW signature,
            // returns 30 and keeps the file as-is (no decode). A redundant delta encodes
            // an OLD->NEW transition the on-disk file is no longer the OLD of, so applying
            // it would corrupt the file; skipping is what keeps a chained rebuild exact.
            if (haveSrc && r.NewSignature != null && !noChecksum)
            {
                byte[] onDisk;
                using (Stream s = File.OpenRead(srcPath))
                    onDisk = RtpSignature.Compute(s);
                if (RtpSignature.Matches(onDisk, r.NewSignature))
                {
                    // The file already equals the target. In-place (src==dst): keep it.
                    // Distinct output tree: copy the current file through unchanged.
                    if (!PathsEqual(srcPath, dstPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                        File.Copy(srcPath, dstPath, overwrite: true);
                    }
                    Console.WriteLine($"[skip] {r.Path}: already at target (signature match)");
                    skipped++;
                    continue;
                }
            }

            // Duplicate-content skip: a NEW record whose target file is absent but whose
            // content already exists elsewhere in the tree (same file name, identical NEW
            // signature). expapply.dll resolves the record's source from the fileset to that
            // existing copy, finds it already equals NEW, and skips — so the duplicate
            // destination is never created (e.g. a patch carries a CREATECHARACTER copy of a
            // file that already lives under SELECTCHARACTER). Only skip on a real content
            // match, so distinct same-named files (e.g. CREATE vs SELECT caustic) still build.
            if (!haveSrc && r.Type == RecordType.New && r.NewSignature != null && !noChecksum)
            {
                string? dup = FindDuplicateBySignature(srcDir, Path.GetFileName(rel), srcPath, r.NewSignature);
                if (dup != null)
                {
                    Console.WriteLine($"[skip] {r.Path}: duplicate of {Path.GetRelativePath(srcDir, dup)} (signature match)");
                    skipped++;
                    continue;
                }
            }

            try
            {
                byte[] result;
                long srcLen;
                using (Stream src = haveSrc
                    ? File.OpenRead(srcPath)
                    : new MemoryStream(Array.Empty<byte>(), writable: false))
                {
                    srcLen = src.Length;
                    var payload = patch.Raw.AsMemory(r.PayloadOffset, r.PayloadLength);
                    // The decoder self-terminates at the real output size; pass a generous upper
                    // bound (record DstSize is unreliable for >256 MB files). The result is trimmed.
                    long cap = srcLen + (long)r.PayloadLength * 64 + (1 << 24);
                    result = FixedHuff.DecodeRecord(payload, 0, r.PayloadLength, src, srcLen, cap);
                }
                // The source handle is closed above, so overwriting is safe even when the
                // output path equals the source path (in-place chained patching).
                Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                File.WriteAllBytes(dstPath, result);
                string kind = r.Type == RecordType.New ? (haveSrc ? "new* " : "new ") : "";
                Console.WriteLine($"[ok]   {kind}{r.Path}: {srcLen} -> {result.Length} bytes");
                ok++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[fail] {r.Path}: {ex.GetType().Name}: {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"apply: {ok} ok, {deleted} deleted, {failed} failed, {skipped} skipped, {missing} missing source.");
        return failed > 0 ? 3 : 0;
    }

    // ── decode-test (validate DecodeRecord against an oracle prefix) ──────────
    private static int CmdDecodeTest(string[] args)
    {
        string file = RequirePositional(args, "file.rtp");
        var opts = ParseOptions(args);
        string srcPath = RequireOption(opts, "source");
        string oraclePath = RequireOption(opts, "oracle");
        string? filter = GetOption(opts, "file");
        long cap = GetOption(opts, "cap") is string c ? long.Parse(c) : 8L * 1024 * 1024;

        RtpPatch patch = LoadPatch(file);
        RtpRecord? rec = patch.Records.FirstOrDefault(r => r.Type == RecordType.Modify
            && (filter == null || r.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        if (rec == null) { Console.Error.WriteLine("no matching MODIFY record"); return 1; }

        Console.WriteLine($"record : {rec.Path}  payload @{rec.PayloadOffset} len {rec.PayloadLength}");
        Console.WriteLine($"head   : {BitConverter.ToString(patch.Raw, rec.PayloadOffset, Math.Min(16, rec.PayloadLength))}");
        if (GetOption(opts, "at") is string atStr)
        {
            int at = int.Parse(atStr);
            Console.WriteLine($"bytes@{at}: {BitConverter.ToString(patch.Raw, rec.PayloadOffset + at, Math.Min(24, rec.PayloadLength - at))}");
            return 0;
        }

        using var src = File.OpenRead(srcPath);
        long srcLen = src.Length;
        Console.WriteLine($"source : {srcPath}  {srcLen} bytes");

        var payload = patch.Raw.AsMemory(rec.PayloadOffset, rec.PayloadLength);
        FixedHuff.LastStopReason = null;
        byte[] decoded;
        try { decoded = FixedHuff.DecodeRecord(payload, 0, rec.PayloadLength, src, srcLen, cap); }
        catch (Exception ex) { Console.WriteLine($"DECODE THREW: {ex.GetType().Name}: {ex.Message}"); return 3; }
        Console.WriteLine($"decoded: {decoded.Length} bytes (cap {cap})"
            + (FixedHuff.LastStopReason != null ? $"  [stopped: {FixedHuff.LastStopReason}]" : ""));

        using var oracle = File.OpenRead(oraclePath);
        byte[] exp = new byte[decoded.Length];
        int off = 0; while (off < exp.Length) { int g = oracle.Read(exp, off, exp.Length - off); if (g <= 0) break; off += g; }
        int lim = Math.Min(off, decoded.Length);
        int match = 0;
        while (match < lim && decoded[match] == exp[match]) match++;
        if (match == lim && lim == decoded.Length)
            Console.WriteLine($"MATCH all {match} bytes  OK");
        else
        {
            Console.WriteLine($"FIRST MISMATCH @ {match} (0x{match:X})  (matched {match}/{lim})");
            int s = Math.Max(0, match - 4);
            Console.WriteLine($"  decoded[{s}..]: {BitConverter.ToString(decoded, s, Math.Min(20, decoded.Length - s))}");
            Console.WriteLine($"  oracle [{s}..]: {BitConverter.ToString(exp, s, Math.Min(20, exp.Length - s))}");
        }
        return 0;
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static RtpPatch LoadPatch(string file)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException($"file not found: {file}");
        byte[] data = File.ReadAllBytes(file);
        return RtpContainer.Parse(data);
    }

    /// <summary>First non-option argument.</summary>
    private static string RequirePositional(string[] args, string what)
    {
        foreach (string a in args)
            if (!a.StartsWith("--", StringComparison.Ordinal))
                return a;
        throw new ArgumentException($"missing required argument: <{what}>");
    }

    /// <summary>Parse "--key value" / "--flag" pairs. Flags (no following value) map to "".</summary>
    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
                continue;
            string key = a[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                opts[key] = args[i + 1];
                i++;
            }
            else
            {
                opts[key] = string.Empty;
            }
        }
        return opts;
    }

    private static string RequireOption(Dictionary<string, string> opts, string key)
    {
        if (opts.TryGetValue(key, out string? v) && !string.IsNullOrEmpty(v))
            return v;
        throw new ArgumentException($"missing required option: --{key} <value>");
    }

    private static string? GetOption(Dictionary<string, string> opts, string key)
        => opts.TryGetValue(key, out string? v) && !string.IsNullOrEmpty(v) ? v : null;

    /// <summary>True when two paths resolve to the same file (in-place patching case).</summary>
    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Find a file (other than <paramref name="excludePath"/>) under <paramref name="root"/> with the
    /// given file name whose rolling signature equals <paramref name="sig"/>. Used to detect a NEW
    /// record that duplicates content already present in the tree, which the official applier skips.
    /// Returns the matching path, or null if none.
    /// </summary>
    private static string? FindDuplicateBySignature(string root, string fileName, string excludePath, byte[] sig)
    {
        IEnumerable<string> candidates;
        try { candidates = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories); }
        catch { return null; }

        foreach (string cand in candidates)
        {
            if (PathsEqual(cand, excludePath))
                continue;
            try
            {
                byte[] candSig;
                using (Stream s = File.OpenRead(cand))
                    candSig = RtpSignature.Compute(s);
                if (RtpSignature.Matches(candSig, sig))
                    return cand;
            }
            catch { /* unreadable candidate — ignore */ }
        }
        return null;
    }

    /// <summary>Flatten a relative path into a single safe file name for the .opcodes dump.</summary>
    private static string SafeFileName(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (char c in path)
            sb.Append(c is '\\' or '/' or ':' ? '_' : c);
        string s = sb.ToString();
        foreach (char bad in Path.GetInvalidFileNameChars())
            s = s.Replace(bad, '_');
        return s;
    }
}
