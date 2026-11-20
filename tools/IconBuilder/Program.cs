using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Render the VoltPerch mark as a multi-resolution Windows icon.
try
{
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "src/BatteryDesk/Assets");
Directory.CreateDirectory(output);
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var images = new List<byte[]>();
foreach (var size in sizes)
{
    using var bitmap = RenderIcon(size);
    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Png);
    images.Add(stream.ToArray());
    if (size == 256) bitmap.Save(Path.Combine(output, "app-preview.png"), ImageFormat.Png);
}
using (var file = File.Create(Path.Combine(output, "app.ico")))
using (var writer = new BinaryWriter(file))
{
    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
        writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
        writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(images[i].Length); writer.Write(offset);
        offset += images[i].Length;
    }
    foreach (var image in images) writer.Write(image);
}
Console.WriteLine($"Wrote {Path.Combine(output, "app.ico")} with sizes {string.Join(", ", sizes)}");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

static Bitmap RenderIcon(int size)
{
    const int grid = 32;
    const int supersample = 4;
    int dimension = size * supersample;
    bool small = size <= 24;
    using var drawing = new Bitmap(dimension, dimension, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(drawing))
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform((float)dimension / grid, (float)dimension / grid);

        using var rim = new SolidBrush(Color.FromArgb(64, 107, 118));
        using var tile = new SolidBrush(Color.FromArgb(16, 44, 52));
        using var mint = new SolidBrush(Color.FromArgb(170, 250, 216));
        using var outside = RoundedRect(1, 1, 30, 30, 7);
        float inset = small ? 2.15f : 1.95f;
        using var inside = RoundedRect(inset, inset, grid - 2 * inset, grid - 2 * inset, 7 - (inset - 1));
        g.FillPath(rim, outside);
        g.FillPath(tile, inside);

        // The bolt is a hole in the silhouette, not a second painted symbol.
        using var bird = new GraphicsPath(FillMode.Alternate);
        bird.StartFigure();
        bird.AddLine(5.7f, 12.6f, 11.6f, 15.0f);
        bird.AddBezier(11.6f, 15.0f, 13.0f, 11.7f, 15.2f, 9.8f, 18.1f, 9.7f);
        bird.AddBezier(18.1f, 9.7f, 18.9f, 7.4f, 20.6f, 6.3f, 22.5f, 6.3f);
        bird.AddBezier(22.5f, 6.3f, 24.3f, 6.3f, 25.6f, 7.2f, 26.1f, 8.8f);
        bird.AddLines([new PointF(26.1f, 8.8f), new(28.3f, 10.1f), new(26.1f, 11.4f)]);
        bird.AddBezier(26.1f, 11.4f, 25.5f, 18.0f, 21.4f, 22.5f, 15.0f, 22.5f);
        bird.AddLines([new PointF(15.0f, 22.5f), new(9.1f, 22.5f), new(11.5f, 19.7f), new(5.7f, 12.6f)]);
        bird.CloseFigure();
        bird.AddPolygon([
            new PointF(19.6f, 11.6f), new(small ? 13.7f : 14.4f, 17.3f), new(17.6f, 17.3f),
            new(16.5f, 20.5f), new(small ? 23.2f : 22.5f, 14.6f), new(19.3f, 14.6f), new(20.5f, 11.6f)
        ]);
        g.FillPath(mint, bird);

        // The short stem seats the silhouette on the perch, even at 16 px.
        g.FillRectangle(mint, 16.1f, 22.0f, small ? 2.6f : 2.2f, 3.05f);
        float perchHeight = small ? 2.35f : 2.0f;
        using var perch = RoundedRect(9.0f, 24.2f, 15.3f, perchHeight, perchHeight / 2);
        g.FillPath(mint, perch);
    }

    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    using var attributes = new ImageAttributes();
    graphics.CompositingMode = CompositingMode.SourceCopy;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    attributes.SetWrapMode(WrapMode.TileFlipXY);
    graphics.DrawImage(drawing, new Rectangle(0, 0, size, size), 0, 0, dimension, dimension,
        GraphicsUnit.Pixel, attributes);
    return bitmap;
}

static GraphicsPath RoundedRect(float x, float y, float width, float height, float radius)
{
    var path = new GraphicsPath();
    float diameter = radius * 2;
    path.AddArc(x, y, diameter, diameter, 180, 90);
    path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
    path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
    path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
}
