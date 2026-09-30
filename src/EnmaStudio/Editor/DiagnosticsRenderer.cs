using System.Windows;
using System.Windows.Media;
using Enma;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace EnmaStudio.Editor;

/// <summary>Underlines Enma compiler errors (red) and warnings (yellow) with a wavy line.</summary>
public sealed class DiagnosticsRenderer : IBackgroundRenderer
{
    private static readonly Pen ErrorPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0x40, 0x4A)), 1.2));
    private static readonly Pen WarningPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xD7, 0xBA, 0x3D)), 1.2));

    public IReadOnlyList<EnmaDiagnostic> Diagnostics { get; set; } = [];

    public KnownLayer Layer => KnownLayer.Selection;

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    /// <summary>Offset range of a diagnostic in <paramref name="document"/>, clamped to its line.</summary>
    public static (int Start, int Length) Range(TextDocument document, EnmaDiagnostic d)
    {
        int lineNumber = Math.Clamp(d.Line, 1, document.LineCount);
        var line = document.GetLineByNumber(lineNumber);
        int start = line.Offset + Math.Clamp(d.Column - 1, 0, line.Length);
        int length = Math.Min(Math.Max(1, d.Length), line.EndOffset - start);
        if (length <= 0)
        {
            // At the end of a line (e.g. "missing }" at end of file): mark the last character instead.
            start = Math.Max(line.Offset, line.EndOffset - 1);
            length = Math.Max(0, line.EndOffset - start);
        }
        return (start, length);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Diagnostics.Count == 0 || textView.Document == null || !textView.VisualLinesValid) return;
        var document = textView.Document;
        foreach (var d in Diagnostics)
        {
            var (start, length) = Range(document, d);
            if (length <= 0) continue;
            var segment = new TextSegment { StartOffset = start, Length = length };
            var pen = d.Severity == EnmaSeverity.Error ? ErrorPen : WarningPen;
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                drawingContext.DrawGeometry(null, pen, Wave(rect.BottomLeft, rect.Right));
        }
    }

    private static StreamGeometry Wave(Point start, double right)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            double y = start.Y - 1;
            ctx.BeginFigure(new Point(start.X, y), false, false);
            bool up = true;
            for (double x = start.X + 2; x <= right + 0.1; x += 2)
            {
                ctx.LineTo(new Point(x, up ? y - 2 : y), true, false);
                up = !up;
            }
        }
        geometry.Freeze();
        return geometry;
    }
}
