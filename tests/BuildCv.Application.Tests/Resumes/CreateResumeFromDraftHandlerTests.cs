using BuildCv.Application.Common;
using BuildCv.Application.Resumes;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Application.Tests.Resumes;

// The import path, which exists so a candidate can create a whole CV in one request instead of the
// sixteen the API demanded. What is pinned here is mostly NOT "the fields arrive": it is the three
// properties that make the endpoint usable by a review screen — every failure is reported at once, each
// one names the field that caused it, and a rejected draft creates nothing.
public class CreateResumeFromDraftHandlerTests
{
    private readonly FakeResumeRepository _resumes = new();
    private readonly FakeImportEvidenceProtector _evidence = new();
    private readonly CreateResumeFromDraftHandler _handler;
    private readonly AccountId _owner = AccountId.New();

    public CreateResumeFromDraftHandlerTests() => _handler = new CreateResumeFromDraftHandler(_resumes, _evidence);

    private Task<ResumeImportResult> Import(ResumeDraft draft) =>
        _handler.Handle(new CreateResumeFromDraftCommand(_owner, draft));

    private static ContactDraft ValidContact() => new(FullName: "Jane Candidate", Email: "jane@example.com");

    private static ResumeDraft ValidDraft() => new(Contact: ValidContact());

    [Fact]
    public async Task Import_FullDraft_CreatesTheWholeResumeInOneWrite()
    {
        var result = await Import(new ResumeDraft(
            Contact: new ContactDraft(
                FullName: "Jane Candidate",
                Email: "Jane@Example.com",
                PhoneNumber: "+541155550123",
                Location: "Buenos Aires",
                Website: "https://jane.example.com",
                Summary: "Backend engineer.",
                Profiles: [new ProfileDraft("GitHub", "janedev", "https://github.com/janedev")]),
            Experiences:
            [
                new ExperienceDraft(
                    Type: "Professional",
                    Organization: "Mercado Libre",
                    Position: "Senior Engineer",
                    Start: "2019-03-01",
                    End: "2023-06-30",
                    Summary: "Payments platform.",
                    Highlights: ["Cut latency in half"])
            ],
            Educations:
            [
                new EducationDraft(
                    Institution: "Universidad de Buenos Aires",
                    Degree: "Ingeniero en Sistemas",
                    FieldOfStudy: "Software",
                    Start: "2012-03-01",
                    End: "2017-12-01",
                    Grade: "8.4",
                    Level: "Bachelor")
            ],
            Skills: [new SkillDraft("C#", "Advanced", "7")],
            Projects:
            [
                new ProjectDraft(
                    Name: "buildcv",
                    Start: "2024-01-01",
                    End: null,
                    Description: "A CV scorer.",
                    RepositoryUrl: "https://github.com/janedev/buildcv",
                    LiveDemoUrl: "https://buildcv.example.com",
                    Technologies: ["dotnet"],
                    Highlights: ["Deterministic scoring"])
            ],
            Certificates:
            [
                new CertificateDraft(
                    Name: "AWS Solutions Architect",
                    Issuer: "Amazon",
                    CredentialId: "cred-123",
                    CredentialUrl: "https://aws.example.com/cred-123",
                    ValidityStart: "2024-01-01",
                    ValidityEnd: "2027-01-01")
            ],
            Languages: [new LanguageDraft("Español", "Bilingüe", "Native")],
            Awards: [new AwardDraft("Best Hack", "Hackathon AR", "2021-11-05", "First place.")],
            Publications:
            [
                new PublicationDraft("On Scoring", "ACM", "https://acm.example.com/on-scoring", "2022-05-01", "A paper.")
            ],
            Interests: [new InterestDraft("Climbing", ["bouldering"])],
            References:
            [
                new ReferenceDraft("John Manager", "Engineering Manager", "Mercado Libre",
                    "john@example.com", "+541155550999", "Would hire again.")
            ]));

        result.IsSuccess.Should().BeTrue();
        result.FieldErrors.Should().BeEmpty();

        // The point of the endpoint, and WriteCount rather than AddCount because the regression to catch
        // is not a second insert: a handler that created the resume and then updated it once per section
        // would leave identical contents behind AND an AddCount of exactly 1.
        _resumes.WriteCount.Should().Be(1);
        _resumes.AddCount.Should().Be(1);

        var stored = await _resumes.GetByIdAsync(result.Resume!.Id);
        stored.Should().NotBeNull();
        stored!.OwnerId.Should().Be(_owner);

        stored.ContactInformation.FullName.Value.Should().Be("Jane Candidate");
        stored.ContactInformation.Email.Value.Should().Be("jane@example.com", "Email.Create lowercases");
        stored.ContactInformation.PhoneNumber!.Value.Should().Be("+541155550123");
        stored.ContactInformation.Location.Should().Be("Buenos Aires");
        stored.ContactInformation.Summary.Should().Be("Backend engineer.");

        var experience = stored.Experiences.Should().ContainSingle().Subject;
        experience.Type.Should().Be(ExperienceType.Professional);
        experience.Organization.Value.Should().Be("Mercado Libre");
        experience.Position.Should().Be("Senior Engineer");
        experience.Period.StartsOn.Should().Be(new DateOnly(2019, 3, 1));
        experience.Period.EndsOn.Should().Be(new DateOnly(2023, 6, 30));
        experience.Summary.Should().Be("Payments platform.");
        experience.Highlights.Should().Equal("Cut latency in half");

        var education = stored.Educations.Should().ContainSingle().Subject;
        education.Institution.Value.Should().Be("Universidad de Buenos Aires");
        education.Degree.Should().Be("Ingeniero en Sistemas");
        education.FieldOfStudy.Should().Be("Software");
        education.Period.StartsOn.Should().Be(new DateOnly(2012, 3, 1));
        education.Period.EndsOn.Should().Be(new DateOnly(2017, 12, 1));
        education.Grade.Should().Be("8.4");
        education.Level.Should().Be(EducationLevel.Bachelor);

        var skill = stored.Skills.Should().ContainSingle().Subject;
        skill.Name.Name.Should().Be("C#");
        skill.Level.Should().Be(SkillLevel.Advanced);
        skill.YearsOfExperience.Should().Be(7);

        var project = stored.Projects.Should().ContainSingle().Subject;
        project.Name.Should().Be("buildcv");
        project.Period.StartsOn.Should().Be(new DateOnly(2024, 1, 1));
        project.Period.EndsOn.Should().BeNull("an omitted end date means the project is current");
        project.Description.Should().Be("A CV scorer.");
        project.RepositoryUrl!.Value.Should().Be("https://github.com/janedev/buildcv");
        project.LiveDemoUrl!.Value.Should().Be("https://buildcv.example.com");
        project.Technologies.Select(technology => technology.Name).Should().Equal("dotnet");
        project.Highlights.Should().Equal("Deterministic scoring");

        var certificate = stored.Certificates.Should().ContainSingle().Subject;
        certificate.Name.Should().Be("AWS Solutions Architect");
        certificate.Issuer.Value.Should().Be("Amazon");
        certificate.CredentialId.Should().Be("cred-123");
        certificate.CredentialUrl!.Value.Should().Be("https://aws.example.com/cred-123");
        certificate.ValidityPeriod!.StartsOn.Should().Be(new DateOnly(2024, 1, 1));
        certificate.ValidityPeriod.EndsOn.Should().Be(new DateOnly(2027, 1, 1));

        var language = stored.Languages.Should().ContainSingle().Subject;
        language.Name.Should().Be("Español");
        language.Fluency.Should().Be("Bilingüe", "free text is carried through verbatim");
        language.Level.Should().Be(LanguageProficiency.Native);

        var award = stored.Awards.Should().ContainSingle().Subject;
        award.Title.Should().Be("Best Hack");
        award.Awarder!.Value.Should().Be("Hackathon AR");
        award.Date.Should().Be(new DateOnly(2021, 11, 5));
        award.Summary.Should().Be("First place.");

        var publication = stored.Publications.Should().ContainSingle().Subject;
        publication.Title.Should().Be("On Scoring");
        publication.Publisher!.Value.Should().Be("ACM");
        publication.Url!.Value.Should().Be("https://acm.example.com/on-scoring");
        publication.ReleaseDate.Should().Be(new DateOnly(2022, 5, 1));
        publication.Summary.Should().Be("A paper.");

        var interest = stored.Interests.Should().ContainSingle().Subject;
        interest.Name.Should().Be("Climbing");
        interest.Keywords.Should().Equal("bouldering");

        var reference = stored.References.Should().ContainSingle().Subject;
        reference.Name.Should().Be("John Manager");
        reference.Position.Should().Be("Engineering Manager");
        reference.Company!.Value.Should().Be("Mercado Libre");
        reference.Email!.Value.Should().Be("john@example.com");
        reference.PhoneNumber!.Value.Should().Be("+541155550999");
        reference.ReferenceText.Should().Be("Would hire again.");
    }

