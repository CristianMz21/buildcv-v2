using System.Text;
using BuildCv.Application.Common.Observability;
using BuildCv.Application.Common.Services;
using BuildCv.Application.Resumes;
using BuildCv.Infrastructure.Documents;
using FluentAssertions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit.Abstractions;

namespace BuildCv.Infrastructure.Tests.Documents;

// The LABELLED CORPUS and the per-field accuracy floor. Synthetic CVs whose ground truth the test wrote,
// run through the real extraction pipeline — text extractor + PDF column detector + ResumeTextParser —
// exactly as the endpoint does. The number this prints is the regression gate: a change that drops it
// fails here rather than merely disappointing someone.
//
// The floor is set at what the parser ACTUALLY achieves on this corpus, recorded after the corpus was
// written — NOT a target the heuristics were tuned to hit. The two-column case is included precisely so
// the number reflects the dominant real-world failure rather than hiding it.
public sealed class ResumeExtractionCorpusTests
{
    // MEASURED at 95.7% (66/69) over 9 CVs after the parser-correctness fixes (findings A/B/C) added their
    // three regression samples; the original six measured 94.2% (49/52). The three remaining misses are
    // genuine (a two-column organisation that kept a leading "at", and a reversed employer/title order the
    // parser swaps) — the corpus is not a parser grading itself. The corpus uses only Standard-14 PDF fonts
    // and UTF-8 plain text, so extraction is deterministic across platforms and this floor is not flaky. A
    // change that breaks several labels trips it. The floor stays 0.90 (a regression gate with headroom),
    // not the measured number; raising it is only honest after a real parser improvement re-measures higher.
    private const double PerFieldAccuracyFloor = 0.90;

    // Unscoped; see the note in DocumentTextExtractionTests.
    private static readonly BuildCvMetrics Metrics = new();

    private static readonly DocumentTextExtractor Extractor =
        new(new PdfPigTextExtractor(Metrics), new OpenXmlDocxTextExtractor(Metrics), new PlainTextExtractor(Metrics), Metrics);

    private static readonly PdfColumnDetector ColumnDetector = new();

    private readonly ITestOutputHelper _output;

    public ResumeExtractionCorpusTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PerFieldAccuracy_OverTheCorpus_MeetsTheRecordedFloor()
    {
        var corpus = BuildCorpus();
        var correct = 0;
        var total = 0;

        foreach (var sample in corpus)
        {
            var extraction = await Extractor.ExtractAsync(new MemoryStream(sample.Bytes), sample.ContentType);
            extraction.IsSuccess.Should().BeTrue($"{sample.Name}: {extraction.Error}");

            var layout = sample.ContentType == "application/pdf"
                ? await ColumnDetector.DetectAsync(new MemoryStream(sample.Bytes))
                : ColumnLayout.Unknown;

            var (draft, _) = ResumeTextParser.Parse(extraction.Value!.Text, layout, extraction.Value.Warnings);

            var hits = 0;
            foreach (var label in sample.Labels)
            {
                var actual = label.Actual(draft);
                var ok = Matches(actual, label.Expected);
                total++;
                if (ok)
                {
                    correct++;
                    hits++;
                }
                else
                {
                    _output.WriteLine($"  MISS {sample.Name}/{label.Name}: expected [{label.Expected ?? "<blank>"}] got [{actual ?? "<blank>"}]");
                }
            }

            _output.WriteLine($"{sample.Name}: {hits}/{sample.Labels.Count}");
        }

        var accuracy = (double)correct / total;
        _output.WriteLine($"PER-FIELD ACCURACY: {accuracy:P1} ({correct}/{total}) over {corpus.Count} CVs");

        accuracy.Should().BeGreaterThanOrEqualTo(PerFieldAccuracyFloor,
            "the corpus is the regression gate; a drop below the recorded floor is a regression");
    }

