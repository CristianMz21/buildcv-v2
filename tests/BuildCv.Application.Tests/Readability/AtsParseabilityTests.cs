using BuildCv.Application.Readability;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Readability;

// The fifth section, and the only one that grades a FILE. It is in a file of its own because the thing
// most worth pinning about it is not what it scores but what it does to the other four: turning it on
// changes every weight in the report, and the failure it can ship silently is a ceiling of 0.90 that
// nobody notices because every section still looks right on its own.
public class AtsParseabilityTests
{
    private static readonly DateOnly ReferenceDate = ReadabilityTestResumes.ReferenceDate;

    private readonly ReadabilityEngine _engine = new();

    private ReadabilityResult Evaluate(Resume resume) => _engine.Evaluate(resume, ReferenceDate);

    private ReadabilityBreakdown BreakdownOf(Resume resume) => Evaluate(resume).Breakdown;

    // THE TRAP TEST, and the reason this section could not ship without a scoring rule.
    //
    // AtsParseability carries 0.10 in the default weighting. Make it APPLICABLE while it still answers
    // zero and every candidate who imported a document is capped at 0.90 — ten points off, for a
    // question the product then never asks them. Make it applicable AND scorable and a clean upload
    // reaches 1.00, which is what this asserts.
    //
    // The weight assertion is not decoration. Without it a report that had renormalized the section
    // straight back OUT would also total 1.0, and this test would pass while proving the opposite of
    // what it claims.
    [Fact]
    public void A_perfect_single_column_pdf_with_a_text_layer_scores_one_hundred_not_ninety()
    {
        var result = Evaluate(ReadabilityTestResumes.FullyPopulated(ReadabilityTestResumes.CleanPdf));

        result.Breakdown.Weights.AtsParseability.Should().Be(0.10,
            "the section applies, so it must carry its weight rather than be renormalized away");
        result.Breakdown.AtsParseabilityScore.Should().Be(1.0);
        result.WeightedTotal.Should().BeApproximately(1.0, 1e-12,
            "a cleanly exported CV must reach the ceiling, not 0.90");
    }

    // And it needs no advice either — otherwise "scores 1.00" would be compatible with a report that
    // still told the candidate to fix their document.
    [Fact]
    public void A_perfect_single_column_pdf_produces_no_ats_advice()
    {
        Evaluate(ReadabilityTestResumes.FullyPopulated(ReadabilityTestResumes.CleanPdf))
            .Recommendations.Should()
            .NotContain(advice => advice.Section == ReadabilitySectionType.AtsParseability);
    }

    // RENORMALIZATION, BOTH DIRECTIONS, in one assertion each way. The applicable case must be the
    // UNRENORMALIZED default — identity, because every section applies — and the inapplicable case must
    // put the ATS weight at zero with the other four still totalling 1.0.
    [Fact]
    public void Signals_present_weights_the_section_at_its_default_share()
    {
        var weights = BreakdownOf(ReadabilityTestResumes.FullyPopulated(ReadabilityTestResumes.CleanPdf)).Weights;

        weights.Should().BeEquivalentTo(ReadabilityWeightsSnapshot.Default(),
            "with all five applicable the divisor is 1.0 and every weight is returned bit for bit");
    }

    [Fact]
    public void Signals_absent_renormalizes_the_section_out_and_the_other_four_still_total_one()
    {
        var weights = BreakdownOf(ReadabilityTestResumes.FullyPopulated()).Weights;

        weights.AtsParseability.Should().Be(0.0);
        (weights.Completeness + weights.Contact + weights.Achievements + weights.Chronology)
            .Should().BeApproximately(1.0, 1e-12);

        // Not merely non-zero: each of the four is its default share DIVIDED BY 0.90, which is what
        // "renormalized" means and what a test asserting only the sum would not distinguish from four
        // weights that had been hand-edited to add up.
        weights.Completeness.Should().BeApproximately(0.30 / 0.90, 1e-12);
        weights.Achievements.Should().BeApproximately(0.25 / 0.90, 1e-12);
    }

