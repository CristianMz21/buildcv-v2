using BuildCv.Application.Common.Observability;
using BuildCv.Application.Resumes;
using BuildCv.Infrastructure.Documents;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Documents;

/// <summary>
/// Runs the real extractor and the real parser over a real CV, and asserts what must be true of any of
/// them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in, because a real CV is somebody's personal data and does not belong in this repository.</b>
/// Point it at one and it runs; leave it unset and it passes without asserting:
/// </para>
/// <code>
/// BUILDCV_SAMPLE_CV=/path/to/cv.pdf dotnet test tests/BuildCv.Infrastructure.Tests/…
/// </code>
/// <para>
/// <b>It exists because hand-written fixtures agreed with three broken versions of this parser in a
/// row.</b> Each was reasoned from `pdftotext` output and each shipped, deployed and changed nothing —
/// the running system reads what PdfPig's <c>ContentOrderTextExtractor</c> produces, which has no
/// indentation, blank lines between some bullets and not others, and wrapped lines ending in things like
/// "70%". No fixture anybody types by hand has those three properties at once, and every one of them was
/// load-bearing.
/// </para>
/// <para>
/// The assertions are deliberately about SHAPE rather than about one document's content, so they hold
/// for whatever CV somebody points this at. A shape check that passes on every real CV and fails on the
/// corruption this parser produced is worth more than an exact-match test on one file nobody else has.
/// </para>
/// </remarks>
public class RealDocumentImportTests
{
    private const string PathVariable = "BUILDCV_SAMPLE_CV";

    [Fact]
    public async Task EveryExperienceReadFromARealCv_NamesARoleRatherThanAnAchievement()
    {
        var path = Environment.GetEnvironmentVariable(PathVariable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        await using var stream = File.OpenRead(path);
        var text = new PdfPigTextExtractor(new BuildCvMetrics())
            .Extract(stream, CancellationToken.None).Value?.Text ?? string.Empty;

        var (draft, _) = ResumeTextParser.Parse(text);

        draft.Contact.Should().NotBeNull();
        draft.Experiences.Should().NotBeNullOrEmpty("a real CV has work history the parser must find");

        foreach (var experience in draft.Experiences!)
        {
            var position = experience!.Position;
            var organization = experience.Organization;

            // A blank field is the failure this parser started with: the review screen showed
            // "Value is required" on a row the candidate never wrote and could not fill.
            position.Should().NotBeNullOrWhiteSpace();
            organization.Should().NotBeNullOrWhiteSpace();

            // AND THE MORE DANGEROUS ONE. When bullets leaked into the context, the title became a
            // sentence — "Implemented semantic search with Meilisearch…", "dashboards." — which looks
            // filled in and gets saved. A job title is a noun phrase: it does not end in a full stop and
            // it is not the length of a sentence.
            position!.Should().NotEndWith(".", "a job title is not a sentence");
            position.Length.Should().BeLessThan(
                80, $"'{position}' is prose, not a role — a bullet leaked into the context");

            organization!.Length.Should().BeLessThan(
                80, $"'{organization}' is prose, not an employer");
        }
    }
}
