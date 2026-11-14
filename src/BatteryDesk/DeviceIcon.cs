using System;
using System.Windows;
using System.Windows.Media;

namespace BatteryDesk;

public sealed class DeviceIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(DeviceKind), typeof(DeviceIcon), new FrameworkPropertyMetadata(DeviceKind.Other, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(DeviceIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EarbudsProperty = DependencyProperty.Register(nameof(Earbuds), typeof(bool), typeof(DeviceIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public DeviceKind Kind { get => (DeviceKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public bool Earbuds { get => (bool)GetValue(EarbudsProperty); set => SetValue(EarbudsProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double scale = Math.Min(ActualWidth, ActualHeight) / 48;
        dc.PushTransform(new TranslateTransform((ActualWidth - 48 * scale) / 2, (ActualHeight - 48 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Foreground, 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (Kind)
        {
            case DeviceKind.Keyboard:
                dc.DrawRoundedRectangle(null, pen, new Rect(3, 11, 42, 28), 4, 4);
                for (int y = 17; y <= 27; y += 5) for (int x = 9; x <= 39; x += 6) dc.DrawRectangle(Foreground, null, new Rect(x, y, 2.6, 2.6));
                dc.DrawLine(pen, new Point(16, 34), new Point(32, 34));
                break;
            case DeviceKind.Mouse:
                dc.DrawRoundedRectangle(null, pen, new Rect(11, 3, 26, 42), 13, 13);
                dc.DrawLine(pen, new Point(24, 4), new Point(24, 17));
                break;
            case DeviceKind.Headphones:
                if (Earbuds)
                {
                    Path(dc, pen,"M20,14 C20,1 2,1 2,14 C2,22 9,24 12,22 L12,42 L19,42 Z M28,14 C28,1 46,1 46,14 C46,22 39,24 36,22 L36,42 L29,42 Z");
                    dc.DrawEllipse(null,pen,new Point(11,14),2.5,2.5);dc.DrawEllipse(null,pen,new Point(37,14),2.5,2.5);
                }
                else Path(dc, pen, "M8,27 L8,22 C8,1 40,1 40,22 L40,27 M10,25 C5,24 3,28 5,34 L8,42 C10,46 16,44 15,39 L12,29 C12,27 11,26 10,25 M38,25 C43,24 45,28 43,34 L40,42 C38,46 32,44 33,39 L36,29 C36,27 37,26 38,25");
                break;
            case DeviceKind.Controller:
                Path(dc, pen, "M14,12 C7,12 6,17 3,33 C1,43 7,44 14,35 L34,35 C41,44 47,43 45,33 C42,17 41,12 34,12 Z M11,22 L21,22 M16,17 L16,27");
                dc.DrawEllipse(Foreground, null, new Point(35, 20), 1.8, 1.8);
                dc.DrawEllipse(Foreground, null, new Point(30, 25), 1.8, 1.8);
                break;
            default:
                Path(dc, pen, "M8,9 L40,9 L40,33 L8,33 Z M3,39 L45,39 M18,33 L18,39 M30,33 L30,39");
                break;
        }
        dc.Pop(); dc.Pop();
    }
    private static void Path(DrawingContext dc, Pen pen, string path) => dc.DrawGeometry(null, pen, Geometry.Parse(path));
}