    // A HAND-BUILT CV IS UNAFFECTED. Same resume, no document behind it: same total, and no advice about
    // a file that does not exist.
    [Fact]
    public void A_hand_built_resume_still_scores_one_and_gets_no_document_advice()
    {
        var result = Evaluate(ReadabilityTestResumes.FullyPopulated());

        result.WeightedTotal.Should().BeApproximately(1.0, 1e-12);
        result.Recommendations.Should().BeEmpty();
    }

    // THE ACCEPTANCE CRITERION: a two-column upload lowers the score, and a hand-built CV does not.
    // Asserted as a comparison between two identical resumes so nothing but the document differs.
    [Fact]
    public void A_two_column_upload_lowers_the_score_against_the_same_cv_imported_cleanly()
    {
        var twoColumn = Evaluate(ReadabilityTestResumes.FullyPopulated(ReadabilityTestResumes.TwoColumnPdf));
        var clean = Evaluate(ReadabilityTestResumes.FullyPopulated(ReadabilityTestResumes.CleanPdf));
        var handBuilt = Evaluate(ReadabilityTestResumes.FullyPopulated());

        twoColumn.WeightedTotal.Should().BeLessThan(clean.WeightedTotal);
        handBuilt.WeightedTotal.Should().BeApproximately(clean.WeightedTotal, 1e-12,
            "a CV with no document behind it is neither credited nor charged for one");

        // Exactly half the section's weight, because the section is a share of two measurable things and
        // this document provides one of them. Stated as a number so a rule that quietly started scoring
        // the layout as all-or-nothing would fail here rather than pass the inequality above.
        (clean.WeightedTotal - twoColumn.WeightedTotal).Should().BeApproximately(0.05, 1e-12);
    }

    // THE SECTION'S WHOLE TRUTH TABLE, including the two cases that are easiest to get backwards:
    // Unknown must not be penalised, and a scanned document must not be excused.
    [Theory]
    // Everything measurable and everything met.
    [InlineData(ColumnLayout.Single, true, ImportWarningFlags.None, 1.0)]
    // Text yes, columns no: one of two.
    [InlineData(ColumnLayout.Multiple, true, ImportWarningFlags.None, 0.5)]
    // A scan that at least reads in one column: the other one of two.
    [InlineData(ColumnLayout.Single, false, ImportWarningFlags.None, 0.5)]
    // A scanned two-column PDF: neither.
    [InlineData(ColumnLayout.Multiple, false, ImportWarningFlags.None, 0.0)]
    // UNKNOWN IS NOT A PENALTY. A DOCX has no geometry, so the column term leaves the denominator and
    // the document is graded on the one thing that was measured. Scoring this 0.5 would charge the most
    // ATS-parseable format there is for the detector's silence.
    [InlineData(ColumnLayout.Unknown, true, ImportWarningFlags.None, 1.0)]
    // Unknown geometry AND no text: the denominator is one and nothing was met.
    [InlineData(ColumnLayout.Unknown, false, ImportWarningFlags.None, 0.0)]
    // An empty document fails the text term even though its format is text-bearing.
    [InlineData(ColumnLayout.Unknown, true, ImportWarningFlags.NoTextContent, 0.0)]
    [InlineData(ColumnLayout.Single, true, ImportWarningFlags.NoTextContent, 0.5)]
    public void The_section_is_the_share_of_measurable_signals_the_document_provided(
        ColumnLayout layout, bool hadTextLayer, ImportWarningFlags warnings, double expected)
    {
        var signals = ImportSignals.Create(layout, hadTextLayer, pageCount: 1, warnings);

        BreakdownOf(ReadabilityTestResumes.FullyPopulated(signals))
            .AtsParseabilityScore.Should().Be(expected);
    }