    // Website and Profiles were IMPOSSIBLE to set through the API before this endpoint — not merely
    // un-exposed. ContactInformationFactory passes a literal null for Website and Profiles had no
    // writer at all, so this is new capability rather than a refactor and gets its own test.
    [Fact]
    public async Task Import_WithAWebsiteAndProfiles_StoresThemOnTheContactInformation()
    {
        var result = await Import(new ResumeDraft(Contact: new ContactDraft(
            FullName: "Jane Candidate",
            Email: "jane@example.com",
            Website: "https://jane.example.com",
            Profiles:
            [
                new ProfileDraft("GitHub", "janedev", "https://github.com/janedev"),
                new ProfileDraft("LinkedIn", "jane-candidate", null)
            ])));

        result.IsSuccess.Should().BeTrue();

        var contact = (await _resumes.GetByIdAsync(result.Resume!.Id))!.ContactInformation;
        contact.Website!.Value.Should().Be("https://jane.example.com");
        contact.Profiles.Should().HaveCount(2);
        contact.Profiles[0].Network.Should().Be("GitHub");
        contact.Profiles[0].Username.Should().Be("janedev");
        contact.Profiles[0].Url!.Value.Should().Be("https://github.com/janedev");
        contact.Profiles[1].Network.Should().Be("LinkedIn");
        contact.Profiles[1].Url.Should().BeNull("a profile without a URL is still a profile");
    }

