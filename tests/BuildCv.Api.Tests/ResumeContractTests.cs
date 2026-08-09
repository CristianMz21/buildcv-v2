using System.Text.Json;
using BuildCv.Api.Contracts;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

namespace BuildCv.Api.Tests;

// The /v1 resume wire contract, asserted against the mapper directly — the JobContractTests precedent,
// and for a stronger version of the same reason. A resume has fifteen top-level members and ten owned
// collections, so a swapped pair inside ResumeResponse.From (issuer for name, start for end, one URL
// for the other) is a real and easy bug, and plausible-looking placeholder values would hide it.
// EVERY LEAF BELOW CARRIES A DISTINCT VALUE, so any transposition fails an assertion naming both ends.
//
// It also covers what no live request can reach: `Skill.Keywords` and `Interest.Keywords` have no
// writer in src/ at all, so only a Domain-built aggregate can prove they are mapped.
public class ResumeContractTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void From_MapsEveryFieldOfTheAggregateToItsOwnPlaceOnTheWire()
    {
        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(ResumeResponse.From(WithIds(BuildFullResume())), WebOptions));
        var root = json.RootElement;

        root.GetProperty("id").GetGuid().Should().NotBeEmpty();
        root.GetProperty("ownerId").GetGuid().Should().NotBeEmpty();

        var contact = root.GetProperty("contactInformation");
        contact.GetProperty("fullName").GetString().Should().Be("Jane Candidate");
        contact.GetProperty("email").GetString().Should().Be("jane@example.com");
        contact.GetProperty("phoneNumber").GetString().Should().Be("+541155550123");
        contact.GetProperty("location").GetString().Should().Be("Buenos Aires");
        contact.GetProperty("website").GetString().Should().Be("https://jane.example.com");
        contact.GetProperty("summary").GetString().Should().Be("Backend engineer.");

        var profile = contact.GetProperty("profiles").EnumerateArray().Should().ContainSingle().Subject;
        profile.GetProperty("network").GetString().Should().Be("GitHub");
        profile.GetProperty("username").GetString().Should().Be("janedev");
        profile.GetProperty("url").GetString().Should().Be("https://github.com/janedev");

        var experience = root.GetProperty("experiences").EnumerateArray().Should().ContainSingle().Subject;
        experience.GetProperty("type").GetString().Should().Be("Professional");
        experience.GetProperty("organization").GetString().Should().Be("Mercado Libre");
        experience.GetProperty("position").GetString().Should().Be("Senior Engineer");
        experience.GetProperty("period").GetProperty("start").GetString().Should().Be("2019-03-01");
        experience.GetProperty("period").GetProperty("end").GetString().Should().Be("2023-06-30");
        experience.GetProperty("summary").GetString().Should().Be("Payments platform.");
        experience.GetProperty("highlights").EnumerateArray().Single().GetString()
            .Should().Be("Cut latency in half");

        var education = root.GetProperty("educations").EnumerateArray().Should().ContainSingle().Subject;
        education.GetProperty("institution").GetString().Should().Be("Universidad de Buenos Aires");
        education.GetProperty("degree").GetString().Should().Be("Ingeniero en Sistemas");
        education.GetProperty("fieldOfStudy").GetString().Should().Be("Software");
        education.GetProperty("period").GetProperty("start").GetString().Should().Be("2012-03-01");
        education.GetProperty("period").GetProperty("end").GetString().Should().Be("2017-12-01");
        education.GetProperty("grade").GetString().Should().Be("8.4");
        education.GetProperty("level").GetString().Should().Be("Bachelor");

        var skill = root.GetProperty("skills").EnumerateArray().Should().ContainSingle().Subject;
        skill.GetProperty("name").GetString().Should().Be("C#");
        skill.GetProperty("level").GetString().Should().Be("Advanced");
        skill.GetProperty("yearsOfExperience").GetInt32().Should().Be(7);
        skill.GetProperty("keywords").EnumerateArray().Single().GetString().Should().Be("dotnet");

        var project = root.GetProperty("projects").EnumerateArray().Should().ContainSingle().Subject;
        project.GetProperty("name").GetString().Should().Be("buildcv");
        project.GetProperty("period").GetProperty("start").GetString().Should().Be("2024-01-01");
        project.GetProperty("period").GetProperty("end").ValueKind.Should().Be(JsonValueKind.Null);
        project.GetProperty("description").GetString().Should().Be("A CV scorer.");
        project.GetProperty("repositoryUrl").GetString().Should().Be("https://github.com/janedev/buildcv");
        project.GetProperty("liveDemoUrl").GetString().Should().Be("https://buildcv.example.com");
        project.GetProperty("technologies").EnumerateArray().Single().GetString().Should().Be("fsharp");
        project.GetProperty("highlights").EnumerateArray().Single().GetString()
            .Should().Be("Deterministic scoring");

        var certificate = root.GetProperty("certificates").EnumerateArray().Should().ContainSingle().Subject;
        certificate.GetProperty("name").GetString().Should().Be("AWS Solutions Architect");
        certificate.GetProperty("issuer").GetString().Should().Be("Amazon");
        certificate.GetProperty("credentialId").GetString().Should().Be("cred-123");
        certificate.GetProperty("credentialUrl").GetString().Should().Be("https://aws.example.com/cred-123");
        certificate.GetProperty("validityPeriod").GetProperty("start").GetString().Should().Be("2024-01-01");
        certificate.GetProperty("validityPeriod").GetProperty("end").GetString().Should().Be("2027-01-01");

        var language = root.GetProperty("languages").EnumerateArray().Should().ContainSingle().Subject;
        language.GetProperty("name").GetString().Should().Be("Español");
        language.GetProperty("fluency").GetString().Should().Be("Bilingüe");
        language.GetProperty("level").GetString().Should().Be("Native");

        var award = root.GetProperty("awards").EnumerateArray().Should().ContainSingle().Subject;
        award.GetProperty("title").GetString().Should().Be("Best Hack");
        award.GetProperty("awarder").GetString().Should().Be("Hackathon AR");
        award.GetProperty("date").GetString().Should().Be("2021-11-05");
        award.GetProperty("summary").GetString().Should().Be("First place.");

        var publication = root.GetProperty("publications").EnumerateArray().Should().ContainSingle().Subject;
        publication.GetProperty("title").GetString().Should().Be("On Scoring");
        publication.GetProperty("publisher").GetString().Should().Be("ACM");
        publication.GetProperty("url").GetString().Should().Be("https://acm.example.com/on-scoring");
        publication.GetProperty("releaseDate").GetString().Should().Be("2022-05-01");
        publication.GetProperty("summary").GetString().Should().Be("A paper.");

        var interest = root.GetProperty("interests").EnumerateArray().Should().ContainSingle().Subject;
        interest.GetProperty("name").GetString().Should().Be("Climbing");
        interest.GetProperty("keywords").EnumerateArray().Single().GetString().Should().Be("bouldering");

        var reference = root.GetProperty("references").EnumerateArray().Should().ContainSingle().Subject;
        reference.GetProperty("name").GetString().Should().Be("John Manager");
        reference.GetProperty("position").GetString().Should().Be("Engineering Manager");
        reference.GetProperty("company").GetString().Should().Be("Contoso");
        reference.GetProperty("email").GetString().Should().Be("john@example.com");
        reference.GetProperty("phoneNumber").GetString().Should().Be("+541155550999");
        reference.GetProperty("referenceText").GetString().Should().Be("Would hire again.");
    }

    // The full property list, in order, at every level. A field silently disappearing from a
    // fifteen-member DTO is the failure a per-field assertion cannot see — it only checks what it
    // names — and this is the list a client developer reads the response against.
    [Fact]
    public void Serialized_CarriesExactlyTheDocumentedFieldsAtEveryLevel()
    {
        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(ResumeResponse.From(WithIds(BuildFullResume())), WebOptions));
        var root = json.RootElement;

        NamesOf(root).Should().Equal(
            "id", "ownerId", "contactInformation", "createdAt", "updatedAt", "experiences", "educations",
            "skills", "projects", "certificates", "languages", "awards", "publications", "interests",
            "references");

        NamesOf(root.GetProperty("contactInformation")).Should().Equal(
            "fullName", "email", "phoneNumber", "location", "website", "summary", "profiles");
        NamesOf(root.GetProperty("contactInformation").GetProperty("profiles")[0]).Should().Equal(
            "network", "username", "url");
        // `id` leads every entry of every collection, and it is the field DELETE /{section}/{itemId}
        // addresses. It is here in the exhaustive list rather than only in a per-field assertion so
        // that dropping it from one collection — the way a mapper refactor drops things — fails.
        NamesOf(root.GetProperty("experiences")[0]).Should().Equal(
            "id", "type", "organization", "position", "period", "summary", "highlights");
        NamesOf(root.GetProperty("experiences")[0].GetProperty("period")).Should().Equal("start", "end");
        NamesOf(root.GetProperty("educations")[0]).Should().Equal(
            "id", "institution", "degree", "fieldOfStudy", "period", "grade", "level");
        NamesOf(root.GetProperty("skills")[0]).Should().Equal(
            "id", "name", "level", "yearsOfExperience", "keywords");
        NamesOf(root.GetProperty("projects")[0]).Should().Equal(
            "id", "name", "period", "description", "repositoryUrl", "liveDemoUrl", "technologies",
            "highlights");
        NamesOf(root.GetProperty("certificates")[0]).Should().Equal(
            "id", "name", "issuer", "credentialId", "credentialUrl", "validityPeriod");
        NamesOf(root.GetProperty("languages")[0]).Should().Equal("id", "name", "fluency", "level");
        NamesOf(root.GetProperty("awards")[0]).Should().Equal(
            "id", "title", "awarder", "date", "summary");
        NamesOf(root.GetProperty("publications")[0]).Should().Equal(
            "id", "title", "publisher", "url", "releaseDate", "summary");
        NamesOf(root.GetProperty("interests")[0]).Should().Equal("id", "name", "keywords");
        NamesOf(root.GetProperty("references")[0]).Should().Equal(
            "id", "name", "position", "company", "email", "phoneNumber", "referenceText");
    }

    // The mapper must pair each entry with ITS OWN id, and the failure it can have is an off-by-one or
    // a transposition between two collections — which every assertion above would survive, because they
    // read one entry of each. So this one gives the ten collections DISJOINT id ranges and checks that
    // every entry came back with the id its position was handed.
    [Fact]
    public void From_PairsEveryEntryWithItsOwnId()
    {
        var resume = BuildFullResume();
        var ids = DisjointIds(resume);

        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(
                ResumeResponse.From(new ResumeWithItemIds(resume, ids)), WebOptions));
        var root = json.RootElement;

        foreach (var section in Enum.GetValues<ResumeSection>())
        {
            var expected = ids.For(section);
            var actual = root.GetProperty(JsonName(section)).EnumerateArray()
                .Select(entry => entry.GetProperty("id").GetInt32());

            actual.Should().Equal(expected, $"{section} entries must carry their own ids");
        }
    }

    // A misalignment is a repository bug and cannot be caused by any request, so the response must not
    // be assembled from it — Enumerable.Zip would have silently dropped the unmatched entry, taking a
    // skill off a candidate's own CV with no error anywhere.
    [Fact]
    public void From_RefusesToBuildAResponseWhenIdsAndEntriesDisagree()
    {
        var resume = BuildFullResume();
        var short_ = new Dictionary<ResumeSection, IReadOnlyList<int>>();
        foreach (var section in Enum.GetValues<ResumeSection>())
            short_[section] = [];

        var build = () => ResumeResponse.From(
            new ResumeWithItemIds(resume, new ResumeItemIds(short_)));

        build.Should().Throw<InvalidOperationException>().WithMessage("*misaligned*");
    }

    // The list shape, pinned in both directions: what it carries, and what it deliberately does not.
    //
    // The absence is the load-bearing half. GET /v1/resumes used to answer with the whole aggregate for
    // every row on the page, and the only thing stopping that from coming back is a test that fails
    // when it does — a per-field assertion on the fields that ARE there would pass either way.
    [Fact]
    public void Summary_CarriesTheCountsAndNoneOfTheEntries()
    {
        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(ResumeSummaryResponse.From(BuildFullResume()), WebOptions));
        var root = json.RootElement;

        NamesOf(root).Should().Equal(
            "id", "ownerId", "fullName", "email", "location", "createdAt", "updatedAt", "counts");

        foreach (var section in Enum.GetValues<ResumeSection>())
        {
            root.TryGetProperty(JsonName(section), out _).Should().BeFalse(
                $"a list row must not carry the {section} entries themselves");
        }

        var counts = root.GetProperty("counts");
        NamesOf(counts).Should().Equal(
            "experiences", "educations", "skills", "projects", "certificates", "languages", "awards",
            "publications", "interests", "references");

        // Read off the aggregate this test built, so a mapper that wired two counts to the same
        // collection fails rather than reporting a plausible number twice.
        counts.GetProperty("experiences").GetInt32().Should().Be(1);
        counts.GetProperty("skills").GetInt32().Should().Be(1);
        counts.GetProperty("references").GetInt32().Should().Be(1);
    }

    private static string JsonName(ResumeSection section) =>
        char.ToLowerInvariant(section.ToString()[0]) + section.ToString()[1..];

    // Sequential within a section, and a different hundred per section, so an id can only match if it
    // travelled with its own entry.
    private static ResumeItemIds DisjointIds(Resume resume)
    {
        var counts = new Dictionary<ResumeSection, int>
        {
            [ResumeSection.Experiences] = resume.Experiences.Count,
            [ResumeSection.Educations] = resume.Educations.Count,
            [ResumeSection.Skills] = resume.Skills.Count,
            [ResumeSection.Projects] = resume.Projects.Count,
            [ResumeSection.Certificates] = resume.Certificates.Count,
            [ResumeSection.Languages] = resume.Languages.Count,
            [ResumeSection.Awards] = resume.Awards.Count,
            [ResumeSection.Publications] = resume.Publications.Count,
            [ResumeSection.Interests] = resume.Interests.Count,
            [ResumeSection.References] = resume.References.Count
        };

        return new ResumeItemIds(counts.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<int>)[.. Enumerable.Range(
                ((int)entry.Key + 1) * 100, entry.Value)]));
    }

    private static ResumeWithItemIds WithIds(Resume resume) => new(resume, DisjointIds(resume));

    // Absent optional data must read as absent. The levels are the ones that matter: EducationLevel 0
    // is HighSchool and SkillLevel 0 is a real member too, so a mapper defaulting instead of preserving
    // null would state a qualification the candidate never claimed — the same rule the job posting's
    // `educationLevel` follows, restated here because this DTO carries four nullable enums.
    [Fact]
    public void From_LeavesUnstatedOptionalFieldsNull()
    {
        var resume = Resume.Create(
            AccountId.New(),
            new ContactInformation(PersonName.Create("Jane Candidate"), Email.Create("jane@example.com")));
        resume.AddSkill(Skill.Create(Technology.Create("C#")));
        resume.AddEducation(new Education(
            OrganizationName.Create("Universidad de Buenos Aires"), null, null,
            DateRange.Create(new DateOnly(2012, 3, 1)), null));
        resume.AddLanguage(Language.Create("Español"));
        resume.AddCertificate(new Certificate(
            "AWS Solutions Architect", OrganizationName.Create("Amazon"), null, null, null));

        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(ResumeResponse.From(WithIds(resume)), WebOptions));
        var root = json.RootElement;

        var contact = root.GetProperty("contactInformation");
        contact.GetProperty("phoneNumber").ValueKind.Should().Be(JsonValueKind.Null);
        contact.GetProperty("website").ValueKind.Should().Be(JsonValueKind.Null);
        contact.GetProperty("profiles").GetArrayLength().Should().Be(0);

        root.GetProperty("skills")[0].GetProperty("level").ValueKind.Should().Be(JsonValueKind.Null,
            "SkillLevel.Beginner is 0, so a default here would claim a level the candidate never stated");
        root.GetProperty("skills")[0].GetProperty("yearsOfExperience").ValueKind
            .Should().Be(JsonValueKind.Null);
        root.GetProperty("educations")[0].GetProperty("level").ValueKind.Should().Be(JsonValueKind.Null,
            "EducationLevel.HighSchool is 0, and 'not stated' is a different claim");
        root.GetProperty("educations")[0].GetProperty("period").GetProperty("end").ValueKind
            .Should().Be(JsonValueKind.Null, "an open-ended period means still studying");
        root.GetProperty("languages")[0].GetProperty("level").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("certificates")[0].GetProperty("validityPeriod").ValueKind
            .Should().Be(JsonValueKind.Null, "a certificate that never expires states no period at all");
    }

    private static IEnumerable<string> NamesOf(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name);

    // Distinct in every field, including across types a transposition could survive: the reference's
    // company is "Contoso" and not the experience's "Mercado Libre", and the project's technology is
    // "fsharp" while the skill's keyword is "dotnet".
    private static Resume BuildFullResume()
    {
        var resume = Resume.Create(
            AccountId.New(),
            new ContactInformation(
                PersonName.Create("Jane Candidate"),
                Email.Create("jane@example.com"),
                PhoneNumber.Create("+541155550123"),
                "Buenos Aires",
                Url.Create("https://jane.example.com"),
                "Backend engineer.")
            {
                Profiles = [new Profile("GitHub", "janedev", Url.Create("https://github.com/janedev"))]
            });

        resume.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create("Mercado Libre"),
            "Senior Engineer",
            DateRange.Create(new DateOnly(2019, 3, 1), new DateOnly(2023, 6, 30)),
            "Payments platform.")
        {
            Highlights = ["Cut latency in half"]
        });

        resume.AddEducation(new Education(
            OrganizationName.Create("Universidad de Buenos Aires"),
            "Ingeniero en Sistemas",
            "Software",
            DateRange.Create(new DateOnly(2012, 3, 1), new DateOnly(2017, 12, 1)),
            "8.4",
            EducationLevel.Bachelor));

        resume.AddSkill(Skill.Create(Technology.Create("C#"), SkillLevel.Advanced, 7) with
        {
            Keywords = ["dotnet"]
        });

        resume.AddProject(new Project(
            "buildcv",
            DateRange.Create(new DateOnly(2024, 1, 1)),
            "A CV scorer.",
            Url.Create("https://github.com/janedev/buildcv"),
            Url.Create("https://buildcv.example.com"))
        {
            Technologies = [Technology.Create("fsharp")],
            Highlights = ["Deterministic scoring"]
        });

        resume.AddCertificate(new Certificate(
            "AWS Solutions Architect",
            OrganizationName.Create("Amazon"),
            "cred-123",
            Url.Create("https://aws.example.com/cred-123"),
            DateRange.Create(new DateOnly(2024, 1, 1), new DateOnly(2027, 1, 1))));

        resume.AddLanguage(Language.Create("Español", "Bilingüe", LanguageProficiency.Native));

        resume.AddAward(new Award(
            "Best Hack", OrganizationName.Create("Hackathon AR"), new DateOnly(2021, 11, 5), "First place."));

        resume.AddPublication(new Publication(
            "On Scoring",
            OrganizationName.Create("ACM"),
            Url.Create("https://acm.example.com/on-scoring"),
            new DateOnly(2022, 5, 1),
            "A paper."));

        resume.AddInterest(new Interest("Climbing") { Keywords = ["bouldering"] });

        resume.AddReference(new Reference(
            "John Manager",
            "Engineering Manager",
            OrganizationName.Create("Contoso"),
            Email.Create("john@example.com"),
            PhoneNumber.Create("+541155550999"),
            "Would hire again."));

        return resume;
    }
}
