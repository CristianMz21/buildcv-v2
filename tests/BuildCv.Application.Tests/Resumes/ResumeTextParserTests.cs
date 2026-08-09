using BuildCv.Application.Common.Services;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Resumes;

// The heuristic parser, exercised on plain strings — which is also THE plain-text path a candidate who
// pastes text gets: no geometry, everything except the two-column signal. The governing rule under test
// throughout: a field the parser is unsure about is EMPTY and FLAGGED, never guessed and silent.
public sealed class ResumeTextParserTests
{
    private const string OneColumnEnglishCv =
        """
        John Smith
        john.smith@example.com
        +1 (415) 555-0132
        San Francisco, USA
        linkedin.com/in/johnsmith

        SUMMARY
        Backend engineer with 8 years building payment systems.

        EXPERIENCE
        Senior Software Engineer
        Stripe
        03/2019 - Present
        Led the payments API team.

        Software Engineer
        Google
        06/2015 - 02/2019

        EDUCATION
        Bachelor of Computer Science
        MIT
        2011 - 2015

        SKILLS
        Python, SQL, Docker, Kubernetes, Go

        LANGUAGES
        English - Native
        Spanish - Professional
        """;

    [Fact]
    public void Parse_AOneColumnEnglishCv_ExtractsTheReliableFields()
    {
        var (draft, _) = ResumeTextParser.Parse(OneColumnEnglishCv);

        draft.Contact!.FullName.Should().Be("John Smith");
        draft.Contact.Email.Should().Be("john.smith@example.com");
        draft.Contact.PhoneNumber.Should().Contain("415");
        draft.Contact.Website.Should().Be("linkedin.com/in/johnsmith");
        draft.Contact.Location.Should().Be("San Francisco, USA");
        draft.Contact.Summary.Should().Contain("Backend engineer");

        draft.Skills!.Select(s => s!.Name).Should().Equal("Python", "SQL", "Docker", "Kubernetes", "Go");

        draft.Languages!.Should().HaveCount(2);
        draft.Languages![0]!.Name.Should().Be("English");
        draft.Languages[0]!.Level.Should().Be("Native");
        draft.Languages[1]!.Name.Should().Be("Spanish");
        draft.Languages[1]!.Level.Should().Be("Professional");

        draft.Experiences!.Should().HaveCount(2);
        draft.Experiences![0]!.Organization.Should().Be("Stripe");
        draft.Experiences[0]!.Position.Should().Be("Senior Software Engineer");

        draft.Educations!.Should().ContainSingle();
        draft.Educations![0]!.Institution.Should().Be("MIT");
        draft.Educations[0]!.Level.Should().Be("Bachelor");
    }

    // THE central guarantee, both halves asserted: a field the parser could not find is null on the draft
    // AND carries a NotExtracted provenance entry. Absent-but-confident is the defect; this is what makes
    // "absent" and "flagged" a single observable fact for the review screen.
    [Fact]
    public void Parse_AFieldTheParserCannotFind_IsAbsentAndFlagged()
    {
        const string noPhone =
            """
            María López
            maria.lopez@example.com

            SKILLS
            Python, SQL
            """;

        var (draft, confidence) = ResumeTextParser.Parse(noPhone);

        draft.Contact!.PhoneNumber.Should().BeNull("the CV states no phone number");
        Provenance(confidence, "contact.phoneNumber")!.Confidence
            .Should().Be(FieldConfidence.NotExtracted, "and the review screen must be told the parser looked");
    }

    // Spanish and English headings, and — critically — accented and unaccented spellings of the same
    // Spanish heading must land in the SAME section, because a PDF's encoding decides which arrives.
    [Theory]
    [InlineData("EDUCACIÓN")]
    [InlineData("EDUCACION")]
    [InlineData("Educación")]
    [InlineData("Formación Académica")]
    public void Parse_BilingualAndAccentedEducationHeadings_AreRecognised(string heading)
    {
        var text =
            $"""
            Ana Ruiz
            ana@example.com

            {heading}
            Licenciatura en Informática
            Universidad de Buenos Aires
            2012 - 2016
            """;

        var (draft, _) = ResumeTextParser.Parse(text);

        draft.Educations.Should().NotBeNull($"'{heading}' is an education heading");
        draft.Educations![0]!.Institution.Should().Be("Universidad de Buenos Aires");
        draft.Educations[0]!.Level.Should().Be("Bachelor", "'Licenciatura' maps to Bachelor");
    }