    // The two-column case in the corpus must raise the warning — the same signal the accuracy hit above
    // reflects. Pinned here so the detector cannot be quietly disabled without a red test.
    [Fact]
    public async Task TheTwoColumnCorpusCase_RaisesTheColumnWarning()
    {
        var sample = BuildCorpus().Single(c => c.Name == "two-column-en");

        var extraction = await Extractor.ExtractAsync(new MemoryStream(sample.Bytes), sample.ContentType);
        var layout = await ColumnDetector.DetectAsync(new MemoryStream(sample.Bytes));
        var (_, confidence) = ResumeTextParser.Parse(extraction.Value!.Text, layout, extraction.Value.Warnings);

        layout.Should().Be(ColumnLayout.Multiple);
        confidence.Warnings.Should().Contain(ResumeTextParser.TwoColumnWarning);
        confidence.Overall.Should().Be(OverallConfidence.Low);
    }

    // ---------------------------------------------------------------- corpus

    private sealed record Label(string Name, string? Expected, Func<ResumeDraft, string?> Actual);

    private sealed record Sample(string Name, byte[] Bytes, string ContentType, IReadOnlyList<Label> Labels);

    private static List<Sample> BuildCorpus() =>
    [
        EnglishOneColumn(),
        SpanishPlainText(),
        NoBlankLines(),
        WithUnknownSection(),
        TwoColumn(),
        ReversedOrder(),
        SubLabelInsideExperience(),
        UnparseableRangeStart(),
        TwoDigitYearDates()
    ];

    // English, one column, full numeric dates — the case the parser should do best on.
    private static Sample EnglishOneColumn()
    {
        var lines = new[]
        {
            "John Smith",
            "john.smith@example.com",
            "+14155550132",
            "San Francisco, USA",
            "johnsmith.dev",
            "",
            "SUMMARY",
            "Backend engineer who builds payment systems.",
            "",
            "EXPERIENCE",
            "Senior Engineer",
            "Stripe",
            "15/03/2019 - 20/06/2021",
            "",
            "EDUCATION",
            "Bachelor of Science",
            "MIT",
            "01/09/2011 - 15/06/2015",
            "",
            "SKILLS",
            "Python, SQL, Docker",
            "",
            "LANGUAGES",
            "English - Native",
            "Spanish - Professional",
        };

        return new Sample("english-one-column", OneColumnPdf(HelveticaLines(lines)), "application/pdf",
        [
            new("name", "John Smith", d => d.Contact?.FullName),
            new("email", "john.smith@example.com", d => d.Contact?.Email),
            new("phone", "+14155550132", d => d.Contact?.PhoneNumber),
            new("website", "johnsmith.dev", d => d.Contact?.Website),
            new("location", "San Francisco, USA", d => d.Contact?.Location),
            new("skill.python", "Python", d => Skill(d, "Python")),
            new("skill.sql", "SQL", d => Skill(d, "SQL")),
            new("skill.docker", "Docker", d => Skill(d, "Docker")),
            new("lang.english", "Native", d => LanguageLevel(d, "English")),
            new("lang.spanish", "Professional", d => LanguageLevel(d, "Spanish")),
            new("exp.org", "Stripe", d => Experience(d, "Stripe")?.Organization),
            new("exp.start", "2019-03-15", d => Experience(d, "Stripe")?.Start),
            new("exp.end", "2021-06-20", d => Experience(d, "Stripe")?.End),
            new("edu.institution", "MIT", d => Education(d, "MIT")?.Institution),
            new("edu.level", "Bachelor", d => Education(d, "MIT")?.Level),
            new("edu.start", "2011-09-01", d => Education(d, "MIT")?.Start),
        ]);
    }

