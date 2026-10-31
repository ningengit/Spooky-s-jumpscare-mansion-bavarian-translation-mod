using System.Buffers.Binary;
using System.Text;

namespace SJSMHDBavarianPatcher;

/// Fixes blocks Unity can't read in bundles packed by AssetsTools.NET: a block whose LZ4 data happens to be exactly as big as
/// the uncompressed data. AssetsTools decompresses it, but Unity treats "same size" as "stored uncompressed" and uses the
/// compressed bytes as they are (FMOD "Unsupported file or audio format" for sounds in that block). Such blocks are
/// decompressed and stored raw; since the size stays the same, nothing else in the file moves.
internal static class BundleBlockFixer
{
    private const int CompressionMask = 0x3F;
    private const uint BlocksInfoAtTheEnd = 0x80;
    private const uint BlockInfoNeedPaddingAtStart = 0x200;

    /// Returns how many blocks were fixed (0 = the file was left as it is).
    public static int Fix(string bundlePath)
    {
        byte[] file = File.ReadAllBytes(bundlePath);
        int pos = 0;
        string signature = ReadString(file, ref pos);
        if (signature != "UnityFS") return 0;
        uint version = ReadU32(file, ref pos);
        string generation = ReadString(file, ref pos);
        string engine = ReadString(file, ref pos);
        pos += 8;   // total size
        uint compressedInfoSize = ReadU32(file, ref pos);
        uint uncompressedInfoSize = ReadU32(file, ref pos);
        uint flags = ReadU32(file, ref pos);
        if ((flags & BlocksInfoAtTheEnd) != 0) return 0;   // not the layout we write
        if (version >= 7) pos = Align16(pos);

        byte[] info = new byte[uncompressedInfoSize];
        int infoCompression = (int)(flags & CompressionMask);
        if (infoCompression == 0) Array.Copy(file, pos, info, 0, uncompressedInfoSize);
        else if (infoCompression is 2 or 3) Lz4DecodeBlock(file, pos, (int)compressedInfoSize, info);
        else return 0;   // LZMA block info: AssetsTools doesn't write that for LZ4 bundles
        int dataStart = pos + (int)compressedInfoSize;
        if ((flags & BlockInfoNeedPaddingAtStart) != 0) dataStart = Align16(dataStart);

        // block table: 16-byte hash, count, then per block uncompressed size, compressed size, flags
        int p = 16;
        int blockCount = ReadI32(info, ref p);
        int fixedBlocks = 0;
        long dataLength = 0;
        var toDecompress = new List<(long Offset, int Size)>();
        for (int i = 0; i < blockCount; i++)
        {
            uint uncompressed = ReadU32(info, ref p);
            uint compressed = ReadU32(info, ref p);
            ushort blockFlags = BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(p));
            if ((blockFlags & CompressionMask) is 2 or 3 && compressed == uncompressed)
            {
                BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(p), (ushort)(blockFlags & ~CompressionMask));
                toDecompress.Add((dataLength, (int)uncompressed));
                fixedBlocks++;
            }
            p += 2;
            dataLength += compressed;
        }
        if (fixedBlocks == 0) return 0;

        // write it again: same header, block table stored uncompressed (simplest, a few KB), data copied unchanged
        using var ms = new MemoryStream();
        WriteString(ms, signature);
        WriteU32(ms, version);
        WriteString(ms, generation);
        WriteString(ms, engine);
        long sizePosition = ms.Position;
        WriteU64(ms, 0);
        WriteU32(ms, (uint)info.Length);
        WriteU32(ms, (uint)info.Length);
        WriteU32(ms, (flags & ~(uint)CompressionMask) & ~BlockInfoNeedPaddingAtStart);
        if (version >= 7) Pad16(ms);
        ms.Write(info, 0, info.Length);
        byte[] data = new byte[dataLength];
        Array.Copy(file, dataStart, data, 0, dataLength);
        foreach (var (offset, size) in toDecompress)
        {
            byte[] raw = new byte[size];
            Lz4DecodeBlock(file, dataStart + (int)offset, size, raw);
            Array.Copy(raw, 0, data, offset, size);
        }
        ms.Write(data, 0, data.Length);
        long total = ms.Length;
        ms.Position = sizePosition;
        WriteU64(ms, (ulong)total);
        File.WriteAllBytes(bundlePath, ms.ToArray());
        return fixedBlocks;
    }

    // ----- big-endian helpers -----
    private static string ReadString(byte[] b, ref int pos)
    {
        int end = Array.IndexOf(b, (byte)0, pos);
        string s = Encoding.UTF8.GetString(b, pos, end - pos);
        pos = end + 1;
        return s;
    }
    private static uint ReadU32(byte[] b, ref int pos) { uint v = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos)); pos += 4; return v; }
    private static int ReadI32(byte[] b, ref int pos) { int v = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(pos)); pos += 4; return v; }
    private static int Align16(int pos) => (pos + 15) & ~15;
    private static void WriteString(Stream s, string text) { byte[] b = Encoding.UTF8.GetBytes(text); s.Write(b, 0, b.Length); s.WriteByte(0); }
    private static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
    private static void WriteU64(Stream s, ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); s.Write(b); }
    private static void Pad16(Stream s) { while (s.Position % 16 != 0) s.WriteByte(0); }

    /// Plain LZ4 block decoding (the block table is small, so a simple decoder is enough).
    private static void Lz4DecodeBlock(byte[] src, int start, int length, byte[] dst)
    {
        int s = start, end = start + length, d = 0;
        while (s < end)
        {
            int token = src[s++];
            int literals = token >> 4;
            if (literals == 15) { int add; do { add = src[s++]; literals += add; } while (add == 255); }
            Array.Copy(src, s, dst, d, literals);
            s += literals; d += literals;
            if (s >= end) break;
            int offset = src[s] | (src[s + 1] << 8);
            s += 2;
            int match = token & 15;
            if (match == 15) { int add; do { add = src[s++]; match += add; } while (add == 255); }
            match += 4;
            for (int i = 0; i < match; i++, d++) dst[d] = dst[d - offset];
        }
        if (d != dst.Length) throw new InvalidDataException("a bundle block didn't decompress to its expected size");
    }
}