    // THE test. The domain throws on the first bad field, so without collection a candidate fixing five
    // problems needs five round trips. Five distinct failures, five different sections, one response —
    // and it goes red the moment anyone reintroduces an early return anywhere in the walk.
    [Fact]
    public async Task Import_WithFiveBadFields_ReportsAllFiveWithTheirPaths()
    {
        var result = await Import(new ResumeDraft(
            Contact: new ContactDraft(
                FullName: "Jane Candidate",
                Email: "jane@example.com",
                PhoneNumber: "(555) 123-4567"),
            Experiences:
            [
                new ExperienceDraft(
                    Type: "Professional", Organization: "Globant", Position: "Engineer",
                    Start: "2020-01-01", End: "2019-01-01")
            ],
            Educations:
            [
                new EducationDraft(Institution: "UBA", Start: "marzo de 2015")
            ],
            Skills:
            [
                new SkillDraft("C#"),
                new SkillDraft(new string('a', 101))
            ],
            Languages: [new LanguageDraft("Español", Level: "Avanzado")]));

        result.IsSuccess.Should().BeFalse();
        result.Resume.Should().BeNull();
        result.FieldErrors.Select(error => error.Path).Should().BeEquivalentTo(
        [
            "contact.phoneNumber",
            "experiences[0].end",
            "educations[0].start",
            "skills[1].name",
            "languages[0].level"
        ]);
    }

    [Fact]
    public async Task Import_WithAnyBadField_CreatesNothing()
    {
        var result = await Import(ValidDraft() with
        {
            Skills = [new SkillDraft("C#"), new SkillDraft("Go", Level: "Wizard")]
        });

        result.IsSuccess.Should().BeFalse();

        // Not "the result says failure" — that is a different claim. Nothing reached the store, so the
        // valid C# skill beside the invalid Go one was not half-imported either.
        _resumes.AddCount.Should().Be(0);
    }

    // A real extracted CV lists "React" twice routinely, and Resume.AddSkill rejects case-insensitive
    // duplicates with an exception. The index in the path is the point: the FIRST occurrence is the one
    // that landed, so the later one is the line the candidate has to delete.
    [Fact]
    public async Task Import_WithADuplicateSkill_ReportsTheLaterOccurrence()
    {
        var result = await Import(ValidDraft() with
        {
            Skills = [new SkillDraft("React"), new SkillDraft("TypeScript"), new SkillDraft("react")]
        });

        result.IsSuccess.Should().BeFalse();
        var error = result.FieldErrors.Should().ContainSingle().Subject;
        error.Path.Should().Be("skills[2].name");

        // The path names the later occurrence and the message names the earlier one, so a review screen
        // can highlight both rows — and the candidate's own text appears in neither.
        error.Message.Should().Be("Duplicates the skill at index 0.");
        error.Message.Should().NotContainEquivalentOf("React");
        _resumes.AddCount.Should().Be(0);
    }

    [Theory]
    [InlineData("certificates")]
    [InlineData("languages")]
    [InlineData("interests")]
    public async Task Import_WithADuplicateEntry_ReportsTheLaterOccurrence(string section)
    {
        var draft = section switch
        {
            "certificates" => ValidDraft() with
            {
                Certificates =
                [
                    new CertificateDraft("CKA", "CNCF"),
                    new CertificateDraft("cka", "CNCF")
                ]
            },
            "languages" => ValidDraft() with
            {
                Languages = [new LanguageDraft("Español"), new LanguageDraft("español")]
            },
            _ => ValidDraft() with
            {
                Interests = [new InterestDraft("Climbing"), new InterestDraft("climbing")]
            }
        };

        var result = await Import(draft);

        result.IsSuccess.Should().BeFalse();
        result.FieldErrors.Should().ContainSingle()
            .Which.Path.Should().Be($"{section}[1].name");
    }

    // "-1" wraps to 255 through the unchecked tinyint conversion — ABOVE Native — and "99" stores as 99,
    // both silently, which is why Enum.IsDefined runs on the CLR value rather than TryParse alone. Same
    // guard and same reason as the four parse sites in ResumeEndpoints.cs; it lives in the validator so
    // it produces a field path instead of a bare 400.
    [Theory]
    [InlineData("-1")]
    [InlineData("99")]
    [InlineData("300")]
    [InlineData("Avanzado")]
    public async Task Import_WithAProficiencyTheEnumDoesNotKnow_IsAFieldErrorAndStoresNothing(string level)
    {
        var result = await Import(ValidDraft() with
        {
            Languages = [new LanguageDraft("Español", "Bilingüe", level)]
        });

        result.IsSuccess.Should().BeFalse();
        var error = result.FieldErrors.Should().ContainSingle().Subject;
        error.Path.Should().Be("languages[0].level");
        error.Message.Should().Be("Invalid language proficiency.");
        _resumes.AddCount.Should().Be(0);
    }