    // Spanish, plain text (UTF-8, accents intact, no font needed), month-and-year dates the parser must
    // recognise but leave BLANK rather than invent a day for.
    private static Sample SpanishPlainText()
    {
        const string text =
            """
            María López García
            maria.lopez@example.com
            +34600112233
            Madrid, España

            EXPERIENCIA LABORAL
            Ingeniera de Software
            Telefónica
            ene 2019 - Actualidad

            EDUCACIÓN
            Grado en Informática
            Universidad Complutense
            2013 - 2017

            HABILIDADES
            Java, Spring, Kubernetes

            IDIOMAS
            Español - Nativo
            Inglés - Avanzado
            """;

        return new Sample("spanish-plain-text", Encoding.UTF8.GetBytes(text), "text/plain",
        [
            new("name", "María López García", d => d.Contact?.FullName),
            new("email", "maria.lopez@example.com", d => d.Contact?.Email),
            new("phone", "+34600112233", d => d.Contact?.PhoneNumber),
            new("location", "Madrid, España", d => d.Contact?.Location),
            new("skill.java", "Java", d => Skill(d, "Java")),
            new("skill.spring", "Spring", d => Skill(d, "Spring")),
            new("lang.spanish", "Native", d => LanguageLevel(d, "Español")),
            new("exp.org", "Telefónica", d => Experience(d, "Telefónica")?.Organization),
            // A month-and-year start is recognised but left blank — no invented day.
            new("exp.start.blank", null, d => Experience(d, "Telefónica")?.Start),
            new("exp.end.present.blank", null, d => Experience(d, "Telefónica")?.End),
            new("edu.institution", "Universidad Complutense", d => Education(d, "Universidad Complutense")?.Institution),
            new("edu.level", "Bachelor", d => Education(d, "Universidad Complutense")?.Level),
        ]);
    }

    // Headings with NO blank line between them and their content — segmentation must work off the heading
    // keyword, not off blank-line separation.
    private static Sample NoBlankLines()
    {
        var lines = new[]
        {
            "Chen Wei",
            "chen.wei@example.com",
            "SKILLS",
            "Go, Rust, Kubernetes",
            "LANGUAGES",
            "English - Fluent",
            "Mandarin - Native",
        };

        return new Sample("no-blank-lines", OneColumnPdf(HelveticaLines(lines)), "application/pdf",
        [
            new("name", "Chen Wei", d => d.Contact?.FullName),
            new("email", "chen.wei@example.com", d => d.Contact?.Email),
            new("skill.go", "Go", d => Skill(d, "Go")),
            new("skill.rust", "Rust", d => Skill(d, "Rust")),
            new("lang.english", "Fluent", d => LanguageLevel(d, "English")),
            new("lang.mandarin", "Native", d => LanguageLevel(d, "Mandarin")),
        ]);
    }

    // An unrecognised section between two known ones. The sections around it must survive.
    private static Sample WithUnknownSection()
    {
        var lines = new[]
        {
            "Aisha Khan",
            "aisha.khan@example.com",
            "",
            "SKILLS",
            "TypeScript, React, Node",
            "",
            "DISPONIBILIDAD:",
            "Immediate start, remote only",
            "",
            "EDUCATION",
            "Master of Engineering",
            "Cambridge",
            "01/10/2016 - 30/06/2018",
        };

        return new Sample("unknown-section", OneColumnPdf(HelveticaLines(lines)), "application/pdf",
        [
            new("name", "Aisha Khan", d => d.Contact?.FullName),
            new("skill.typescript", "TypeScript", d => Skill(d, "TypeScript")),
            new("skill.react", "React", d => Skill(d, "React")),
            new("edu.institution", "Cambridge", d => Education(d, "Cambridge")?.Institution),
            new("edu.level", "Master", d => Education(d, "Cambridge")?.Level),
            new("edu.start", "2016-10-01", d => Education(d, "Cambridge")?.Start),
        ]);
    }