    // Never default a missing level. A skill with no stated level has a null Level; only an explicit
    // parenthetical fills it.
    [Fact]
    public void Parse_ASkillLevel_IsNeverInvented()
    {
        var (draft, _) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            SKILLS
            Python, SQL (Advanced)
            """);

        var python = draft.Skills!.Single(s => s!.Name == "Python");
        python!.Level.Should().BeNull("the source states no level for Python");

        var sql = draft.Skills!.Single(s => s!.Name == "SQL");
        sql!.Level.Should().Be("Advanced", "'(Advanced)' is an explicit level");
    }

    [Fact]
    public void Parse_ALanguageLevel_IsNeverInvented()
    {
        var (draft, _) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            IDIOMAS
            Francés
            Alemán - Nativo
            """);

        var french = draft.Languages!.Single(l => l!.Name == "Francés");
        french!.Level.Should().BeNull("the source states no proficiency for French");

        var german = draft.Languages!.Single(l => l!.Name == "Alemán");
        german!.Level.Should().Be("Native", "'Nativo' maps to Native");
    }

    // "Present" is recognised but never turned into today's date: the end is blank and flagged.
    [Fact]
    public void Parse_APresentEndDate_IsNeverInferred()
    {
        var (draft, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Engineer
            Acme
            01/03/2019 - Presente
            """);

        draft.Experiences![0]!.Start.Should().Be("2019-03-01", "a full start date is confident");
        draft.Experiences[0]!.End.Should().BeNull("'Presente' must not become today");
        Provenance(confidence, "experiences[0].end")!.Confidence.Should().Be(FieldConfidence.NotExtracted);
    }

    [Fact]
    public void Parse_AFullDate_BecomesIso()
    {
        var (draft, _) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Engineer
            Acme
            15/03/2019 - 20/06/2021
            """);

        draft.Experiences![0]!.Start.Should().Be("2019-03-15");
        draft.Experiences[0]!.End.Should().Be("2021-06-20");
    }

    // A month-and-year or year-only date is recognised but left blank and flagged, with the raw snippet
    // preserved so the candidate can complete it — no day is invented.
    [Fact]
    public void Parse_AYearOnlyDate_IsBlankAndFlaggedWithItsSource()
    {
        var (draft, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Engineer
            Acme
            2019 - 2021
            """);

        draft.Experiences![0]!.Start.Should().BeNull();
        var start = Provenance(confidence, "experiences[0].start")!;
        start.Confidence.Should().Be(FieldConfidence.NotExtracted);
        start.SourceText.Should().Be("2019");
    }

    [Fact]
    public void Parse_ExperienceType_IsNeverGuessed()
    {
        var (draft, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Volunteer Coordinator
            Red Cross
            15/03/2019 - 20/06/2021
            """);

        draft.Experiences![0]!.Type.Should().BeNull("no CV states Professional vs Volunteer machine-readably");
        Provenance(confidence, "experiences[0].type")!.Confidence.Should().Be(FieldConfidence.NotExtracted);
    }

    // Two columns are warned about, loudly and first, and drop the overall confidence to Low. The counter-
    // case pins that a single-column verdict does NOT raise the warning — so the warning tracks the
    // detector, not the text.
    [Fact]
    public void Parse_TwoColumnLayout_WarnsFirstAndLowersConfidence()
    {
        var multi = ResumeTextParser.Parse(OneColumnEnglishCv, ColumnLayout.Multiple);
        multi.Confidence.Warnings.Should().Contain(ResumeTextParser.TwoColumnWarning);
        multi.Confidence.Warnings[0].Should().Be(ResumeTextParser.TwoColumnWarning, "it must be shown first");
        multi.Confidence.Overall.Should().Be(OverallConfidence.Low);

        var single = ResumeTextParser.Parse(OneColumnEnglishCv, ColumnLayout.Single);
        single.Confidence.Warnings.Should().NotContain(ResumeTextParser.TwoColumnWarning);
    }

    // An unrecognised section closes the section above it and is skipped with a warning — the sections on
    // either side survive.
    [Fact]
    public void Parse_AnUnrecognisedSection_DoesNotLoseTheSectionsAroundIt()
    {
        var (draft, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            SKILLS
            Python, SQL

            DISPONIBILIDAD:
            Incorporación inmediata

            EDUCATION
            MIT
            2011 - 2015
            """);

        draft.Skills!.Select(s => s!.Name).Should().Equal("Python", "SQL");
        draft.Educations!.Should().ContainSingle().Which!.Institution.Should().Be("MIT");
        confidence.Warnings.Should().Contain(w => w.Contains("not recognised"));
    }

    [Fact]
    public void Parse_PlainTextWithNoGeometry_StillProducesADraftAndNoColumnWarning()
    {
        var (draft, confidence) = ResumeTextParser.Parse(OneColumnEnglishCv, ColumnLayout.Unknown);

        draft.Contact!.Email.Should().Be("john.smith@example.com");
        confidence.Warnings.Should().NotContain(ResumeTextParser.TwoColumnWarning,
            "Unknown means the parser could not see geometry, not that it is single-column");
    }

    [Fact]
    public void Parse_DocumentWarnings_ArePassedThrough()
    {
        var (_, confidence) = ResumeTextParser.Parse(
            "Sam Doe\nsam@example.com", ColumnLayout.Unknown, ["The PDF has no text layer."]);

        confidence.Warnings.Should().Contain("The PDF has no text layer.");
    }

    // FINDING B: a bare "Label:" line inside an experience body — "Responsibilities:", "Logros:",
    // "Funciones:", "Objetivo Profesional:" — must NOT reset the section and swallow every job beneath it.
    // The reviewer's exact reproduction: two jobs, a "Responsibilities:" line in the first job's body. The
    // second job (org, title, date) must survive, so TWO experiences come back.
    [Fact]
    public void Parse_ASubLabelInsideAnExperienceBody_DoesNotSwallowTheNextJob()
    {
        var (draft, _) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Senior Engineer
            Stripe
            15/03/2019 - 20/06/2021
            Responsibilities:
            Led the migration.

            Software Engineer
            Google
            06/2015 - 02/2019
            """);

        draft.Experiences!.Should().HaveCount(2, "the 'Responsibilities:' sub-label must not swallow the second job");
        draft.Experiences![0]!.Organization.Should().Be("Stripe");
        draft.Experiences[1]!.Organization.Should().Be("Google");
        draft.Experiences[1]!.Position.Should().Be("Software Engineer");
    }

    // FINDING A: a range whose leading span cannot be parsed — "Invierno 2020", a word+year whose word is
    // not a month — must NOT silently promote the surviving date into the START slot. The one parseable
    // date is the END; the unparseable start is left blank and flagged.
    [Fact]
    public void Parse_ARangeWhoseStartCannotBeParsed_DoesNotPromoteTheEndToStart()
    {
        var (draft, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Engineer
            Acme
            Invierno 2020 - 15/03/2021
            """);

        draft.Experiences![0]!.Start.Should().BeNull(
            "'Invierno 2020' is not parseable and must not be replaced by the end date");
        Provenance(confidence, "experiences[0].start")!.Confidence.Should().Be(FieldConfidence.NotExtracted);
        draft.Experiences[0]!.End.Should().Be("2021-03-15", "the one parseable date is the END, not the start");
    }

    // FINDING C: a job whose date uses 2-digit years ("03/95 - 06/98") must NOT vanish. The parser cannot
    // resolve a 2-digit year to a full date, but the ENTRY still surfaces (dates blank and flagged) so the
    // job before a later 4-digit-year job is not silently lost with no trace on the review screen.
    [Fact]
    public void Parse_AJobDatedWithTwoDigitYears_StillSurfacesInsteadOfVanishing()
    {
        var (draft, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Engineer
            OldCo
            03/95 - 06/98

            Senior Engineer
            NewCo
            15/03/2019 - 20/06/2021
            """);

        draft.Experiences!.Should().HaveCount(2, "the 2-digit-year job must not vanish before the 4-digit-year one");
        draft.Experiences![0]!.Organization.Should().Be("OldCo");
        draft.Experiences[1]!.Organization.Should().Be("NewCo");

        draft.Experiences[0]!.Start.Should().BeNull("a 2-digit year cannot be resolved to a full date");
        Provenance(confidence, "experiences[0].start")!.Confidence.Should().Be(FieldConfidence.NotExtracted);
        Provenance(confidence, "experiences[0].start")!.SourceText
            .Should().Be("03/95", "the raw snippet is preserved for the candidate to complete");
    }

    // FINDING HIGH: every field the parser reasons about carries a provenance entry, exactly as the
    // DraftConfidence comment claims. languages[i].fluency populates a real draft value, so it MUST have a
    // provenance entry — a populated field with no confidence is the defect. A skill with no level must
    // still emit a NotExtracted flag rather than being silently omitted.
    [Fact]
    public void Parse_EveryReasonedField_CarriesProvenance_IncludingLanguageFluency()
    {
        var (draft, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            SKILLS
            Python

            IDIOMAS
            Español - Nativo
            """);

        draft.Languages![0]!.Fluency.Should().Be("Nativo");
        Provenance(confidence, "languages[0].fluency").Should().NotBeNull("a populated fluency must carry provenance");
        Provenance(confidence, "languages[0].fluency")!.Confidence.Should().Be(FieldConfidence.Medium);

        var pythonLevel = Provenance(confidence, "skills[0].level");
        pythonLevel.Should().NotBeNull("a skill with no stated level must still be flagged, not omitted");
        pythonLevel!.Confidence.Should().Be(FieldConfidence.NotExtracted);
    }

    private static FieldProvenance? Provenance(DraftConfidence confidence, string path) =>
        confidence.Fields.FirstOrDefault(field => field.Path == path);
}