    // The guard must not narrow the input to the enum's names: GET returns levels as NUMBERS, so a
    // client that reads a resume, edits it and posts it back legitimately sends "4".
    [Theory]
    [InlineData("Native", LanguageProficiency.Native)]
    [InlineData("native", LanguageProficiency.Native)]
    [InlineData("4", LanguageProficiency.Native)]
    [InlineData("0", LanguageProficiency.Basic)]
    public async Task Import_WithAKnownProficiency_StoresIt(string level, LanguageProficiency expected)
    {
        var result = await Import(ValidDraft() with
        {
            Languages = [new LanguageDraft("Español", null, level)]
        });

        result.IsSuccess.Should().BeTrue();
        result.Resume!.Languages.Should().ContainSingle().Which.Level.Should().Be(expected);
    }

    // EVERY ITEM IS INDIVIDUALLY INVALID, and that is the whole point of the seed. Seeded with valid
    // items instead, deleting the `return` after the cap error let the walk build 201 Technologies, 201
    // Skills and run 201 O(n) duplicate scans while adding ZERO further errors — so ContainSingle stayed
    // green and the test could not see the work the cap exists to decline. With invalid items, walking
    // is 201 extra errors and the assertion has teeth.
    [Fact]
    public async Task Import_WithAnOverCapCollection_IsOneFieldErrorAndTheSectionIsNotWalked()
    {
        var result = await Import(ValidDraft() with
        {
            Skills = [.. Enumerable.Repeat(new SkillDraft(Name: null), ResumeDraftLimits.Skills + 1)]
        });

        result.IsSuccess.Should().BeFalse();

        var error = result.FieldErrors.Should().ContainSingle(
            "an over-cap section is refused WITHOUT being walked, so not one of its 201 invalid items is "
            + "inspected").Subject;
        error.Path.Should().Be("skills");
        error.Message.Should().Be($"Too many items. At most {ResumeDraftLimits.Skills} are accepted.");
        _resumes.AddCount.Should().Be(0);
    }

    [Fact]
    public async Task Import_WithAnOverCapCollection_StillReportsTheOtherSections()
    {
        var result = await Import(ValidDraft() with
        {
            Skills = [.. Enumerable.Repeat(new SkillDraft(Name: null), ResumeDraftLimits.Skills + 1)],
            Languages = [new LanguageDraft("Español", Level: "Avanzado")]
        });

        result.FieldErrors.Select(error => error.Path).Should().BeEquivalentTo(["skills", "languages[0].level"]);
    }

    // THE CAP VALUES THEMSELVES, as literals. Every other cap assertion interpolates the same symbol the
    // code does, so it holds for any value of that symbol — including `Experiences = 1`, which would
    // reject a real CV with two jobs and turn no test red.
    [Fact]
    public void Limits_AreTheNumbersThisEndpointWasSizedFor()
    {
        ResumeDraftLimits.Experiences.Should().Be(50);
        ResumeDraftLimits.Educations.Should().Be(20);
        ResumeDraftLimits.Skills.Should().Be(200);
        ResumeDraftLimits.Projects.Should().Be(50);
        ResumeDraftLimits.Certificates.Should().Be(50);
        ResumeDraftLimits.Languages.Should().Be(20);
        ResumeDraftLimits.Awards.Should().Be(50);
        ResumeDraftLimits.Publications.Should().Be(200);
        ResumeDraftLimits.Interests.Should().Be(25);
        ResumeDraftLimits.References.Should().Be(20);
        ResumeDraftLimits.Profiles.Should().Be(20);
        ResumeDraftLimits.TextItems.Should().Be(50);
    }

    // THE WIRING, section by section, with the limit as a literal rather than as the constant the code
    // reads. Every one of the twelve caps was correct and none of them was tested: AddPublications
    // reaching for Limits.Educations instead of Limits.Publications turned nothing red, because the
    // message interpolates whichever limit it was handed.
    //
    // Both directions matter. Over-cap proves the section is capped at that number and refused whole;
    // at-cap proves the number is not LOWER than it claims, which is the half that catches a cap
    // accidentally shrunk.
    [Theory]
    [InlineData("experiences", 50)]
    [InlineData("educations", 20)]
    [InlineData("skills", 200)]
    [InlineData("projects", 50)]
    [InlineData("certificates", 50)]
    [InlineData("languages", 20)]
    [InlineData("awards", 50)]
    [InlineData("publications", 200)]
    [InlineData("interests", 25)]
    [InlineData("references", 20)]
    [InlineData("contact.profiles", 20)]
    [InlineData("experiences[0].highlights", 50)]
    [InlineData("projects[0].technologies", 50)]
    [InlineData("projects[0].highlights", 50)]
    [InlineData("interests[0].keywords", 50)]
    public async Task Import_OverOneSectionsOwnCap_IsRefusedAtThatPathWithThatLimit(string path, int limit)
    {
        var over = await Import(SectionOf(path, limit + 1, valid: false));

        over.FieldErrors.Should().ContainSingle()
            .Which.Should().Be(new FieldError(path, $"Too many items. At most {limit} are accepted."));

        var atCap = await Import(SectionOf(path, limit, valid: true));

        atCap.FieldErrors.Should().NotContain(error => error.Path == path);
        atCap.IsSuccess.Should().BeTrue();
    }

