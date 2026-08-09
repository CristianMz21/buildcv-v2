using BuildCv.Application.Common.Services;
using BuildCv.Domain.Resumes;
using BuildCv.Infrastructure.Documents;
using FluentAssertions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace BuildCv.Infrastructure.Tests.Documents;

// Column detection in two layers: the pure geometry (ColumnGeometry, synthetic boxes, no PdfPig) and the
// PdfPig adapter over real generated PDFs. Splitting them means the algorithm is pinned independently of
// whether PdfPig produced the boxes it was fed.
public sealed class ColumnDetectionTests
{
    private const double PageWidth = 600;

    // ---------------------------------------------------------------- pure geometry

    // Dense, single-column text: many words scattered across the width, so no vertical channel is ever
    // empty. Seeded so the "scatter" is deterministic.
    [Fact]
    public void DetectPage_DenseSingleColumn_IsSingle()
    {
        var words = ScatteredWords(count: 200, left: 50, right: 540, seed: 1);

        ColumnGeometry.DetectPage(words, PageWidth).Should().Be(ColumnLayout.Single);
    }

    // Two blocks with an empty gutter between them: words on the left half, words on the right half, and
    // nothing in the channel down the middle.
    [Fact]
    public void DetectPage_TwoBlocksWithAGutter_IsMultiple()
    {
        var left = ScatteredWords(count: 100, left: 50, right: 250, seed: 2);
        var right = ScatteredWords(count: 100, left: 330, right: 550, seed: 3);

        ColumnGeometry.DetectPage([.. left, .. right], PageWidth).Should().Be(ColumnLayout.Multiple);
    }

    // Too few words to judge — a sparse header page — is Unknown, which never raises a warning.
    [Fact]
    public void DetectPage_TooFewWords_IsUnknown()
    {
        var words = ScatteredWords(count: 8, left: 50, right: 540, seed: 4);

        ColumnGeometry.DetectPage(words, PageWidth).Should().Be(ColumnLayout.Unknown);
    }

    // One heavy column and a handful of words on the other side is NOT two columns: the light side is
    // below the 20% share a real second column carries.
    [Fact]
    public void DetectPage_OneColumnWithAFewStragglersOpposite_IsSingle()
    {
        var main = ScatteredWords(count: 100, left: 50, right: 250, seed: 5);
        var stragglers = ScatteredWords(count: 3, left: 330, right: 550, seed: 6);

        ColumnGeometry.DetectPage([.. main, .. stragglers], PageWidth).Should().Be(ColumnLayout.Single);
    }

    [Fact]
    public void Combine_AnyMultiplePageWins()
    {
        ColumnGeometry.Combine([ColumnLayout.Single, ColumnLayout.Multiple, ColumnLayout.Single])
            .Should().Be(ColumnLayout.Multiple);
        ColumnGeometry.Combine([ColumnLayout.Single, ColumnLayout.Unknown]).Should().Be(ColumnLayout.Single);
        ColumnGeometry.Combine([ColumnLayout.Unknown, ColumnLayout.Unknown]).Should().Be(ColumnLayout.Unknown);
    }

    // ---------------------------------------------------------------- PdfPig adapter

    [Fact]
    public async Task DetectAsync_AOneColumnPdf_IsSingle()
    {
        var pdf = OneColumnPdf(
            "John Smith backend engineer",
            "Experience at several companies over ten years",
            "Skills include Python SQL Docker and Kubernetes",
            "Education from a well known university",
            "Languages English and Spanish spoken fluently",
            "References available upon request from managers",
            "Summary of a long and varied engineering career");

        var layout = await new PdfColumnDetector().DetectAsync(new MemoryStream(pdf));

        layout.Should().Be(ColumnLayout.Single);
    }

    // The two-column control: the same words split into two columns with a gutter must read as Multiple,
    // and disabling the detector (returning without the geometry) would make ResumeTextParser drop the
    // warning — which is what the corpus and the API test would then catch.
    [Fact]
    public async Task DetectAsync_ATwoColumnPdf_IsMultiple()
    {
        var pdf = TwoColumnPdf(
            leftRows:
            [
                "Python developer", "SQL and Postgres", "Docker Kubernetes",
                "Amazon Web Services", "React and Node", "Git and CI CD"
            ],
            rightRows:
            [
                "Senior Engineer role", "at a payments company", "led a small team",
                "shipped the billing API", "mentored three juniors", "on call rotation"
            ]);

        var layout = await new PdfColumnDetector().DetectAsync(new MemoryStream(pdf));

        layout.Should().Be(ColumnLayout.Multiple);
    }

    // Best-effort by contract: bytes that are not a readable PDF yield Unknown, never an exception — the
    // text is already extracted by the time this runs, so a geometry failure is just a missing warning.
    [Fact]
    public async Task DetectAsync_UnreadableBytes_IsUnknownNotAnException()
    {
        var layout = await new PdfColumnDetector().DetectAsync(new MemoryStream([1, 2, 3, 4]));

        layout.Should().Be(ColumnLayout.Unknown);
    }

    // ---------------------------------------------------------------- fixtures

    private static IReadOnlyList<WordBox> ScatteredWords(int count, double left, double right, int seed)
    {
        var random = new Random(seed);
        var words = new List<WordBox>(count);
        for (var i = 0; i < count; i++)
        {
            var start = left + random.NextDouble() * (right - left - 20);
            words.Add(new WordBox(start, start + 20));
        }

        return words;
    }

    private static byte[] OneColumnPdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var baseline = 780d;
        foreach (var line in lines)
        {
            page.AddText(line, 12, new PdfPoint(40, baseline), font);
            baseline -= 24;
        }

        return builder.Build();
    }

    // Two columns written row by row so the left cell sits at x=40 and the right cell at x=330, with an
    // empty channel between them — the geometry a two-column CV has.
    private static byte[] TwoColumnPdf(string[] leftRows, string[] rightRows)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var baseline = 780d;
        for (var row = 0; row < Math.Max(leftRows.Length, rightRows.Length); row++)
        {
            if (row < leftRows.Length)
                page.AddText(leftRows[row], 12, new PdfPoint(40, baseline), font);
            if (row < rightRows.Length)
                page.AddText(rightRows[row], 12, new PdfPoint(330, baseline), font);
            baseline -= 24;
        }

        return builder.Build();
    }
}
