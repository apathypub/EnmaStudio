using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace EnmaStudio.Editor;

/// <summary>Paints every find match behind the text; the current one stronger and outlined.</summary>
public sealed class SearchHighlighter : IBackgroundRenderer
{
    private static readonly Brush MatchBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0xE8, 0xC4, 0x40)));
    private static readonly Brush CurrentBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xE8, 0xC4, 0x40)));
    private static readonly Pen CurrentPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xC4, 0x40))), 1));

    public IReadOnlyList<ISegment> Matches { get; set; } = [];
    public ISegment? Current { get; set; }

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Matches.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;

        int viewStart = textView.VisualLines[0].FirstDocumentLine.Offset;
        int viewEnd = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (var match in Matches)
        {
            if (match.EndOffset < viewStart || match.Offset > viewEnd) continue;
            bool current = ReferenceEquals(match, Current);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, match))
                drawingContext.DrawRoundedRectangle(current ? CurrentBrush : MatchBrush, current ? CurrentPen : null, rect, 2, 2);
        }
    }

    private static T Frozen<T>(T freezable) where T : System.Windows.Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
