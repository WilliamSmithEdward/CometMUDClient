using System.Buffers.Binary;
using SkiaSharp;

namespace Comet.Tools.IconGenerator;

/// <summary>
/// Draws the Comet icon (a comet crossing a night-sky tile) at every size the platforms need
/// and writes comet.ico, comet.icns and comet.png into the app's Assets folder.
/// </summary>
internal static class Program
{
    private const float Design = 256f;

    private static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : FindAssetsDirectory();
        Directory.CreateDirectory(output);

        var png = new Dictionary<int, byte[]>();
        foreach (int size in (int[])[16, 24, 32, 48, 64, 128, 256, 512, 1024])
        {
            png[size] = Render(size);
        }

        File.WriteAllBytes(Path.Combine(output, "comet.png"), png[512]);
        File.WriteAllBytes(Path.Combine(output, "comet.ico"), BuildIco(png, [16, 24, 32, 48, 64, 128, 256]));
        File.WriteAllBytes(Path.Combine(output, "comet.icns"), BuildIcns(png));
        Console.WriteLine($"Wrote comet.png, comet.ico and comet.icns to {output}");
        return 0;
    }

    private static string FindAssetsDirectory()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Comet.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }
        if (directory is null)
        {
            throw new InvalidOperationException("Run this from inside the repository, or pass an output folder.");
        }
        return Path.Combine(directory, "src", "Comet.App", "Assets");
    }

    private static byte[] Render(int size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / Design);

        // Small sizes get a bolder drawing: fewer details, a bigger head, a thicker tail.
        bool small = size <= 32;
        bool tiny = size <= 16;

        var tile = new SKRoundRect(new SKRect(8, 8, 248, 248), 54, 54);
        using (var paint = new SKPaint { IsAntialias = true })
        {
            paint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(40, 16), new SKPoint(216, 248),
                [new SKColor(0x1D, 0x2B, 0x53), new SKColor(0x0B, 0x10, 0x26), new SKColor(0x05, 0x07, 0x12)],
                [0f, 0.6f, 1f], SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(tile, paint);
        }

        canvas.Save();
        canvas.ClipRoundRect(tile, antialias: true);

        if (!small)
        {
            using var star = new SKPaint { IsAntialias = true };
            foreach ((float x, float y, float r, byte a) in ((float, float, float, byte)[])
                [(52, 58, 3.2f, 210), (96, 34, 2.2f, 150), (214, 150, 3f, 190), (188, 208, 2.2f, 140), (34, 132, 2f, 120), (228, 96, 1.8f, 110), (146, 226, 2.4f, 150)])
            {
                star.Color = new SKColor(0xE8, 0xF4, 0xFF, a);
                canvas.DrawCircle(x, y, r, star);
            }
        }

        var head = new SKPoint(small ? 170 : 174, small ? 86 : 82);
        var tailEnd = new SKPoint(34, 222);
        float headRadius = tiny ? 34 : small ? 30 : 24;

        DrawTail(canvas, head, tailEnd, headRadius * 0.95f, small ? 62 : 52, new SKColor(0x4F, 0xB6, 0xFF, 235), new SKColor(0x4F, 0xB6, 0xFF, 0));
        DrawTail(canvas, head, new SKPoint(62, 194), headRadius * 0.6f, small ? 26 : 20, new SKColor(0xEA, 0xF8, 0xFF, 245), new SKColor(0xBF, 0xE9, 0xFF, 0));

        using (var glow = new SKPaint { IsAntialias = true })
        {
            glow.Shader = SKShader.CreateRadialGradient(
                head, headRadius * 2.4f,
                [new SKColor(0xBF, 0xE9, 0xFF, 200), new SKColor(0x5C, 0xBE, 0xFF, 90), new SKColor(0x5C, 0xBE, 0xFF, 0)],
                [0f, 0.45f, 1f], SKShaderTileMode.Clamp);
            canvas.DrawCircle(head, headRadius * 2.4f, glow);
        }
        using (var core = new SKPaint { IsAntialias = true })
        {
            core.Shader = SKShader.CreateRadialGradient(
                new SKPoint(head.X + (headRadius * 0.2f), head.Y - (headRadius * 0.2f)), headRadius,
                [SKColors.White, new SKColor(0xF2, 0xFB, 0xFF), new SKColor(0xA9, 0xE0, 0xFF)],
                [0f, 0.55f, 1f], SKShaderTileMode.Clamp);
            canvas.DrawCircle(head, headRadius, core);
        }

        canvas.Restore();

        if (!tiny)
        {
            using var rim = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = small ? 6 : 3,
                Color = new SKColor(0x6F, 0x8F, 0xD0, 110),
            };
            canvas.DrawRoundRect(tile, rim);
        }

        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A tail that starts as wide as the head and fans out, fading to nothing.</summary>
    private static void DrawTail(SKCanvas canvas, SKPoint head, SKPoint end, float startHalfWidth, float endHalfWidth, SKColor from, SKColor to)
    {
        float dx = end.X - head.X;
        float dy = end.Y - head.Y;
        float length = MathF.Sqrt((dx * dx) + (dy * dy));
        var normal = new SKPoint(-dy / length, dx / length);

        using var path = new SKPath();
        path.MoveTo(head.X + (normal.X * startHalfWidth), head.Y + (normal.Y * startHalfWidth));
        path.LineTo(end.X + (normal.X * endHalfWidth), end.Y + (normal.Y * endHalfWidth));
        path.LineTo(end.X - (normal.X * endHalfWidth), end.Y - (normal.Y * endHalfWidth));
        path.LineTo(head.X - (normal.X * startHalfWidth), head.Y - (normal.Y * startHalfWidth));
        path.Close();

        using var paint = new SKPaint { IsAntialias = true };
        paint.Shader = SKShader.CreateLinearGradient(head, end, [from, to], [0f, 1f], SKShaderTileMode.Clamp);
        canvas.DrawPath(path, paint);
    }

    /// <summary>An ICO file whose entries are PNG images, which Windows Vista and later read.</summary>
    private static byte[] BuildIco(Dictionary<int, byte[]> png, int[] sizes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);

        int offset = 6 + (16 * sizes.Length);
        foreach (int size in sizes)
        {
            byte[] image = png[size];
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)image.Length);
            writer.Write((uint)offset);
            offset += image.Length;
        }
        foreach (int size in sizes)
        {
            writer.Write(png[size]);
        }
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>An Apple ICNS file: a list of PNG images tagged by size, including the 2x variants.</summary>
    private static byte[] BuildIcns(Dictionary<int, byte[]> png)
    {
        (string Type, int Size)[] entries =
        [
            ("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024),
            ("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512),
        ];

        using var body = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach ((string type, int size) in entries)
        {
            byte[] image = png[size];
            body.Write(System.Text.Encoding.ASCII.GetBytes(type));
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)(image.Length + 8));
            body.Write(length);
            body.Write(image);
        }

        using var file = new MemoryStream();
        file.Write("icns"u8);
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)(body.Length + 8));
        file.Write(length);
        body.WriteTo(file);
        return file.ToArray();
    }
}