    // Two columns, written row by row so the text extractor interleaves them. This is the dominant real
    // failure: the parser produces plausible-but-wrong output, and the column warning is what saves the
    // candidate. Its labels are the TRUE values, so the parser scores LOW here — that hit is the honest
    // 35% the number must reflect.
    private static Sample TwoColumn()
    {
        var contact = new[]
        {
            "Diego Torres",
            "diego.torres@example.com",
        };
        string[] left = ["SKILLS", "Python", "Django", "Postgres", "Redis", "Celery", "Docker"];
        string[] right = ["EXPERIENCE", "Backend Dev", "at Mercado", "2018 to 2022", "built the API", "led migrations", "on call"];

        return new Sample("two-column-en", TwoColumnPdf(contact, left, right), "application/pdf",
        [
            new("name", "Diego Torres", d => d.Contact?.FullName),
            new("email", "diego.torres@example.com", d => d.Contact?.Email),
            new("skill.python", "Python", d => Skill(d, "Python")),
            new("skill.django", "Django", d => Skill(d, "Django")),
            new("exp.org", "Mercado", d => Experience(d, "Mercado")?.Organization),
        ]);
    }

    // The genuine ambiguity the parser cannot resolve: this CV lists the employer ABOVE the job title,
    // the opposite of the "Title then Company" order SplitContext assumes. The parser therefore SWAPS
    // organisation and position — a wrong-but-flagged (Low) guess the review screen shows for the
    // candidate to correct. Labelled with the TRUE values, so these are honest misses that keep the corpus
    // from being a parser that only grades itself.
    private static Sample ReversedOrder()
    {
        var lines = new[]
        {
            "Liam O'Brien",
            "liam.obrien@example.com",
            "",
            "EXPERIENCE",
            "DataCorp",
            "Data Analyst",
            "15/01/2020 - 10/12/2022",
            "",
            "SKILLS",
            "Excel / SQL / Tableau",
        };

        return new Sample("reversed-order", OneColumnPdf(HelveticaLines(lines)), "application/pdf",
        [
            new("name", "Liam O'Brien", d => d.Contact?.FullName),
            new("email", "liam.obrien@example.com", d => d.Contact?.Email),
            new("exp.org", "DataCorp", d => d.Experiences?.FirstOrDefault()?.Organization),
            new("exp.position", "Data Analyst", d => d.Experiences?.FirstOrDefault()?.Position),
            new("exp.start", "2020-01-15", d => d.Experiences?.FirstOrDefault()?.Start),
            new("skill.excel", "Excel", d => Skill(d, "Excel")),
            new("skill.tableau", "Tableau", d => Skill(d, "Tableau")),
        ]);
    }

    // FINDING B in the corpus: a bare "Responsibilities:" sub-label inside the first job's body must not
    // swallow the second job. Both experiences must survive — this used to yield ONE.
    private static Sample SubLabelInsideExperience()
    {
        const string text =
            """
            Bruno Costa
            bruno.costa@example.com

            EXPERIENCE
            Senior Engineer
            Stripe
            15/03/2019 - 20/06/2021
            Responsibilities:
            Led the payments migration.

            Software Engineer
            Google
            15/06/2015 - 10/02/2019
            """;

        return new Sample("sub-label-experience", Encoding.UTF8.GetBytes(text), "text/plain",
        [
            new("name", "Bruno Costa", d => d.Contact?.FullName),
            new("email", "bruno.costa@example.com", d => d.Contact?.Email),
            new("exp.stripe.org", "Stripe", d => Experience(d, "Stripe")?.Organization),
            new("exp.stripe.position", "Senior Engineer", d => Experience(d, "Stripe")?.Position),
            new("exp.google.org", "Google", d => Experience(d, "Google")?.Organization),
            new("exp.google.position", "Software Engineer", d => Experience(d, "Google")?.Position),
        ]);
    }

    // FINDING A in the corpus: a range whose leading span is an unparseable word+year ("Invierno 2020")
    // must not promote the surviving date into the start slot. Start stays blank; the parseable date is the
    // END — this used to write the end date AS the start.
    private static Sample UnparseableRangeStart()
    {
        const string text =
            """
            Carla Ruiz
            carla.ruiz@example.com

            EXPERIENCE
            Analista
            Acme
            Invierno 2020 - 15/03/2021
            """;

        return new Sample("unparseable-range-start", Encoding.UTF8.GetBytes(text), "text/plain",
        [
            new("name", "Carla Ruiz", d => d.Contact?.FullName),
            new("email", "carla.ruiz@example.com", d => d.Contact?.Email),
            new("exp.org", "Acme", d => Experience(d, "Acme")?.Organization),
            // The unparseable start stays blank; the one real date is the END, not silently promoted.
            new("exp.start.blank", null, d => Experience(d, "Acme")?.Start),
            new("exp.end", "2021-03-15", d => Experience(d, "Acme")?.End),
        ]);
    }