    // One section filled with `count` items. Invalid items are used for the over-cap direction so that
    // walking the section would be visible as `count` extra errors; valid ones are distinct because four
    // of these collections reject case-insensitive duplicates.
    private static ResumeDraft SectionOf(string path, int count, bool valid)
    {
        IReadOnlyList<T?> Items<T>(Func<int, T> item) => [.. Enumerable.Range(0, count).Select(item)];
        IReadOnlyList<string?> Text() =>
            valid ? [.. Enumerable.Range(0, count).Select(index => $"item-{index}")] : [.. Enumerable.Repeat((string?)null, count)];

        var draft = ValidDraft();
        return path switch
        {
            "experiences" => draft with
            {
                Experiences = Items(index => valid
                    ? new ExperienceDraft("Professional", $"org-{index}", "Engineer", "2020-01-01")
                    : new ExperienceDraft())
            },
            "educations" => draft with
            {
                Educations = Items(index => valid
                    ? new EducationDraft($"school-{index}", Start: "2020-01-01")
                    : new EducationDraft())
            },
            "skills" => draft with
            {
                Skills = Items(index => valid ? new SkillDraft($"skill-{index}") : new SkillDraft())
            },
            "projects" => draft with
            {
                Projects = Items(index => valid
                    ? new ProjectDraft($"project-{index}", "2020-01-01")
                    : new ProjectDraft())
            },
            "certificates" => draft with
            {
                Certificates = Items(index => valid
                    ? new CertificateDraft($"cert-{index}", "CNCF")
                    : new CertificateDraft())
            },
            "languages" => draft with
            {
                Languages = Items(index => valid ? new LanguageDraft($"lang-{index}") : new LanguageDraft())
            },
            "awards" => draft with
            {
                Awards = Items(index => valid ? new AwardDraft($"award-{index}") : new AwardDraft())
            },
            "publications" => draft with
            {
                Publications = Items(index => valid ? new PublicationDraft($"paper-{index}") : new PublicationDraft())
            },
            "interests" => draft with
            {
                Interests = Items(index => valid ? new InterestDraft($"interest-{index}") : new InterestDraft())
            },
            "references" => draft with
            {
                References = Items(index => valid ? new ReferenceDraft($"referee-{index}") : new ReferenceDraft())
            },
            "contact.profiles" => draft with
            {
                Contact = ValidContact() with
                {
                    Profiles = Items(index => valid ? new ProfileDraft($"network-{index}") : new ProfileDraft())
                }
            },
            "experiences[0].highlights" => draft with
            {
                Experiences =
                [
                    new ExperienceDraft("Professional", "Globant", "Engineer", "2020-01-01", Highlights: Text())
                ]
            },
            "projects[0].technologies" => draft with
            {
                Projects = [new ProjectDraft("buildcv", "2020-01-01", Technologies: Text())]
            },
            "projects[0].highlights" => draft with
            {
                Projects = [new ProjectDraft("buildcv", "2020-01-01", Highlights: Text())]
            },
            "interests[0].keywords" => draft with
            {
                Interests = [new InterestDraft("Climbing", Text())]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, "No seed for this section.")
        };
    }

    // A volunteer entry must import. AddWorkExperience throws unless the type is Professional, so a
    // validator that reached for it instead of AddExperience would reject half of a real CV while every
    // professional-only test stayed green.
    [Fact]
    public async Task Import_WithAVolunteerExperience_IsAccepted()
    {
        var result = await Import(ValidDraft() with
        {
            Experiences =
            [
                new ExperienceDraft(
                    Type: "Volunteer", Organization: "Techo", Position: "Mentor", Start: "2021-01-01")
            ]
        });

        result.IsSuccess.Should().BeTrue();
        result.Resume!.Experiences.Should().ContainSingle()
            .Which.Type.Should().Be(ExperienceType.Volunteer);
    }

    [Fact]
    public async Task Import_WithAnEmptyDraft_ReportsBothRequiredContactFields()
    {
        var result = await Import(new ResumeDraft());

        result.FieldErrors.Should().BeEquivalentTo(
        [
            new FieldError("contact.fullName", "Value is required."),
            new FieldError("contact.email", "Value is required.")
        ]);
    }

    // A contact that cannot be built must not stop the collections being validated. The validator builds
    // the aggregate on a placeholder contact in that case, and this pins the two things that must both
    // hold: the collection errors ARE reported, and the placeholder never becomes a resume.
    [Fact]
    public async Task Import_WithABadContactAndABadCollection_ReportsBothAndCreatesNothing()
    {
        var result = await Import(new ResumeDraft(
            Contact: new ContactDraft(FullName: "Jane Candidate", Email: "not-an-email"),
            Languages: [new LanguageDraft("Español", Level: "Avanzado")]));

        result.FieldErrors.Select(error => error.Path).Should().BeEquivalentTo(
            ["contact.email", "languages[0].level"]);
        result.Resume.Should().BeNull();
        _resumes.AddCount.Should().Be(0);
    }

