using System.IO.Compression;
using System.Text;

namespace CrowdFund.Web.Services;

/// <summary>
/// Generates Open Graph share images (1200×630 PNG) without external graphics packages.
/// </summary>
public static class OgImageGenerator
{
    public const int Width = 1200;
    public const int Height = 630;

    public static byte[] Generate(string title, string subtitle, int trustScore, string band, string brand = "CrowdFund AI")
    {
        var pixels = new byte[Width * Height * 3]; // RGB

        // Background gradient (purple theme #6C5CE7 → #2d1b69)
        for (var y = 0; y < Height; y++)
        {
            var t = y / (float)(Height - 1);
            var r = (byte)(108 + (45 - 108) * t);
            var g = (byte)(92 + (27 - 92) * t);
            var b = (byte)(231 + (105 - 231) * t);
            for (var x = 0; x < Width; x++)
            {
                var i = (y * Width + x) * 3;
                // subtle horizontal vignette
                var vx = Math.Abs(x - Width / 2) / (float)(Width / 2);
                var f = 1f - 0.12f * vx;
                pixels[i] = (byte)(r * f);
                pixels[i + 1] = (byte)(g * f);
                pixels[i + 2] = (byte)(b * f);
            }
        }

        // Card panel
        FillRect(pixels, 60, 80, Width - 120, Height - 160, 250, 250, 254);
        // Accent bar
        FillRect(pixels, 60, 80, 16, Height - 160, 108, 92, 231);

        var safeTitle = Truncate(title ?? "Campaign", 42);
        var safeSub = Truncate(subtitle ?? "", 70);
        DrawText(pixels, 100, 130, brand, 2, 108, 92, 231);
        DrawText(pixels, 100, 200, safeTitle, 3, 30, 30, 40);
        if (!string.IsNullOrWhiteSpace(safeSub))
            DrawText(pixels, 100, 320, safeSub, 2, 90, 90, 110);

        var bandColor = band switch
        {
            "High" => (46, 125, 50),
            "Medium" => (249, 168, 37),
            _ => (198, 40, 40)
        };
        DrawText(pixels, 100, 480, $"Trust {trustScore} · {band}", 2, (byte)bandColor.Item1, (byte)bandColor.Item2, (byte)bandColor.Item3);
        DrawText(pixels, 100, 540, "Advisory trust signals · Not a guarantee", 1, 140, 140, 150);

        return EncodePng(pixels, Width, Height);
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    private static void FillRect(byte[] px, int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        for (var y = y0; y < y0 + h && y < Height; y++)
        for (var x = x0; x < x0 + w && x < Width; x++)
        {
            if (x < 0 || y < 0) continue;
            var i = (y * Width + x) * 3;
            px[i] = r; px[i + 1] = g; px[i + 2] = b;
        }
    }

    // 5x7 style bitmap font (A-Z, a-z, 0-9, basic punct)
    private static void DrawText(byte[] px, int x, int y, string text, int scale, byte r, byte g, byte b)
    {
        var cx = x;
        foreach (var ch in text)
        {
            var glyph = Glyph(ch);
            for (var gy = 0; gy < 7; gy++)
            for (var gx = 0; gx < 5; gx++)
            {
                if (((glyph[gy] >> (4 - gx)) & 1) == 0) continue;
                for (var sy = 0; sy < scale; sy++)
                for (var sx = 0; sx < scale; sx++)
                {
                    var px_ = cx + gx * scale + sx;
                    var py_ = y + gy * scale + sy;
                    if (px_ < 0 || py_ < 0 || px_ >= Width || py_ >= Height) continue;
                    var i = (py_ * Width + px_) * 3;
                    px[i] = r; px[i + 1] = g; px[i + 2] = b;
                }
            }
            cx += 6 * scale;
            if (cx > Width - 40) break;
        }
    }

    private static byte[] Glyph(char c)
    {
        // rows of 5 bits
        return c switch
        {
            'A' or 'a' => new byte[] { 0x0E, 0x11, 0x1F, 0x11, 0x11, 0x11, 0x00 },
            'B' or 'b' => new byte[] { 0x1E, 0x11, 0x1E, 0x11, 0x11, 0x1E, 0x00 },
            'C' or 'c' => new byte[] { 0x0E, 0x11, 0x10, 0x10, 0x11, 0x0E, 0x00 },
            'D' or 'd' => new byte[] { 0x1E, 0x11, 0x11, 0x11, 0x11, 0x1E, 0x00 },
            'E' or 'e' => new byte[] { 0x1F, 0x10, 0x1E, 0x10, 0x10, 0x1F, 0x00 },
            'F' or 'f' => new byte[] { 0x1F, 0x10, 0x1E, 0x10, 0x10, 0x10, 0x00 },
            'G' or 'g' => new byte[] { 0x0E, 0x11, 0x10, 0x13, 0x11, 0x0E, 0x00 },
            'H' or 'h' => new byte[] { 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11, 0x00 },
            'I' or 'i' => new byte[] { 0x0E, 0x04, 0x04, 0x04, 0x04, 0x0E, 0x00 },
            'J' or 'j' => new byte[] { 0x07, 0x02, 0x02, 0x02, 0x12, 0x0C, 0x00 },
            'K' or 'k' => new byte[] { 0x11, 0x12, 0x1C, 0x12, 0x11, 0x11, 0x00 },
            'L' or 'l' => new byte[] { 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F, 0x00 },
            'M' or 'm' => new byte[] { 0x11, 0x1B, 0x15, 0x11, 0x11, 0x11, 0x00 },
            'N' or 'n' => new byte[] { 0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x00 },
            'O' or 'o' => new byte[] { 0x0E, 0x11, 0x11, 0x11, 0x11, 0x0E, 0x00 },
            'P' or 'p' => new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x00 },
            'Q' or 'q' => new byte[] { 0x0E, 0x11, 0x11, 0x15, 0x12, 0x0D, 0x00 },
            'R' or 'r' => new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x12, 0x11, 0x00 },
            'S' or 's' => new byte[] { 0x0F, 0x10, 0x0E, 0x01, 0x11, 0x0E, 0x00 },
            'T' or 't' => new byte[] { 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x00 },
            'U' or 'u' => new byte[] { 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E, 0x00 },
            'V' or 'v' => new byte[] { 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04, 0x00 },
            'W' or 'w' => new byte[] { 0x11, 0x11, 0x11, 0x15, 0x1B, 0x11, 0x00 },
            'X' or 'x' => new byte[] { 0x11, 0x0A, 0x04, 0x04, 0x0A, 0x11, 0x00 },
            'Y' or 'y' => new byte[] { 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04, 0x00 },
            'Z' or 'z' => new byte[] { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x1F, 0x00 },
            '0' => new byte[] { 0x0E, 0x11, 0x13, 0x15, 0x19, 0x0E, 0x00 },
            '1' => new byte[] { 0x04, 0x0C, 0x04, 0x04, 0x04, 0x0E, 0x00 },
            '2' => new byte[] { 0x0E, 0x11, 0x01, 0x06, 0x08, 0x1F, 0x00 },
            '3' => new byte[] { 0x1F, 0x01, 0x06, 0x01, 0x11, 0x0E, 0x00 },
            '4' => new byte[] { 0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x00 },
            '5' => new byte[] { 0x1F, 0x10, 0x1E, 0x01, 0x11, 0x0E, 0x00 },
            '6' => new byte[] { 0x06, 0x08, 0x1E, 0x11, 0x11, 0x0E, 0x00 },
            '7' => new byte[] { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x00 },
            '8' => new byte[] { 0x0E, 0x11, 0x0E, 0x11, 0x11, 0x0E, 0x00 },
            '9' => new byte[] { 0x0E, 0x11, 0x11, 0x0F, 0x02, 0x0C, 0x00 },
            ' ' => new byte[] { 0, 0, 0, 0, 0, 0, 0 },
            '.' => new byte[] { 0, 0, 0, 0, 0x04, 0x04, 0 },
            ',' => new byte[] { 0, 0, 0, 0, 0x04, 0x08, 0 },
            '-' or '–' or '—' => new byte[] { 0, 0, 0x1F, 0, 0, 0, 0 },
            ':' => new byte[] { 0, 0x04, 0, 0, 0x04, 0, 0 },
            '/' => new byte[] { 0x01, 0x02, 0x04, 0x08, 0x10, 0, 0 },
            '%' => new byte[] { 0x19, 0x1A, 0x04, 0x08, 0x13, 0x03, 0 },
            '·' => new byte[] { 0, 0, 0x04, 0, 0, 0, 0 },
            '…' => new byte[] { 0, 0, 0, 0, 0x15, 0, 0 },
            _ => new byte[] { 0x0E, 0x11, 0x01, 0x06, 0x00, 0x04, 0x00 }, // ?
        };
    }

    private static byte[] EncodePng(byte[] rgb, int w, int h)
    {
        using var ms = new MemoryStream();
        // signature
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        // IHDR
        var ihdr = new byte[13];
        WriteInt(ihdr, 0, w);
        WriteInt(ihdr, 4, h);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        WriteChunk(ms, "IHDR", ihdr);

        // IDAT raw: each row filter 0 + RGB
        using var raw = new MemoryStream();
        for (var y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgb, y * w * 3, w * 3);
        }
        var compressed = ZlibCompress(raw.ToArray());
        WriteChunk(ms, "IDAT", compressed);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteInt(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)((value >> 24) & 0xff);
        buf[offset + 1] = (byte)((value >> 16) & 0xff);
        buf[offset + 2] = (byte)((value >> 8) & 0xff);
        buf[offset + 3] = (byte)(value & 0xff);
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteInt(len, 0, data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crcSrc = new byte[typeBytes.Length + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcSrc, 0, 4);
        Buffer.BlockCopy(data, 0, crcSrc, 4, data.Length);
        var crc = Crc32(crcSrc);
        var crcBytes = new byte[4];
        WriteInt(crcBytes, 0, (int)crc);
        s.Write(crcBytes);
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        // zlib header
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);
        using (var def = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            def.Write(data, 0, data.Length);
        var adler = Adler32(data);
        ms.WriteByte((byte)((adler >> 24) & 0xff));
        ms.WriteByte((byte)((adler >> 16) & 0xff));
        ms.WriteByte((byte)((adler >> 8) & 0xff));
        ms.WriteByte((byte)(adler & 0xff));
        return ms.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xffffffff;
        foreach (var b in data)
        {
            var x = (crc ^ b) & 0xff;
            for (var k = 0; k < 8; k++)
                x = (x & 1) != 0 ? (0xedb88320 ^ (x >> 1)) : (x >> 1);
            crc = (crc >> 8) ^ x;
        }
        return crc ^ 0xffffffff;
    }
}
