namespace BuildCv.Infrastructure.Documents;

using BuildCv.Application.Common.Services;

/// <summary>One word's horizontal extent on a page, in PDF user-space units.</summary>
public readonly record struct WordBox(double Left, double Right);

/// <summary>
/// Decides whether a page's words are laid out in two (or more) columns, from their horizontal extents.
/// Pure geometry, no PdfPig — so the decision can be unit-tested with synthetic boxes and the adapter is
/// only responsible for turning a PDF into boxes.
/// </summary>
/// <remarks>
/// <para>
/// The signal is a GUTTER: a vertical strip in the middle band of the page that no word crosses, with a
/// substantial share of words strictly on each side of it. A single-column page has no such strip —
/// across many lines, some word covers almost every interior x — while a two-column page has an empty
/// channel between the columns that persists down the whole page.
/// </para>
/// <para>
/// Coverage (not the straddle count of individual words) is what distinguishes the layouts: individual
/// words are narrow, so few straddle any given x in EITHER layout. What differs is whether ANY word from
/// ANY line covers a given interior x. The gutter is where that count falls to near zero over a strip wide
/// enough to be a real channel rather than a chance alignment of inter-word spaces.
/// </para>
/// </remarks>
public static class ColumnGeometry
{
    // A page with fewer words than this cannot be judged — too little geometry to tell a gutter from a
    // sparse header. Reported Unknown, which never raises a column warning.
    private const int MinWords = 20;

    private const double BandStartFraction = 0.30;
    private const double BandEndFraction = 0.70;
    private const int Samples = 80;

    // A gutter sample is "empty" at no more than this share of the page's words, has this share of words
    // strictly on each side, and the empty run must span this fraction of the page width to count — a real
    // channel, not one aligned column of spaces.
    private const double EmptyFraction = 0.01;
    private const double MinSideFraction = 0.20;
    private const double MinGutterWidthFraction = 0.03;

    public static ColumnLayout DetectPage(IReadOnlyList<WordBox> words, double pageWidth)
    {
        if (words.Count < MinWords || pageWidth <= 0)
            return ColumnLayout.Unknown;

        var bandStart = pageWidth * BandStartFraction;
        var bandEnd = pageWidth * BandEndFraction;
        var sampleWidth = (bandEnd - bandStart) / Samples;
        var emptyThreshold = Math.Max(1, words.Count * EmptyFraction);
        var minSide = words.Count * MinSideFraction;
        var requiredRun = (int)Math.Ceiling(pageWidth * MinGutterWidthFraction / sampleWidth);

        var run = 0;
        for (var k = 0; k <= Samples; k++)
        {
            var c = bandStart + sampleWidth * k;

            var coverage = 0;
            var leftOfC = 0;
            var rightOfC = 0;
            foreach (var word in words)
            {
                if (word.Left <= c && c <= word.Right)
                    coverage++;
                else if (word.Right < c)
                    leftOfC++;
                else if (word.Left > c)
                    rightOfC++;
            }

            var isGutter = coverage <= emptyThreshold && leftOfC >= minSide && rightOfC >= minSide;
            if (!isGutter)
            {
                run = 0;
                continue;
            }

            run++;
            if (run >= requiredRun)
                return ColumnLayout.Multiple;
        }

        return ColumnLayout.Single;
    }

    /// <summary>
    /// Aggregates per-page verdicts: any two-column page makes the document two-column; otherwise
    /// single-column if any page could be judged; otherwise Unknown.
    /// </summary>
    public static ColumnLayout Combine(IEnumerable<ColumnLayout> pages)
    {
        var sawSingle = false;
        foreach (var page in pages)
        {
            if (page == ColumnLayout.Multiple)
                return ColumnLayout.Multiple;
            if (page == ColumnLayout.Single)
                sawSingle = true;
        }

        return sawSingle ? ColumnLayout.Single : ColumnLayout.Unknown;
    }
}
