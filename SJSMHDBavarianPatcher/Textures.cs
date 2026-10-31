using AssetsTools.NET;
using StbImageSharp;

namespace SJSMHDBavarianPatcher;

public sealed class EncodedTexture
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int MipCount { get; init; }
    public int Format { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public string Summary { get; init; } = "";
}

/// <summary>
/// Turns a PNG into Unity texture data. The format of the ORIGINAL texture is kept where possible
/// (DXT1, DXT5, RGBA32, ARGB32, BGRA32, RGB24), so the replacement is about as big as the original.
/// Any other original format is replaced by RGBA32, which is larger.
/// </summary>
public static class TextureImporter
{
    // Unity TextureFormat values
    private const int RGB24 = 3, RGBA32 = 4, ARGB32 = 5, DXT1 = 10, DXT5 = 12, BGRA32 = 14;

    public static EncodedTexture Encode(AssetTypeValueField tex, string pngPath)
    {
        if (!File.Exists(pngPath))
            throw new ModException($"image file not found: {pngPath}");

        ImageResult img;
        try
        {
            using var fs = File.OpenRead(pngPath);
            img = ImageResult.FromStream(fs, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex)
        {
            throw new ModException($"{Path.GetFileName(pngPath)} could not be read as an image: {ex.Message}");
        }

        int width = img.Width, height = img.Height;

        AssetTypeValueField Need(string path)
        {
            AssetTypeValueField f = tex[path];
            if (f.IsDummy) throw new ModException($"the Texture2D has no field '{path}'");
            return f;
        }

        Need("image data");
        int oldWidth = (int)FieldNumbers.Get(Need("m_Width"));
        int oldHeight = (int)FieldNumbers.Get(Need("m_Height"));
        int oldFormat = (int)FieldNumbers.Get(Need("m_TextureFormat"));
        int oldMips = tex["m_MipCount"].IsDummy ? 1 : (int)FieldNumbers.Get(tex["m_MipCount"]);

        int format = ChooseFormat(oldFormat, width, height, out string note);

        // Unity stores the rows bottom-to-top, PNGs are top-to-bottom
        byte[] level0 = new byte[width * height * 4];
        int stride = width * 4;
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(img.Data, y * stride, level0, (height - 1 - y) * stride, stride);

        // keep mipmaps if the original texture had them
        var levels = new List<(byte[] Rgba, int W, int H)> { (level0, width, height) };
        if (oldMips > 1)
        {
            var (prev, cw, ch) = levels[0];
            while (cw > 1 || ch > 1)
            {
                int nw = Math.Max(1, cw / 2), nh = Math.Max(1, ch / 2);
                byte[] next = new byte[nw * nh * 4];
                for (int y = 0; y < nh; y++)
                {
                    int y0 = Math.Min(y * 2, ch - 1), y1 = Math.Min(y * 2 + 1, ch - 1);
                    for (int x = 0; x < nw; x++)
                    {
                        int x0 = Math.Min(x * 2, cw - 1), x1 = Math.Min(x * 2 + 1, cw - 1);
                        for (int c = 0; c < 4; c++)
                        {
                            int sum = prev[(y0 * cw + x0) * 4 + c] + prev[(y0 * cw + x1) * 4 + c]
                                    + prev[(y1 * cw + x0) * 4 + c] + prev[(y1 * cw + x1) * 4 + c];
                            next[(y * nw + x) * 4 + c] = (byte)((sum + 2) / 4);
                        }
                    }
                }
                levels.Add((next, nw, nh));
                prev = next; cw = nw; ch = nh;
            }
        }

        using var ms = new MemoryStream();
        foreach (var (rgba, w, h) in levels)
        {
            byte[] encoded = EncodeLevel(rgba, w, h, format);
            ms.Write(encoded, 0, encoded.Length);
        }
        byte[] data = ms.ToArray();

        string summary = $"{width}x{height} {FormatName(format)}, {levels.Count} mip level(s), {data.Length / 1024} KB";
        if (note.Length > 0) summary += "  NOTE: " + note;
        if (width != oldWidth || height != oldHeight)
            summary += $"  WARNING: the original is {oldWidth}x{oldHeight}; sprites or UVs that depend on the size may look wrong";

        return new EncodedTexture
        {
            Width = width, Height = height, MipCount = levels.Count, Format = format, Data = data, Summary = summary
        };
    }

    /// <summary>Sets size, format, mip count and total size (not the pixel data).</summary>
    public static void SetFields(AssetTypeValueField tex, EncodedTexture enc)
    {
        FieldNumbers.Set(tex["m_Width"], enc.Width);
        FieldNumbers.Set(tex["m_Height"], enc.Height);
        FieldNumbers.Set(tex["m_TextureFormat"], enc.Format);
        if (!tex["m_MipCount"].IsDummy) FieldNumbers.Set(tex["m_MipCount"], enc.MipCount);
        if (!tex["m_CompleteImageSize"].IsDummy) FieldNumbers.Set(tex["m_CompleteImageSize"], enc.Data.Length);
    }

    /// <summary>Stores the pixels inside the assets file and clears the streamed-data reference.</summary>
    public static void SetInline(AssetTypeValueField tex, EncodedTexture enc)
    {
        tex["image data"].AsByteArray = enc.Data;

        if (!tex["m_StreamData"].IsDummy)
        {
            if (!tex["m_StreamData.path"].IsDummy) tex["m_StreamData.path"].AsString = "";
            if (!tex["m_StreamData.offset"].IsDummy) FieldNumbers.Set(tex["m_StreamData.offset"], 0);
            if (!tex["m_StreamData.size"].IsDummy) FieldNumbers.Set(tex["m_StreamData.size"], 0);
        }
    }

    // ------------------------------------------------------------------ formats

    private static int ChooseFormat(int oldFormat, int w, int h, out string note)
    {
        note = "";
        switch (oldFormat)
        {
            case RGBA32:
            case ARGB32:
            case BGRA32:
            case RGB24:
                return oldFormat;
            case DXT1:
            case DXT5:
                if (w % 4 == 0 && h % 4 == 0) return oldFormat;
                note = "DXT needs a width and height that are multiples of 4, stored as RGBA32 (larger)";
                return RGBA32;
            default:
                note = $"the original format ({oldFormat}) cannot be matched, stored as RGBA32 (larger)";
                return RGBA32;
        }
    }

    private static string FormatName(int format) => format switch
    {
        RGB24 => "RGB24", RGBA32 => "RGBA32", ARGB32 => "ARGB32", BGRA32 => "BGRA32",
        DXT1 => "DXT1", DXT5 => "DXT5", _ => "format " + format
    };

    private static byte[] EncodeLevel(byte[] rgba, int w, int h, int format)
    {
        int count = w * h;
        switch (format)
        {
            case RGBA32:
                return rgba;

            case ARGB32:
            {
                byte[] o = new byte[count * 4];
                for (int i = 0; i < count; i++)
                {
                    o[i * 4] = rgba[i * 4 + 3];
                    o[i * 4 + 1] = rgba[i * 4];
                    o[i * 4 + 2] = rgba[i * 4 + 1];
                    o[i * 4 + 3] = rgba[i * 4 + 2];
                }
                return o;
            }

            case BGRA32:
            {
                byte[] o = new byte[count * 4];
                for (int i = 0; i < count; i++)
                {
                    o[i * 4] = rgba[i * 4 + 2];
                    o[i * 4 + 1] = rgba[i * 4 + 1];
                    o[i * 4 + 2] = rgba[i * 4];
                    o[i * 4 + 3] = rgba[i * 4 + 3];
                }
                return o;
            }

            case RGB24:
            {
                byte[] o = new byte[count * 3];
                for (int i = 0; i < count; i++)
                {
                    o[i * 3] = rgba[i * 4];
                    o[i * 3 + 1] = rgba[i * 4 + 1];
                    o[i * 3 + 2] = rgba[i * 4 + 2];
                }
                return o;
            }

            case DXT1: return DxtEncoder.Encode(rgba, w, h, false);
            case DXT5: return DxtEncoder.Encode(rgba, w, h, true);
            default: throw new ModException("internal error: unknown texture format " + format);
        }
    }
}

/// <summary>
/// Simple block compression for DXT1 (BC1) and DXT5 (BC3): endpoints come from the colour bounding box of each
/// 4x4 block. Good enough for UI and sprite textures; a dedicated encoder (e.g. in Unity) can look slightly better.
/// </summary>
internal static class DxtEncoder
{
    public static byte[] Encode(byte[] rgba, int w, int h, bool dxt5)
    {
        int blocksX = Math.Max(1, (w + 3) / 4), blocksY = Math.Max(1, (h + 3) / 4);
        int blockSize = dxt5 ? 16 : 8;
        byte[] output = new byte[blocksX * blocksY * blockSize];

        int pos = 0;
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int[] r = new int[16], g = new int[16], b = new int[16], a = new int[16];
                for (int i = 0; i < 16; i++)
                {
                    int x = Math.Min(bx * 4 + (i & 3), w - 1);
                    int y = Math.Min(by * 4 + (i >> 2), h - 1);
                    int p = (y * w + x) * 4;
                    r[i] = rgba[p]; g[i] = rgba[p + 1]; b[i] = rgba[p + 2]; a[i] = rgba[p + 3];
                }

                if (dxt5)
                {
                    EncodeAlphaBlock(a, output, pos);
                    EncodeColorBlock(r, g, b, a, false, output, pos + 8);
                }
                else
                {
                    EncodeColorBlock(r, g, b, a, true, output, pos);
                }
                pos += blockSize;
            }
        }
        return output;
    }

    private static void EncodeAlphaBlock(int[] a, byte[] output, int pos)
    {
        int min = 255, max = 0;
        for (int i = 0; i < 16; i++)
        {
            if (a[i] < min) min = a[i];
            if (a[i] > max) max = a[i];
        }

        int a0 = max, a1 = min; // a0 > a1: eight interpolated values
        int[] palette = new int[8];
        palette[0] = a0;
        palette[1] = a1;
        for (int k = 1; k <= 6; k++) palette[1 + k] = ((7 - k) * a0 + k * a1) / 7;

        ulong bits = 0;
        if (a0 != a1)
        {
            for (int i = 0; i < 16; i++)
            {
                int best = 0, bestDist = int.MaxValue;
                for (int j = 0; j < 8; j++)
                {
                    int d = Math.Abs(a[i] - palette[j]);
                    if (d < bestDist) { bestDist = d; best = j; }
                }
                bits |= (ulong)best << (3 * i);
            }
        }

        output[pos] = (byte)a0;
        output[pos + 1] = (byte)a1;
        for (int i = 0; i < 6; i++) output[pos + 2 + i] = (byte)(bits >> (8 * i));
    }

    private static void EncodeColorBlock(int[] r, int[] g, int[] b, int[] a, bool alphaCutout, byte[] output, int pos)
    {
        bool transparent = false;
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0, opaque = 0;
        for (int i = 0; i < 16; i++)
        {
            if (alphaCutout && a[i] < 128) { transparent = true; continue; }
            opaque++;
            if (r[i] < minR) minR = r[i];
            if (g[i] < minG) minG = g[i];
            if (b[i] < minB) minB = b[i];
            if (r[i] > maxR) maxR = r[i];
            if (g[i] > maxG) maxG = g[i];
            if (b[i] > maxB) maxB = b[i];
        }

        if (opaque == 0)
        {
            // fully transparent block: three-colour mode, every pixel uses index 3
            WriteColorBlock(output, pos, 0, 0, 0xFFFFFFFFu);
            return;
        }

        // the endpoints are two opposite corners of the colour bounding box; pick the diagonal that follows the data
        // (otherwise colours that change in opposite directions, e.g. yellow next to blue, fit very badly)
        int midR = (minR + maxR) / 2, midG = (minG + maxG) / 2, midB = (minB + maxB) / 2;
        int covR = 0, covG = 0;
        for (int i = 0; i < 16; i++)
        {
            if (alphaCutout && a[i] < 128) continue;
            covR += (r[i] - midR) * (b[i] - midB);
            covG += (g[i] - midG) * (b[i] - midB);
        }

        // pull the endpoints in a little, which reduces the error for typical blocks
        int insetR = (maxR - minR) >> 4, insetG = (maxG - minG) >> 4, insetB = (maxB - minB) >> 4;
        maxR -= insetR; maxG -= insetG; maxB -= insetB;
        minR += insetR; minG += insetG; minB += insetB;

        int e0R = maxR, e0G = maxG, e0B = maxB, e1R = minR, e1G = minG, e1B = minB;
        if (covR < 0) (e0R, e1R) = (e1R, e0R);
        if (covG < 0) (e0G, e1G) = (e1G, e0G);

        int c0 = To565(e0R, e0G, e0B);
        int c1 = To565(e1R, e1G, e1B);

        if (transparent)
        {
            // c0 <= c1 selects the three-colour mode, index 3 means transparent
            if (c0 > c1) (c0, c1) = (c1, c0);
        }
        else
        {
            // c0 > c1 selects the four-colour mode
            if (c0 < c1) (c0, c1) = (c1, c0);
        }

        if (c0 == c1 && !transparent)
        {
            WriteColorBlock(output, pos, c0, c1, 0);
            return;
        }

        int[,] palette = new int[4, 3];
        Expand565(c0, out palette[0, 0], out palette[0, 1], out palette[0, 2]);
        Expand565(c1, out palette[1, 0], out palette[1, 1], out palette[1, 2]);
        int colors = transparent ? 3 : 4;
        for (int k = 0; k < 3; k++)
        {
            if (transparent)
            {
                palette[2, k] = (palette[0, k] + palette[1, k]) / 2;
            }
            else
            {
                palette[2, k] = (2 * palette[0, k] + palette[1, k]) / 3;
                palette[3, k] = (palette[0, k] + 2 * palette[1, k]) / 3;
            }
        }

        uint indices = 0;
        for (int i = 0; i < 16; i++)
        {
            uint best = 0;
            if (transparent && a[i] < 128)
            {
                best = 3;
            }
            else
            {
                int bestDist = int.MaxValue;
                for (int j = 0; j < colors; j++)
                {
                    int dr = r[i] - palette[j, 0], dg = g[i] - palette[j, 1], db = b[i] - palette[j, 2];
                    int d = dr * dr + dg * dg + db * db;
                    if (d < bestDist) { bestDist = d; best = (uint)j; }
                }
            }
            indices |= best << (2 * i);
        }

        WriteColorBlock(output, pos, c0, c1, indices);
    }

    private static void WriteColorBlock(byte[] output, int pos, int c0, int c1, uint indices)
    {
        output[pos] = (byte)c0;
        output[pos + 1] = (byte)(c0 >> 8);
        output[pos + 2] = (byte)c1;
        output[pos + 3] = (byte)(c1 >> 8);
        output[pos + 4] = (byte)indices;
        output[pos + 5] = (byte)(indices >> 8);
        output[pos + 6] = (byte)(indices >> 16);
        output[pos + 7] = (byte)(indices >> 24);
    }

    private static int To565(int r, int g, int b) => ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);

    private static void Expand565(int c, out int r, out int g, out int b)
    {
        int r5 = (c >> 11) & 31, g6 = (c >> 5) & 63, b5 = c & 31;
        r = (r5 << 3) | (r5 >> 2);
        g = (g6 << 2) | (g6 >> 4);
        b = (b5 << 3) | (b5 >> 2);
    }
}
