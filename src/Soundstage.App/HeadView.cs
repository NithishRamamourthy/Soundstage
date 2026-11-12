using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using Soundstage.Engine;

namespace Soundstage.UI;

/// <summary>Top-down view of the listener: virtual speakers stay fixed in the room
/// while the head in the middle turns with the tracker.</summary>
public sealed class HeadView : FrameworkElement
{
    static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(HeadView),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty YawProperty = Register(nameof(Yaw), 0.0);
    public static readonly DependencyProperty LayoutProperty = Register<SpeakerLayout?>(nameof(Layout), null);
    public static readonly DependencyProperty ActiveProperty = Register(nameof(Active), false);
    public static readonly DependencyProperty LevelsProperty = Register<double[]?>(nameof(Levels), null);
    public static readonly DependencyProperty StrokeProperty = Register<Brush>(nameof(Stroke), Brushes.Gray);
    public static readonly DependencyProperty SubtleProperty = Register<Brush>(nameof(Subtle), Brushes.LightGray);
    public static readonly DependencyProperty AccentProperty = Register<Brush>(nameof(Accent), Brushes.DodgerBlue);
    public static readonly DependencyProperty HeadFillProperty = Register<Brush>(nameof(HeadFill), Brushes.White);

    public double Yaw { get => (double)GetValue(YawProperty); set => SetValue(YawProperty, value); }
    public SpeakerLayout? Layout { get => (SpeakerLayout?)GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }
    /// <summary>Level per channel (0..1); a speaker glows while sound comes from it.</summary>
    public double[]? Levels { get => (double[]?)GetValue(LevelsProperty); set => SetValue(LevelsProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Subtle { get => (Brush)GetValue(SubtleProperty); set => SetValue(SubtleProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush HeadFill { get => (Brush)GetValue(HeadFillProperty); set => SetValue(HeadFillProperty, value); }

    static readonly double[] DefaultAzimuths = { -30, 30 };
    static readonly string[] DefaultNames = { "FL", "FR" };
    static readonly FontFamily HeadFontFamily = new("Segoe UI Variable Text, Segoe UI");
    static readonly Typeface HeadTypeface = new(HeadFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 60) return;
        var c = new Point(w / 2, h / 2);
        double radius = Math.Min(w, h) / 2 - 30;
        if (radius < 30) return;

        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = HeadTypeface;

        // Accent colour, falling back to the signature violet when a theme brush is not a solid one.
        Color accentColor = Accent is SolidColorBrush scb ? scb.Color : Color.FromRgb(0x8B, 0x5C, 0xF6);

        // Soft radial glow behind the listening position, fading from accent to transparent.
        double glowR = radius * 0.62;
        var glow = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            GradientOrigin = new Point(c.X, c.Y),
            RadiusX = glowR,
            RadiusY = glowR,
        };
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(60, accentColor.R, accentColor.G, accentColor.B), 0.0));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(18, accentColor.R, accentColor.G, accentColor.B), 0.55));
        glow.GradientStops.Add(new GradientStop(Colors.Transparent, 1.0));
        dc.DrawEllipse(glow, null, c, glowR, glowR);

        // Concentric room rings: hairline outer, dashed inner.
        var ringPen = new Pen(Subtle, 1);
        dc.PushOpacity(0.45);
        dc.DrawEllipse(null, ringPen, c, radius, radius);
        dc.Pop();
        var dashPen = new Pen(Subtle, 1) { DashStyle = new DashStyle(new double[] { 3, 5 }, 0) };
        dc.DrawEllipse(null, dashPen, c, radius * 0.58, radius * 0.58);

        // Compass ticks at front, right, back and left.
        var tickPen = new Pen(Subtle, 1);
        for (int i = 0; i < 4; i++)
        {
            double a = i * Math.PI / 2;
            var dir = new Vector(Math.Sin(a), -Math.Cos(a));
            dc.DrawLine(tickPen, c + dir * (radius - 5), c + dir * (radius + 5));
        }

        // Virtual speakers, fixed in the room.
        var layout = Layout;
        int count = layout?.Count ?? DefaultAzimuths.Length;
        var levels = Levels;
        for (int i = 0; i < count; i++)
        {
            if (layout != null && layout.IsLfe[i]) continue;
            double az = (layout?.Azimuth[i] ?? DefaultAzimuths[i]) * Math.PI / 180;
            string name = layout?.Names[i] ?? DefaultNames[i];
            var dir = new Vector(Math.Sin(az), -Math.Cos(az));
            var p = new Point(c.X + dir.X * radius, c.Y + dir.Y * radius);
            double level = levels != null && i < levels.Length ? levels[i] : 0;

            // Soft accent halo that blooms with the channel level.
            if (level > 0)
            {
                double haloR = 8 + 12 * level;
                dc.PushOpacity(0.30 * level);
                dc.DrawEllipse(Accent, null, p, haloR, haloR);
                dc.Pop();
            }

            // Small dot: HeadFill core with an Accent ring when the channel is live/hot.
            bool hot = Active || level > 0.02;
            if (hot) dc.DrawEllipse(HeadFill, new Pen(Accent, 1.5), p, 5, 5);
            else dc.DrawEllipse(Stroke, null, p, 4.5, 4.5);

            // Channel label, centred just outside the ring.
            var text = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10.5, Stroke, dip);
            var tp = new Point(c.X + dir.X * (radius + 19), c.Y + dir.Y * (radius + 19));
            dc.DrawText(text, new Point(tp.X - text.Width / 2, tp.Y - text.Height / 2));
        }

        // The head, turned by the tracked yaw (clockwise = turning right).
        double hr = radius * 0.19;
        dc.PushTransform(new RotateTransform(Yaw, c.X, c.Y));

        // Direction wedge: attention cone from the nose towards the front.
        double wedgeLen = radius * 0.62;
        var wedge = new StreamGeometry();
        using (var g = wedge.Open())
        {
            double half = 24 * Math.PI / 180;
            g.BeginFigure(c, true, true);
            g.LineTo(new Point(c.X - Math.Sin(half) * wedgeLen, c.Y - Math.Cos(half) * wedgeLen), false, false);
            g.ArcTo(new Point(c.X + Math.Sin(half) * wedgeLen, c.Y - Math.Cos(half) * wedgeLen), new Size(wedgeLen, wedgeLen), 0, false, SweepDirection.Clockwise, false, false);
        }
        wedge.Freeze();
        // The fan is brightest at the listener and dissolves into the room.
        var wedgeGrad = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            GradientOrigin = new Point(c.X, c.Y),
            Center = new Point(c.X, c.Y),
            RadiusX = wedgeLen,
            RadiusY = wedgeLen,
        };
        wedgeGrad.GradientStops.Add(new GradientStop(Color.FromArgb(130, accentColor.R, accentColor.G, accentColor.B), 0.0));
        wedgeGrad.GradientStops.Add(new GradientStop(Color.FromArgb(46, accentColor.R, accentColor.G, accentColor.B), 0.62));
        wedgeGrad.GradientStops.Add(new GradientStop(Colors.Transparent, 1.0));
        dc.DrawGeometry(wedgeGrad, null, wedge);

        var headPen = new Pen(Stroke, 1.5);
        if (Active)
        {
            dc.PushOpacity(0.55);
            dc.DrawEllipse(null, new Pen(Accent, 1), c, hr + 6, hr + 6);
            dc.Pop();
        }
        dc.DrawEllipse(HeadFill, headPen, c, hr, hr);

        // Ears flanking the head, nose at the front.
        dc.DrawEllipse(HeadFill, headPen, new Point(c.X - hr, c.Y), 4, hr * 0.42);
        dc.DrawEllipse(HeadFill, headPen, new Point(c.X + hr, c.Y), 4, hr * 0.42);

        var nose = new StreamGeometry();
        using (var g = nose.Open())
        {
            g.BeginFigure(new Point(c.X, c.Y - hr - 9), true, true);
            g.LineTo(new Point(c.X - 7, c.Y - hr + 3), true, true);
            g.LineTo(new Point(c.X + 7, c.Y - hr + 3), true, true);
        }
        nose.Freeze();
        dc.DrawGeometry(HeadFill, headPen, nose);

        dc.Pop();
    }
}