    [Fact]
    public async Task Import_WithAnEndDateThatDidNotParse_DoesNotSilentlyDropIt()
    {
        var result = await Import(ValidDraft() with
        {
            Experiences =
            [
                new ExperienceDraft(
                    Type: "Professional", Organization: "Globant", Position: "Engineer",
                    Start: "2019-03-01", End: "Present")
            ]
        });

        result.IsSuccess.Should().BeFalse();
        result.FieldErrors.Should().ContainSingle()
            .Which.Should().Be(new FieldError(
                "experiences[0].end", "Invalid date. Expected yyyy-MM-dd, yyyy-MM or yyyy."));
    }

    // THE AGREEMENT TEST, and the reason the validator constructs instead of re-checking.
    //
    // Each case sends a value the real factory refuses, then asserts the reported message is THE ONE
    // THAT FACTORY THREW — captured by calling it, never hardcoded. That is what makes divergence
    // detectable: the day someone replaces a construction with a hand-written rule, the message stops
    // matching even if the verdict happens to agree, and the day they replace it with a rule that
    // disagrees, the field stops being reported at all.
    //
    // CaptureMessage throws on a value the factory ACCEPTS, so a case that stopped proving anything
    // fails loudly rather than passing vacuously.
    [Fact]
    public async Task Import_ForEveryFactoryBackedField_ReportsTheFactorysOwnMessage()
    {
        var tooLong = new string('a', 400);
        var cases = new (string Path, ResumeDraft Draft, Func<object> Factory)[]
        {
            ("contact.fullName",
                new ResumeDraft(Contact: ValidContact() with { FullName = tooLong }),
                () => PersonName.Create(tooLong)),

            ("contact.email",
                new ResumeDraft(Contact: ValidContact() with { Email = "jane(at)example.com" }),
                () => Email.Create("jane(at)example.com")),

            ("contact.phoneNumber",
                new ResumeDraft(Contact: ValidContact() with { PhoneNumber = "(555) 123-4567" }),
                () => PhoneNumber.Create("(555) 123-4567")),

            ("contact.website",
                new ResumeDraft(Contact: ValidContact() with { Website = "ftp://jane.example.com" }),
                () => Url.Create("ftp://jane.example.com")),

            ("contact.profiles[0].url",
                new ResumeDraft(Contact: ValidContact() with
                {
                    Profiles = [new ProfileDraft("GitHub", "janedev", "javascript:alert(1)")]
                }),
                () => Url.Create("javascript:alert(1)")),

            ("experiences[0].organization",
                ValidDraft() with
                {
                    Experiences =
                    [
                        new ExperienceDraft("Professional", tooLong, "Engineer", "2020-01-01")
                    ]
                },
                () => OrganizationName.Create(tooLong)),

            ("experiences[0].end",
                ValidDraft() with
                {
                    Experiences =
                    [
                        new ExperienceDraft("Professional", "Globant", "Engineer", "2020-01-01", "2019-01-01")
                    ]
                },
                () => DateRange.Create(new DateOnly(2020, 1, 1), new DateOnly(2019, 1, 1))),

            ("skills[0].name",
                ValidDraft() with { Skills = [new SkillDraft(tooLong)] },
                () => Technology.Create(tooLong)),

            ("skills[0].yearsOfExperience",
                ValidDraft() with { Skills = [new SkillDraft("C#", null, "99")] },
                () => Skill.Create(Technology.Create("C#"), null, 99)),

            ("projects[0].technologies[0]",
                ValidDraft() with
                {
                    Projects = [new ProjectDraft("buildcv", "2024-01-01", Technologies: [tooLong])]
                },
                () => Technology.Create(tooLong)),

            ("references[0].email",
                ValidDraft() with
                {
                    References = [new ReferenceDraft("John Manager", Email: "john(at)example.com")]
                },
                () => Email.Create("john(at)example.com")),
        };

        foreach (var (path, draft, factory) in cases)
        {
            var expected = WithoutDeveloperNoise(CaptureMessage(factory));
            var result = await Import(draft);

            result.IsSuccess.Should().BeFalse($"the factory behind {path} refuses this value");
            result.FieldErrors.Should().ContainSingle(error => error.Path == path)
                .Which.Message.Should().Be(expected,
                    $"{path} must report what its own factory threw, not a restatement of the rule");
        }
    }

    // Deliberately a SECOND, naive implementation of what ResumeDraftValidator.ForACandidate does.
    // Restating it here is what gives the assertion above teeth: the expected value still comes from
    // calling the real factory, and only the developer-facing tail is removed, so a hand-written rule
    // still fails the comparison while the trim itself stays pinned from the outside.
    private static string WithoutDeveloperNoise(string message) =>
        message.Split('\n')[0].Split(" (Parameter '")[0];