    // PAGE COUNT IS CARRIED AND NEVER SCORED, which is a claim about the code and not a preference: an
    // ATS parses a nine-page PDF exactly as well as a one-page one, and how long a CV should be is
    // advice about CONTENT that Completeness and Achievements already give.
    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(null)]
    public void Page_count_does_not_move_the_score(int? pageCount)
    {
        var signals = ImportSignals.Create(ColumnLayout.Single, hadTextLayer: true, pageCount);

        BreakdownOf(ReadabilityTestResumes.FullyPopulated(signals))
            .AtsParseabilityScore.Should().Be(1.0);
    }

    // Which sentence each gap produces. The two text-gap kinds are mutually exclusive by construction —
    // one extractor reports a missing text layer and the others report an empty document — so the pair
    // is asserted as "exactly this one, and not the other".
    [Fact]
    public void A_scanned_pdf_is_told_to_export_rather_than_scan()
    {
        var advice = Evaluate(ReadabilityTestResumes.FullyPopulated(ReadabilityTestResumes.ScannedPdf))
            .Recommendations;

        advice.Should().ContainSingle(item =>
            item.Kind == ReadabilityRecommendationKind.DocumentHasNoTextLayer);
        advice.Should().NotContain(item =>
            item.Kind == ReadabilityRecommendationKind.DocumentHasNoText);
        advice.Should().ContainSingle(item => item.Section == ReadabilitySectionType.AtsParseability,
            "a single-column scan has one gap, not two");
    }

    [Fact]
    public void An_empty_document_is_told_to_upload_the_real_file()
    {
        var advice = Evaluate(ReadabilityTestResumes.FullyPopulated(ReadabilityTestResumes.EmptyDocument))
            .Recommendations;

        advice.Should().ContainSingle(item =>
            item.Kind == ReadabilityRecommendationKind.DocumentHasNoText);
        advice.Should().NotContain(item =>
            item.Kind == ReadabilityRecommendationKind.DocumentHasNoTextLayer);
    }

    [Fact]
    public void A_two_column_pdf_is_told_to_re_export_in_one_column()
    {
        Evaluate(ReadabilityTestResumes.FullyPopulated(ReadabilityTestResumes.TwoColumnPdf))
            .Recommendations.Should().ContainSingle(item =>
                item.Kind == ReadabilityRecommendationKind.DocumentUsesMultipleColumns);
    }

    // NOTHING IS SAID ABOUT A LAYOUT NOBODY MEASURED. The column advice must key on Multiple, not on
    // "not Single" — the difference is invisible in the score, because Unknown leaves the denominator
    // either way, and visible only here.
    [Fact]
    public void An_unknown_layout_produces_no_column_advice()
    {
        Evaluate(ReadabilityTestResumes.FullyPopulated(ReadabilityTestResumes.PastedText))
            .Recommendations.Should().NotContain(item =>
                item.Kind == ReadabilityRecommendationKind.DocumentUsesMultipleColumns);
    }

    // Every sentence this section emits names the one step that actually changes the stored signals.
    // Without it the advice is unfollowable: re-exporting a file the server never kept changes nothing.
    [Theory]
    [InlineData(ReadabilityRecommendationKind.DocumentHasNoTextLayer)]
    [InlineData(ReadabilityRecommendationKind.DocumentUsesMultipleColumns)]
    [InlineData(ReadabilityRecommendationKind.DocumentHasNoText)]
    public void Every_document_recommendation_tells_the_candidate_to_import_again(
        ReadabilityRecommendationKind kind)
    {
        var signals = ImportSignals.Create(
            ColumnLayout.Multiple,
            hadTextLayer: kind != ReadabilityRecommendationKind.DocumentHasNoTextLayer,
            pageCount: 1,
            kind == ReadabilityRecommendationKind.DocumentHasNoText
                ? ImportWarningFlags.NoTextContent
                : ImportWarningFlags.None);

        var advice = Evaluate(ReadabilityTestResumes.FullyPopulated(signals))
            .Recommendations.Should().ContainSingle(item => item.Kind == kind).Subject;

        advice.Message.Should().Contain("import it again", "the file is never stored, so nothing else pays");
    }

    // A resume with no signals gets no advice about a document, even when every other section is empty
    // and the report is full of other things to fix.
    [Fact]
    public void An_empty_hand_built_resume_gets_advice_but_none_about_a_document()
    {
        var result = Evaluate(ReadabilityTestResumes.Empty());

        result.Recommendations.Should().NotBeEmpty();
        result.Recommendations.Should().NotContain(advice =>
            advice.Section == ReadabilitySectionType.AtsParseability);
    }
}
