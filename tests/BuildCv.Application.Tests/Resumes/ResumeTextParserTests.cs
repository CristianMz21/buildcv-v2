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

    // A year-only date arrives as a YEAR, not as a blank and not as the first of January: the draft's
    // date fields carry whatever precision their source stated. The source snippet is still preserved,
    // because the candidate may want to make it more precise on the review screen.
    [Fact]
    public void Parse_AYearOnlyDate_ArrivesAsAYearWithItsSource()
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

        draft.Experiences![0]!.Start.Should().Be("2019");
        draft.Experiences[0]!.End.Should().Be("2021");

        var start = Provenance(confidence, "experiences[0].start")!;
        start.Confidence.Should().Be(FieldConfidence.Medium);
        start.SourceText.Should().Be("2019");
    }

    // THE FORMAT THIS WHOLE CHANGE EXISTS FOR, in the parser: a Spanish month name and a year, in both
    // slots. It used to arrive blank and flagged on every job on the CV.
    [Fact]
    public void Parse_AMonthNameAndYearRange_ArrivesAsTwoMonthPrecisionDates()
    {
        var (draft, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Engineer
            Acme
            junio 2015 - febrero 2019
            """);

        draft.Experiences![0]!.Start.Should().Be("2015-06");
        draft.Experiences[0]!.End.Should().Be("2019-02");

        Provenance(confidence, "experiences[0].start")!.Confidence.Should().Be(FieldConfidence.Medium);
        Provenance(confidence, "experiences[0].start")!.SourceText.Should().Be("junio 2015");
    }

    // The numeric month/year shape, which is the other half of the dominant format. "06/2015" has a
    // four-digit second field, so it is unambiguous even though the three-field numeric shapes are read
    // day-first.
    [Fact]
    public void Parse_ANumericMonthAndYearRange_ArrivesAsTwoMonthPrecisionDates()
    {
        var (draft, _) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Engineer
            Acme
            06/2015 - 02/2019
            """);

        draft.Experiences![0]!.Start.Should().Be("2015-06");
        draft.Experiences[0]!.End.Should().Be("2019-02");
    }

    // Still blank and still flagged, and this is the rule that did NOT move: a two-digit year names no
    // century, so there is nothing to resolve it to. Recognised so the job still appears.
    [Fact]
    public void Parse_ATwoDigitYearDate_IsStillBlankAndFlaggedWithItsSource()
    {
        var (draft, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Engineer
            Acme
            03/95 - 06/98
            """);

        draft.Experiences![0]!.Start.Should().BeNull();
        var start = Provenance(confidence, "experiences[0].start")!;
        start.Confidence.Should().Be(FieldConfidence.NotExtracted);
        start.SourceText.Should().Be("03/95");
    }

    // THE FIXTURE IS THE CASE THIS GETS WRONG, ON PURPOSE. A volunteer role listed under an EXPERIENCE
    // heading is labelled Professional, because the inference is about WHERE the entry was found and not
    // about what it says — and the words "Volunteer Coordinator" are exactly the sort of thing no parser
    // should read a type out of, since "Volunteer Coordinator" at the Red Cross is frequently a paid job.
    //
    // This replaces Parse_ExperienceType_IsNeverGuessed, which pinned the opposite decision. That
    // decision was not neutral: ResumeDraftValidator REQUIRES the type, so leaving it null turned every
    // imported entry into a blocking error — nine of them on the real import that prompted this, each
    // needing a manual click on a two-value enum before anything could be created.
    //
    // The trade: the candidate sees a value marked CHECK and corrects one field, instead of being forced
    // to fill nine. Medium is what makes that honest — it is the same confidence every other positional
    // read here carries, and it is what tells the review screen to flag rather than to hide.
    [Fact]
    public void Parse_ExperienceType_IsInferredFromTheSection_EvenWhenTheWordsSayOtherwise()
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

        draft.Experiences![0]!.Type.Should().Be(
            "Professional", "the entry was found under a heading classified as Experience");

        var type = Provenance(confidence, "experiences[0].type")!;
        type.Confidence.Should().Be(
            FieldConfidence.Medium, "a positional inference must be flagged for review, not hidden");
        type.SourceText.Should().Be("Professional");
    }

    // A bare host is completed, not rejected. This invents no fact about the candidate — it writes out in
    // full what they already wrote — which is what separates it from the phone hint below.
    [Fact]
    public void Parse_ABareDomain_IsSuggestedWithAScheme()
    {
        var (_, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com
            cristianarellano.com
            """);

        var website = Provenance(confidence, "contact.website")!;
        website.SourceText.Should().Be("cristianarellano.com");
        website.Suggestion.Should().Be("https://cristianarellano.com");
    }

    // Nothing to correct, and re-prefixing would corrupt it.
    [Fact]
    public void Parse_AUrlThatAlreadyHasAScheme_IsNotSuggested()
    {
        var (_, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com
            https://cristianarellano.com
            """);

        Provenance(confidence, "contact.website")!.Suggestion.Should().BeNull();
    }

    // The country comes from the candidate's own location line. `310 4580645` is a real Colombian mobile
    // and the ordinary way to write one; today it is REJECTED, which hands a correct reading back as an
    // error.
    [Fact]
    public void Parse_ANationalPhone_IsSuggestedInInternationalForm_WhenTheLocationNamesACountry()
    {
        var (_, confidence) = ResumeTextParser.Parse(
            """
            Cristian Arellano
            cristian@example.com
            Bogotá, Colombia
            310 4580645
            """);

        var phone = Provenance(confidence, "contact.phoneNumber")!;
        phone.SourceText.Should().Be("310 4580645");
        phone.Suggestion.Should().Be("+573104580645");
    }

    // THE LOAD-BEARING NEGATIVE. Without a country named in the document there is no evidence, and a
    // plausible prefix accepted without reading is exactly the wrong data this product may not hold —
    // so nothing is proposed and the candidate supplies the one thing nobody can know for them.
    [Fact]
    public void Parse_ANationalPhone_IsNotSuggested_WhenNoCountryIsKnown()
    {
        var (_, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com
            310 4580645
            """);

        var phone = Provenance(confidence, "contact.phoneNumber");
        phone!.Suggestion.Should().BeNull("no country was named, so any prefix would be invented");
    }

    // THE FIXTURES HERE ARE UNINDENTED ON PURPOSE. The first version of this fix keyed on indentation,
    // passed its tests, deployed, and changed nothing in production — because the API reads what PdfPig's
    // ContentOrderTextExtractor produces, and that rebuilds text from glyph positions with no leading
    // whitespace at all. The indentation was real in the PDF, real in `pdftotext -layout`, and absent
    // from the string this parser receives. A fixture that keeps it would certify a signal the running
    // system does not have.
    //
    // A BULLET THAT WRAPPED IS ONE BULLET. Measured on a real import: five of six experiences arrived
    // with a fragment of the PREVIOUS job's achievements as their title and employer, because the
    // unmarked second line of a wrapped bullet was never consumed and became context for the next dated
    // entry. Those read like data rather than like blanks, which is the more dangerous of the two
    // failures — a blank is obviously wrong, "Title: dashboards." looks filled in.
    [Fact]
    public void Parse_AWrappedBullet_StaysWithItsOwnEntry()
    {
        var (draft, _) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            Experience
            L2/L3 Support Analyst, SIESA – Cali, Colombia   June 2024 – Dec 2024
            • Resolved L2/L3 escalations of complex incidents in SIESA ERP, against
            aggressive SLAs.
            Python Developer, American Telnet – Cali, Colombia   July 2023 – Dec 2023
            • Led architecture of a Python automation suite.
            """);

        draft.Experiences.Should().HaveCount(2);

        // The wrapped tail joined its own bullet instead of becoming the next job's title.
        draft.Experiences![0]!.Highlights.Should().ContainSingle()
            .Which.Should().Be(
                "Resolved L2/L3 escalations of complex incidents in SIESA ERP, against aggressive SLAs.");

        // The next entry's title comes from its OWN line. It still carries the employer inside it, for the
        // "Role, Company – City" separator reason documented below — but it is that entry's own text
        // rather than the previous job's achievements, which is the corruption this fixes.
        draft.Experiences[1]!.Position.Should().StartWith("Python Developer");
        draft.Experiences[1]!.Position.Should().NotContain("aggressive");
        draft.Experiences[1]!.Position.Should().NotContain("SLAs");
    }

    // A hyphen at a line break is the PDF splitting a word, not the candidate's punctuation. Joining with
    // a space would invent two words where the document had one.
    [Fact]
    public void Parse_AWordSplitAcrossLines_IsRejoinedWithoutTheHyphen()
    {
        var (draft, _) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            Experience
            Technologist, SENA   Apr 2020 – Apr 2022
            • Studied software analysis and develop-
            ment across two years.
            """);

        draft.Experiences![0]!.Highlights.Should().ContainSingle()
            .Which.Should().Be("Studied software analysis and development across two years.");
    }

    // THE ONE-LINE HEADER, which is what a modern CV uses. Tested whole, this line is disqualified by the
    // first guard it meets -- it has an @, and digits, and a URL-shaped run -- so the location was simply
    // not extracted. That cost the phone its country hint too, which is why both fields came back empty
    // on a real import.
    [Fact]
    public void Parse_ALocationSharingItsLineWithContactDetails_IsStillRead()
    {
        var (draft, confidence) = ResumeTextParser.Parse(
            """
            Cristian Arellano Muñoz
            Cali, Valle del Cauca, Colombia | hi@cristianarellano.com | 310 4580645
            """);

        draft.Contact!.Location.Should().Be("Cali, Valle del Cauca, Colombia");

        // And with a country finally in reach, the phone gets its proposal.
        Provenance(confidence, "contact.phoneNumber")!.Suggestion.Should().Be("+573104580645");
    }

    // THE LAYOUT OUR USER'S OWN CV USES, and the one that produced the empty rows on the review screen
    // that started this work. Role, employer and period on ONE line with the period at the right margin:
    // looking backwards for context finds nothing, because the context was never on a line of its own.
    //
    // Verbatim from that document rather than invented, because a fixture written to match the fix is a
    // fixture that cannot fail for the right reason.
    [Fact]
    public void Parse_RoleEmployerAndPeriodOnOneLine_ReadsTheRoleFromThatLine()
    {
        var (draft, _) = ResumeTextParser.Parse(
            """
            Cristian Arellano Muñoz
            hi@cristianarellano.com

            Experience
            Backend Engineer — Shoppipai, Independent Development – Cali, Colombia   Dec 2025 – present
            • Designed the architecture of an enterprise e-commerce platform.
            IT Support & Systems, CDA La Luna – Cali, Colombia   Jan 2025 – Nov 2025
            • Administered network and server infrastructure.
            """);

        draft.Experiences.Should().HaveCount(2);

        // THE PROPERTY THAT MATTERS: neither entry is blank. Before this, both arrived with an empty role
        // and an empty employer — two required fields the candidate could not fill, because the text was
        // sitting on a line the parser never looked at.
        foreach (var experience in draft.Experiences!)
        {
            experience!.Position.Should().NotBeNullOrWhiteSpace();
            experience.Organization.Should().NotBeNullOrWhiteSpace();
        }

        draft.Experiences![0]!.Position.Should().Be("Backend Engineer");

        // AND A LIMIT WORTH STATING RATHER THAN HIDING. The second line is "Role, Company – City", where
        // the comma separates role from employer and the dash separates employer from location — so
        // SplitContext, which tries the dash first, keeps the company inside the role and puts the city in
        // the employer field. Asserted as it behaves, not as it should: the entry is now reviewable
        // instead of blocking, which is the win, and re-ordering those separators would break the
        // "Senior Engineer — Company" shape that is more common. Fixing it properly means recognising the
        // trailing location, which the document already states in its contact block.
        draft.Experiences[1]!.Position.Should().Be("IT Support & Systems, CDA La Luna");
        draft.Experiences[1]!.Organization.Should().Be("Cali, Colombia");

        // The date is gone from the role either way. A half-stripped period sitting in a job title is the
        // failure that sharing one grammar with CvDateParser exists to avoid.
        draft.Experiences[0]!.Position.Should().NotContain("2025");
        draft.Experiences[0]!.Organization.Should().NotContain("Dec 2025");
        draft.Experiences[1]!.Organization.Should().NotContain("Jan 2025");
    }

    // A date line with nothing naming it is not an entry. It used to become a row with an empty employer
    // and an empty role — two "Value is required" fields on something the candidate never wrote, and
    // could not fix because there was nothing to put in them.
    [Fact]
    public void Parse_ADateWithNothingNamingIt_IsNotAnEntry()
    {
        var (draft, _) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            01/2020 - 12/2020

            Senior Developer
            Globant
            15/03/2021 - 20/06/2023
            """);

        draft.Experiences.Should().ContainSingle("the orphan date line names no role and no employer");
        draft.Experiences![0]!.Position.Should().Be("Senior Developer");
        draft.Experiences[0]!.Organization.Should().Be("Globant");
    }

    // THE DATE-FIRST LAYOUT, which looking backwards alone cannot read. Searching only behind finds
    // nothing here and then steals the title as context for the NEXT date line, so one such block used to
    // corrupt two entries at once.
    [Fact]
    public void Parse_ADateAboveTheRole_ReadsTheRoleBelowIt()
    {
        var (draft, _) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            15/03/2019 - 20/06/2021
            Senior Developer, Globant
            • Shipped the thing
            """);

        draft.Experiences.Should().ContainSingle();
        draft.Experiences![0]!.Position.Should().Be("Senior Developer");
        draft.Experiences[0]!.Organization.Should().Be("Globant");
        draft.Experiences[0]!.Highlights.Should().ContainSingle().Which.Should().Be("Shipped the thing");
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

    // Achievements is computed from Experience.Highlights and nothing else, and it carries 0.25 of the
    // readability total. The parser used to emit no highlights from any document in any format, so every
    // imported CV scored zero on a quarter of that score and was then advised to add bullet points it
    // had already written.
    [Fact]
    public void Parse_BulletsUnderARole_BecomeThatRolesHighlights()
    {
        var (draft, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Senior Engineer
            Stripe
            03/2019 - 06/2024
            - Cut settlement time by 40% across 12 markets.
            - Led a team of six.
            """);

        draft.Experiences![0]!.Highlights.Should().BeEquivalentTo(
            ["Cut settlement time by 40% across 12 markets.", "Led a team of six."],
            "the bullets below the date line describe the role above it");

        var provenance = Provenance(confidence, "experiences[0].highlights");
        provenance.Should().NotBeNull("a populated highlights list must carry provenance");
        provenance!.Confidence.Should().Be(FieldConfidence.Medium, "the text is verbatim but the attribution is positional");
    }

    // The repair that matters more than the extraction. Bullets under one role used to fall into the NEXT
    // entry's context window, get stripped of their marker, and be read as that entry's position and
    // organisation. Silent, and worse than a missing field.
    //
    // THE SECOND ROLE DELIBERATELY HAS ONE CONTEXT LINE, and the first version of this test had two —
    // which passed with the fix reverted, because the window already keeps only the last two lines and
    // "Junior Engineer" / "Google" filled it on their own. A test that cannot observe its guarantee is
    // the failure mode this repository keeps catching, so the shape here is the shape that corrupts:
    // one bullet plus one title, both inside the window.
    [Fact]
    public void Parse_ABulletAboveASingleLineRole_IsNotReadAsThatRolesTitle()
    {
        var (draft, _) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Senior Engineer
            Stripe
            03/2019 - 06/2024
            - Cut settlement time by 40% across 12 markets.
            Junior Engineer at Google
            06/2015 - 02/2019
            """);

        draft.Experiences.Should().HaveCount(2);
        draft.Experiences![0]!.Highlights.Should().ContainSingle();
        draft.Experiences![1]!.Position.Should().Be(
            "Junior Engineer", "the bullet belongs to the role above it, not to this one");
        draft.Experiences![1]!.Organization.Should().Be("Google");
        draft.Experiences![1]!.Highlights.Should().BeNull("this role listed none, and absent is honest");
    }

    // A role with no bullets is flagged, never silently absent — the rule every other field here follows.
    [Fact]
    public void Parse_ARoleWithNoBullets_FlagsHighlightsRatherThanOmittingThem()
    {
        var (draft, confidence) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Senior Engineer
            Stripe
            03/2019 - 06/2024
            """);

        draft.Experiences![0]!.Highlights.Should().BeNull();
        Provenance(confidence, "experiences[0].highlights")!.Confidence.Should().Be(FieldConfidence.NotExtracted);
    }

    // An indented continuation line is not a bullet. BulletLine exists precisely because LeadingBullet's
    // character class includes \s, so `^[\s...]+` matches on indentation alone and would have consumed a
    // plain indented job title as somebody else's achievement.
    [Fact]
    public void Parse_AnIndentedLineWithNoMarker_IsNotAHighlight()
    {
        var (draft, _) = ResumeTextParser.Parse(
            """
            Sam Doe
            sam@example.com

            EXPERIENCE
            Senior Engineer
            Stripe
            03/2019 - 06/2024
              Junior Engineer
              Google
            06/2015 - 02/2019
            """);

        draft.Experiences.Should().HaveCount(2);
        draft.Experiences![0]!.Highlights.Should().BeNull("indentation alone does not make a bullet");
        draft.Experiences![1]!.Position.Should().Be("Junior Engineer");
    }

    [Theory]
    // The case the frontend session reported: a comma is the most common separator on a real CV.
    [InlineData("Senior Engineer, Remington Rand", "Senior Engineer", "Remington Rand")]
    // ...and the case that makes it the most dangerous one. Splitting here would produce Organization
    // "Inc.", which is strictly worse than leaving the name whole.
    [InlineData("Remington Rand, Inc.", null, "Remington Rand, Inc.")]
    [InlineData("Mercado Libre, S.A.", null, "Mercado Libre, S.A.")]
    // Two commas is too ambiguous to guess at, so it falls through and stays flagged.
    [InlineData("Engineer, Payments, Stripe", null, "Engineer, Payments, Stripe")]
    public void Parse_ASingleContextLine_SplitsOnACommaOnlyWhenItIsNotALegalForm(
        string contextLine, string? expectedPosition, string expectedOrganization)
    {
        var (draft, _) = ResumeTextParser.Parse(
            $"""
            Sam Doe
            sam@example.com

            EXPERIENCE
            {contextLine}
            03/2019 - 06/2024
            """);

        draft.Experiences![0]!.Position.Should().Be(expectedPosition);
        draft.Experiences![0]!.Organization.Should().Be(expectedOrganization);
    }

    private static FieldProvenance? Provenance(DraftConfidence confidence, string path) =>
        confidence.Fields.FirstOrDefault(field => field.Path == path);
}
