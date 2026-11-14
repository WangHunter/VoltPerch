using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace BatteryDesk;

public enum ToolbarIconKind { Refresh, Theme, Settings, Back }

public sealed class ToolbarIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(ToolbarIconKind), typeof(ToolbarIcon), new FrameworkPropertyMetadata(ToolbarIconKind.Refresh, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(ToolbarIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public ToolbarIconKind Kind { get => (ToolbarIconKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public ToolbarIcon() { Width = 20; Height = 20; IsHitTestVisible = false; }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double scale = Math.Min(ActualWidth, ActualHeight) / 24;
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Foreground, 1.9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (Kind)
        {
            case ToolbarIconKind.Refresh:
                DrawPath(dc, pen, "M20,9 A8.3,8.3 0 0 0 5.2,6.5 M20,4 L20,9 L15,9 M4,15 A8.3,8.3 0 0 0 18.8,17.5 M4,20 L4,15 L9,15");
                break;
            case ToolbarIconKind.Theme:
                dc.DrawEllipse(null, pen, new Point(12, 12), 8.3, 8.3);
                dc.DrawGeometry(Foreground, null, Geometry.Parse("M12,3.7 A8.3,8.3 0 0 0 12,20.3 Z"));
                break;
            case ToolbarIconKind.Settings:
                DrawPath(dc, pen, "M9.5,3 L14.5,3 L15.1,5.5 L17,6.6 L19.5,5.9 L22,10.1 L20.1,12 L20.1,14 L22,15.9 L19.5,20.1 L17,19.4 L15.1,20.5 L14.5,23 L9.5,23 L8.9,20.5 L7,19.4 L4.5,20.1 L2,15.9 L3.9,14 L3.9,12 L2,10.1 L4.5,5.9 L7,6.6 L8.9,5.5 Z", new TranslateTransform(0, -1));
                dc.DrawEllipse(null, pen, new Point(12, 12), 3.2, 3.2);
                break;
            case ToolbarIconKind.Back:
                DrawPath(dc, pen, "M10,5 L3,12 L10,19 M3,12 L21,12");
                break;
        }
        dc.Pop(); dc.Pop();
    }

    private static void DrawPath(DrawingContext dc, Pen pen, string data, Transform? transform = null)
    {
        if (transform != null) dc.PushTransform(transform);
        dc.DrawGeometry(null, pen, Geometry.Parse(data));
        if (transform != null) dc.Pop();
    }
}