    // The other direction of the same agreement, and the one that costs a 500 rather than a wrong
    // message: a validator that let a value through to a constructor that then threw would answer 500 on
    // a request it had just called valid. Nothing here may escape as an exception.
    //
    // The same value goes into every field of every section at once, so this cannot claim anything about
    // which message any single field produced — the name now says only what the body checks. What it DOES
    // prove beyond "no throw" is that the walk is never abandoned by the first section to fail: all
    // eleven sections report, which is why the distinct-sections assertion is here rather than a bare
    // NotBeEmpty that one failing field would satisfy.
    [Fact]
    public async Task Import_WithHostileValuesInEveryField_NeverThrowsAndRejectsEverySection()
    {
        var hostile = new[]
        {
            "+abc", "+", "++541155550123", "0", "-1", "e", "1e3", "NaN", "\0", " ", "  ", "\r\n",
            "2019 - Present", "Avanzado", "javascript:alert(1)", "//example.com", new string('a', 5000)
        };

        foreach (var value in hostile)
        {
            var act = async () => await Import(new ResumeDraft(
                Contact: new ContactDraft(value, value, value, value, value, value, [new ProfileDraft(value, value, value)]),
                Experiences: [new ExperienceDraft(value, value, value, value, value, value, [value])],
                Educations: [new EducationDraft(value, value, value, value, value, value, value)],
                Skills: [new SkillDraft(value, value, value)],
                Projects: [new ProjectDraft(value, value, value, value, value, value, [value], [value])],
                Certificates: [new CertificateDraft(value, value, value, value, value, value)],
                Languages: [new LanguageDraft(value, value, value)],
                Awards: [new AwardDraft(value, value, value, value)],
                Publications: [new PublicationDraft(value, value, value, value, value)],
                Interests: [new InterestDraft(value, [value])],
                References: [new ReferenceDraft(value, value, value, value, value, value)]));

            var result = await act.Should().NotThrowAsync(
                $"'{value}' must come back as a field error, never as an unhandled exception");

            result.Subject.IsSuccess.Should().BeFalse();
            _resumes.AddCount.Should().Be(0);

            // NOT a count of sections: some of these values are legitimate in some sections ("+abc" is a
            // perfectly good interest name), and asserting "all eleven report" was simply false. What
            // must hold for every value is that the walk RAN TO THE END — contact is the first thing
            // validated and references the last, so both reporting is the property, and it is stronger
            // than any count because no early return can satisfy it.
            result.Subject.FieldErrors
                .Select(error => error.Path.Split('[', '.')[0])
                .Should().Contain(["contact", "references"],
                    $"'{value}' must be reported in the last section as well as the first");
        }
    }

    // `[null]` is valid JSON, and System.Text.Json does not enforce nullable reference annotations, so a
    // null element arrives as a real element whatever the declared type says. It used to be an unhandled
    // NullReferenceException — a 500 — on all eleven sections plus contact.profiles, while the plain
    // string arrays beside them already handled it.
    [Theory]
    [InlineData("experiences")]
    [InlineData("educations")]
    [InlineData("skills")]
    [InlineData("projects")]
    [InlineData("certificates")]
    [InlineData("languages")]
    [InlineData("awards")]
    [InlineData("publications")]
    [InlineData("interests")]
    [InlineData("references")]
    [InlineData("contact.profiles")]
    public async Task Import_WithANullElement_IsAFieldErrorAtThatIndex(string section)
    {
        var draft = ValidDraft();
        draft = section switch
        {
            "experiences" => draft with { Experiences = [null] },
            "educations" => draft with { Educations = [null] },
            "skills" => draft with { Skills = [null] },
            "projects" => draft with { Projects = [null] },
            "certificates" => draft with { Certificates = [null] },
            "languages" => draft with { Languages = [null] },
            "awards" => draft with { Awards = [null] },
            "publications" => draft with { Publications = [null] },
            "interests" => draft with { Interests = [null] },
            "references" => draft with { References = [null] },
            _ => draft with { Contact = ValidContact() with { Profiles = [null] } }
        };

        var act = async () => await Import(draft);
        var result = await act.Should().NotThrowAsync();

        result.Subject.FieldErrors.Should().ContainSingle()
            .Which.Should().Be(new FieldError($"{section}[0]", "Value is required."));
        _resumes.AddCount.Should().Be(0);
    }

    // A null between two real items must not swallow them: the walk continues past it.
    [Fact]
    public async Task Import_WithANullElementBetweenValidOnes_ReportsOnlyThatIndex()
    {
        var result = await Import(ValidDraft() with
        {
            Skills = [new SkillDraft("C#"), null, new SkillDraft("Go", Level: "Wizard")]
        });

        result.FieldErrors.Should().BeEquivalentTo(
        [
            new FieldError("skills[1]", "Value is required."),
            new FieldError("skills[2].level", "Invalid skill level.")
        ]);
    }