    // FINDING C in the corpus: a job dated with 2-digit years ("03/95 - 06/98") must still surface — its
    // dates blank and flagged — instead of vanishing entirely before a later 4-digit-year job.
    private static Sample TwoDigitYearDates()
    {
        const string text =
            """
            Diego Marín
            diego.marin@example.com

            EXPERIENCE
            Ingeniero
            OldCo
            03/95 - 06/98

            Ingeniero Senior
            NewCo
            15/03/2019 - 20/06/2021
            """;

        return new Sample("two-digit-year-dates", Encoding.UTF8.GetBytes(text), "text/plain",
        [
            new("name", "Diego Marín", d => d.Contact?.FullName),
            new("email", "diego.marin@example.com", d => d.Contact?.Email),
            new("exp.oldco.org", "OldCo", d => Experience(d, "OldCo")?.Organization),
            // A 2-digit year is recognised but never resolved to a full date — surfaced, not lost.
            new("exp.oldco.start.blank", null, d => Experience(d, "OldCo")?.Start),
            new("exp.newco.org", "NewCo", d => Experience(d, "NewCo")?.Organization),
            new("exp.newco.start", "2019-03-15", d => Experience(d, "NewCo")?.Start),
        ]);
    }

    // ---------------------------------------------------------------- label helpers

    private static string? Skill(ResumeDraft draft, string name) =>
        draft.Skills?.FirstOrDefault(s => string.Equals(s?.Name, name, StringComparison.OrdinalIgnoreCase))?.Name;

    private static string? LanguageLevel(ResumeDraft draft, string name) =>
        draft.Languages?.FirstOrDefault(l => string.Equals(l?.Name, name, StringComparison.OrdinalIgnoreCase))?.Level;

    private static ExperienceDraft? Experience(ResumeDraft draft, string organization) =>
        draft.Experiences?.FirstOrDefault(e =>
            e?.Organization is { } o && o.Contains(organization, StringComparison.OrdinalIgnoreCase));

    private static EducationDraft? Education(ResumeDraft draft, string institution) =>
        draft.Educations?.FirstOrDefault(e =>
            e?.Institution is { } i && i.Contains(institution, StringComparison.OrdinalIgnoreCase));

    private static bool Matches(string? actual, string? expected)
    {
        if (actual is null || expected is null)
            return actual is null && expected is null;
        return string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- PDF rendering

    private sealed record RenderLine(string Text);

    private static IReadOnlyList<RenderLine> HelveticaLines(string[] lines) =>
        lines.Select(line => new RenderLine(line)).ToList();

    private static byte[] OneColumnPdf(IReadOnlyList<RenderLine> lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var baseline = 790d;
        foreach (var line in lines)
        {
            if (line.Text.Length > 0)
                page.AddText(line.Text, 12, new PdfPoint(40, baseline), font);
            baseline -= 22;
        }

        return builder.Build();
    }

    private static byte[] TwoColumnPdf(string[] contactRows, string[] leftRows, string[] rightRows)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var baseline = 790d;

        foreach (var row in contactRows)
        {
            page.AddText(row, 12, new PdfPoint(40, baseline), font);
            baseline -= 22;
        }

        baseline -= 22;
        var columnsTop = baseline;
        for (var i = 0; i < leftRows.Length; i++)
            page.AddText(leftRows[i], 12, new PdfPoint(40, columnsTop - i * 22), font);
        for (var i = 0; i < rightRows.Length; i++)
            page.AddText(rightRows[i], 12, new PdfPoint(330, columnsTop - i * 22), font);

        return builder.Build();
    }
}
