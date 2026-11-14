using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Package the generated application artwork as a multi-resolution Windows icon.
// Keep the original alpha channel; all sizes are derived from the same source.
try
{
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "src/BatteryDesk/Assets");
Directory.CreateDirectory(output);
using var artwork = Image.FromFile(Path.Combine(output, "app-imagegen.png"));
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var images = new List<byte[]>();
foreach (var size in sizes)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(bitmap))
    using (var attributes = new ImageAttributes())
    {
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        graphics.DrawImage(artwork, new Rectangle(0, 0, size, size),
            0, 0, artwork.Width, artwork.Height, GraphicsUnit.Pixel, attributes);
    }
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
