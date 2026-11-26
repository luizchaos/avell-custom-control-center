using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AvellCC.App.Controls;

/// <summary>Circular usage gauge (0–100 %) with the value in the middle, drawn directly (no template).</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.Register(
        nameof(IsAvailable), typeof(bool), typeof(RingGauge), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Track = Frozen(Color.FromRgb(0x2E, 0x32, 0x3B));
    private static readonly Brush Normal = Frozen(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly Brush High = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush Text = Frozen(Color.FromRgb(0xE6, 0xE8, 0xEC));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0x9A, 0xA1, 0xAD));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }

    /// <summary>False when there is no reading: shows "—" instead of 0.</summary>
    public bool IsAvailable { get => (bool)GetValue(IsAvailableProperty); set => SetValue(IsAvailableProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Diameter, Diameter);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var thickness = size * 0.09;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - thickness) / 2;
        dc.DrawEllipse(null, new Pen(Track, thickness), center, radius, radius);

        var value = Math.Clamp(Value, 0, 100);
        if (IsAvailable && value > 0.5)
        {
            var pen = new Pen(value >= 90 ? High : Normal, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (value >= 99.5)
            {
                dc.DrawEllipse(null, pen, center, radius, radius);
            }
            else
            {
                var angle = value / 100 * 2 * Math.PI;
                var start = new Point(center.X, center.Y - radius);
                var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
                var arc = new StreamGeometry();
                using (var ctx = arc.Open())
                {
                    ctx.BeginFigure(start, false, false);
                    ctx.ArcTo(end, new Size(radius, radius), 0, value > 50, SweepDirection.Clockwise, true, false);
                }
                arc.Freeze();
                dc.DrawGeometry(null, pen, arc);
            }
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var number = new FormattedText(IsAvailable ? value.ToString("0", CultureInfo.CurrentCulture) : "—", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size * 0.27, Text, dpi);
        var unit = new FormattedText("%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size * 0.12, Muted, dpi);
        var top = center.Y - (number.Height + unit.Height * 0.8) / 2;
        dc.DrawText(number, new Point(center.X - number.Width / 2, top));
        dc.DrawText(unit, new Point(center.X - unit.Width / 2, top + number.Height * 0.92));
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
