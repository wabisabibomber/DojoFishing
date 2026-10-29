using System.Windows;
using System.Windows.Media;

namespace DojoFishing;

public sealed class ScorePlot : FrameworkElement
{
    private readonly Queue<(double Seconds, double Score)> points = new();
    public double Threshold { get; set; } = .55;
    public void Add(double seconds, double score)
    {
        points.Enqueue((seconds, score));
        while (points.Count > 1 && points.Peek().Seconds < seconds - 15) points.Dequeue();
        InvalidateVisual();
    }
    public void Clear() { points.Clear(); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
        for (int i = 0; i <= 4; i++) dc.DrawLine(new Pen(Brushes.LightGray, .5), new Point(0, h * i / 4), new Point(w, h * i / 4));
        dc.DrawLine(new Pen(Brushes.IndianRed, 1), new Point(0, h * (1 - Threshold)), new Point(w, h * (1 - Threshold)));
        if (points.Count < 2) return;
        double end = Math.Max(15, points.Last().Seconds);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            bool first = true;
            foreach (var item in points)
            {
                var point = new Point(w * (item.Seconds - end + 15) / 15, h * (1 - Math.Clamp(item.Score, 0, 1)));
                if (first) { context.BeginFigure(point, false, false); first = false; }
                else context.LineTo(point, true, false);
            }
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(26, 111, 153)), 2), geometry);
    }
}
