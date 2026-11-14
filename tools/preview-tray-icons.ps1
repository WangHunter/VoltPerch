param(
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$output = Join-Path $workspace 'artifacts\tray-icon-preview'
$rendererProject = Join-Path $output 'renderer'
New-Item -ItemType Directory -Path $rendererProject -Force | Out-Null
$traySource = Get-Content -LiteralPath (Join-Path $workspace 'src\BatteryDesk\TrayIcon.cs') -Raw
$start = $traySource.IndexOf('    private static Bitmap RenderIconBitmap(')
$end = $traySource.IndexOf('    public void Dispose()', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Could not locate the pure drawing methods.' }
$drawing = $traySource.Substring($start, $end - $start).Replace('private static Bitmap RenderIconBitmap(', 'public static Bitmap RenderIconBitmap(')
$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@
Set-Content -LiteralPath (Join-Path $rendererProject 'Preview.csproj') -Value $project -Encoding utf8
$program = @'
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

try {
var output = args[0];
var samples = new (string name, int? percent, bool charging, string mode, bool low, BatteryLevel? level, bool estimate)[] {
    ("battery-50", 50, false, "Battery", false, null, false),
    ("battery-75", 75, false, "Battery", false, null, false),
    ("estimated-75", 75, false, "Battery", false, null, true),
    ("full-100", 100, false, "Battery", false, null, false),
    ("low-15", 15, false, "Battery", true, null, false),
    ("charging-92", 92, true, "Battery", false, null, false),
    ("unknown", null, false, "Battery", false, null, false),
    ("level-medium", null, false, "Battery", false, BatteryLevel.Medium, false)
};
int[] sizes = [16, 20, 24, 32];
foreach (var dark in new[] { true, false }) {
    using var sheet = new Bitmap(1040, samples.Length * 126 + 100);
    using var g = Graphics.FromImage(sheet);
    var background = dark ? Color.FromArgb(27, 30, 36) : Color.FromArgb(241, 244, 248);
    var foreground = dark ? Color.White : Color.FromArgb(35, 42, 55);
    using var textBrush = new SolidBrush(foreground);
    using var titleFont = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
    using var labelFont = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
    g.Clear(background);
    g.DrawString("VoltPerch tray icon: actual size + 3x pixel zoom", titleFont, textBrush, 20, 12);
    for (var col = 0; col < sizes.Length; col++) {
        var size = sizes[col];
        g.DrawString($"{size}px", titleFont, textBrush, col * 260 + 20, 46);
        for (var row = 0; row < samples.Length; row++) {
            var sample = samples[row];
            using var icon = PreviewRenderer.RenderIconBitmap(size, sample.percent, sample.charging, sample.mode, sample.low, sample.level, sample.estimate, lightTaskbar: !dark);
            var x = col * 260 + 20;
            var y = row * 126 + 80;
            g.DrawString(sample.name, labelFont, textBrush, x, y);
            g.DrawImageUnscaled(icon, x, y + 33);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(icon, new Rectangle(x + 80, y + 24, size * 3, size * 3));
            icon.Save(Path.Combine(output, $"{sample.name}-{size}-{(dark ? "dark" : "light")}.png"), ImageFormat.Png);
            if (dark && size == 16) {
                    var inkXs = new List<int>();
                    var inkYs = new List<int>();
                    for (var iy = 0; iy < size; iy++)
                        for (var ix = 0; ix < size; ix++) {
                            var pixel = icon.GetPixel(ix, iy);
                            if (pixel.A > 128) { inkXs.Add(ix); inkYs.Add(iy); }
                        }
                    Console.WriteLine($"{sample.name} at {size}px: visible ink = {inkXs.Max() - inkXs.Min() + 1}x{inkYs.Max() - inkYs.Min() + 1}px");
            }
        }
    }
    var path = Path.Combine(output, dark ? "tray-icons-dark.png" : "tray-icons-light.png");
    sheet.Save(path, ImageFormat.Png);
    Console.WriteLine(path);
}
} catch (Exception exception) {
    Console.Error.WriteLine(exception);
    Environment.Exit(1);
}
enum BatteryLevel { Empty, Low, Medium, Full }
static class PreviewRenderer {
'@
Set-Content -LiteralPath (Join-Path $rendererProject 'Program.cs') -Value ($program + "`n" + $drawing + "`n}") -Encoding utf8
$env:DOTNET_CLI_HOME = Join-Path $workspace '.dotnet-home'

& $Dotnet run --project (Join-Path $rendererProject 'Preview.csproj') -c Release -- $output
if ($LASTEXITCODE -ne 0) { throw "Drawing preview failed ($LASTEXITCODE)." }