    // PersonName, OrganizationName and Technology all reject `.Any(char.IsControl)`; IsNullOrWhiteSpace
    // does not, so a plain required name accepted "Music \r\nadmin: true" verbatim. It matters most for
    // the fields that reach a column something else reads back.
    [Theory]
    [InlineData("interests")]
    [InlineData("languages")]
    [InlineData("references")]
    public async Task Import_WithAControlCharacterInARequiredName_IsAFieldError(string section)
    {
        const string Injected = "Music \r\nadmin: true";
        var draft = ValidDraft();
        draft = section switch
        {
            "interests" => draft with { Interests = [new InterestDraft(Injected)] },
            "languages" => draft with { Languages = [new LanguageDraft(Injected)] },
            _ => draft with { References = [new ReferenceDraft(Injected)] }
        };

        var result = await Import(draft);

        result.IsSuccess.Should().BeFalse();
        result.FieldErrors.Should().ContainSingle().Which.Path.Should().Be($"{section}[0].name");
        _resumes.AddCount.Should().Be(0);
    }

    // Highlights and keywords deliberately keep accepting control characters: they are unvalidated
    // encrypted string lists in the Domain and the per-section routes store them as sent, so refusing a
    // multi-line bullet here would be this endpoint inventing a rule rather than enforcing one.
    [Fact]
    public async Task Import_WithAMultiLineHighlight_IsAccepted()
    {
        var result = await Import(ValidDraft() with
        {
            Experiences =
            [
                new ExperienceDraft("Professional", "Globant", "Engineer", "2020-01-01",
                    Highlights: ["Shipped v1\nHalved build times"])
            ]
        });

        result.IsSuccess.Should().BeTrue();
        result.Resume!.Experiences.Should().ContainSingle()
            .Which.Highlights.Should().Equal("Shipped v1\nHalved build times");
    }

    // Language.Name is the only PLAINTEXT BOUNDED column on the aggregate — nvarchar(100), because the
    // engine joins on it. Before the rule landed on Language.Create this reached SQL Server as error
    // 2628, "String or binary data would be truncated", which SaveChangesExtensions does not translate:
    // a 500, with the candidate's own text written into the log by GlobalExceptionHandler.
    [Fact]
    public async Task Import_WithALanguageNameLongerThanItsColumn_IsAFieldErrorNotADatabaseTruncation()
    {
        var result = await Import(ValidDraft() with
        {
            Languages = [new LanguageDraft(new string('a', 101))]
        });

        result.IsSuccess.Should().BeFalse();
        result.FieldErrors.Should().ContainSingle()
            .Which.Should().Be(new FieldError("languages[0].name", "Language name exceeds 100 characters."));
        _resumes.AddCount.Should().Be(0);
    }

    [Fact]
    public async Task Import_WithALanguageNameExactlyAtItsColumnLength_IsAccepted()
    {
        var result = await Import(ValidDraft() with
        {
            Languages = [new LanguageDraft(new string('a', 100))]
        });

        result.IsSuccess.Should().BeTrue();
        result.Resume!.Languages.Should().ContainSingle().Which.Name.Should().HaveLength(100);
    }

    // COLLECTION MUST NOT TRUNCATE AT THE ITEM. Skipping an item whose years were rejected meant it never
    // reached AddSkill, so its duplicate went unreported: the candidate fixed the years, resubmitted and
    // only then learned about the duplicate — the second round trip this endpoint exists to remove.
    [Fact]
    public async Task Import_WithABadFieldAndADuplicateOnTheSameSkill_ReportsBoth()
    {
        var result = await Import(ValidDraft() with
        {
            Skills = [new SkillDraft("React", YearsOfExperience: "99"), new SkillDraft("react")]
        });

        result.FieldErrors.Select(error => error.Path).Should().BeEquivalentTo(
            ["skills[0].yearsOfExperience", "skills[1].name"]);
    }

    [Fact]
    public async Task Import_WithABadFieldAndADuplicateOnTheSameCertificate_ReportsBoth()
    {
        var result = await Import(ValidDraft() with
        {
            Certificates = [new CertificateDraft("CKA", Issuer: null), new CertificateDraft("cka", "CNCF")]
        });

        result.FieldErrors.Select(error => error.Path).Should().BeEquivalentTo(
            ["certificates[0].issuer", "certificates[1].name"]);
    }

    // The principle Required already applies to a blank, applied where the factory's own message carries
    // a C# parameter name: ArgumentOutOfRangeException appends "(Parameter 'yearsOfExperience')" and a
    // second line reading "Actual value was 99." Neither belongs on a review screen.
    [Fact]
    public async Task Import_WithImpossibleYearsOfExperience_ReportsNoCSharpParameterName()
    {
        var result = await Import(ValidDraft() with
        {
            Skills = [new SkillDraft("C#", YearsOfExperience: "99")]
        });

        result.FieldErrors.Should().ContainSingle()
            .Which.Should().Be(new FieldError(
                "skills[0].yearsOfExperience", "YearsOfExperience must be between 0 and 60."));
    }

    private static string CaptureMessage(Func<object> factory)
    {
        try
        {
            factory();
        }
        catch (Exception exception)
        {
            return exception.Message;
        }

        throw new InvalidOperationException(
            "The factory accepted this value, so the case proves nothing about agreement.");
    }
}
