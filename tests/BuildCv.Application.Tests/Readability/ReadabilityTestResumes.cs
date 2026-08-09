using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

namespace BuildCv.Application.Tests.Readability;

// The resumes the readability suites are built from, in one place because three files need the same two
// shapes and a drifting copy of "a complete CV" would make the 1.0 and 0.0 tests describe different
// documents.
internal static class ReadabilityTestResumes
{
    internal static readonly DateOnly ReferenceDate = new(2025, 1, 1);

    // The five documents the ATS-parseability tests are written against, named for what a candidate
    // actually uploaded rather than for the field values, so a scenario reads as a story about a file.
    //
    // NULL IS THE SIXTH AND THE DEFAULT: a resume with no signals came from no document, which is what
    // every resume built by hand or by any route other than /import/propose looks like.
    internal static ImportSignals CleanPdf =>
        ImportSignals.Create(ColumnLayout.Single, hadTextLayer: true, pageCount: 2);

    internal static ImportSignals TwoColumnPdf =>
        ImportSignals.Create(ColumnLayout.Multiple, hadTextLayer: true, pageCount: 2);

    internal static ImportSignals ScannedPdf =>
        ImportSignals.Create(ColumnLayout.Single, hadTextLayer: false, pageCount: 2);

    // A DOCX or a pasted text file: readable text, and no geometry to judge the layout from.
    internal static ImportSignals PastedText =>
        ImportSignals.Create(ColumnLayout.Unknown, hadTextLayer: true);

    internal static ImportSignals EmptyDocument => ImportSignals.Create(
        ColumnLayout.Unknown, hadTextLayer: true, pageCount: null, ImportWarningFlags.NoTextContent);

    // THE MINIMUM A RESUME CAN BE. ContactInformation requires a name and an email, so this is genuinely
    // the emptiest document the Domain can hold — there is no "no contact information" case to test.
    internal static Resume Empty(ImportSignals? importSignals = null) =>
        Resume.Create(
            AccountId.New(),
            new ContactInformation(PersonName.Create("Jane Doe"), Email.Create("jane@example.com")),
            importSignals);

    // EVERY SECTION AT ITS CEILING, which is what makes the 1.0 assertion mean "a good CV scores full
    // marks" rather than "the arithmetic happens to reach 1.0 somewhere".
    internal static Resume FullyPopulated(ImportSignals? importSignals = null)
    {
        var resume = Resume.Create(
            AccountId.New(),
            new ContactInformation(
                PersonName.Create("Jane Doe"),
                Email.Create("jane@example.com"),
                PhoneNumber.Create("+541155501234"),
                "Buenos Aires, Argentina",
                Url.Create("https://janedoe.dev"),
                "Backend engineer with eight years building payment systems."),
            importSignals);

        resume.AddSkill(Skill.Create(Technology.Create("C#")));
        resume.AddEducation(new Education(
            OrganizationName.Create("Universidad de Buenos Aires"),
            "Ingeniería en Sistemas",
            "Computer Science",
            DateRange.Create(new DateOnly(2013, 3, 1), new DateOnly(2018, 12, 1)),
            null));

        // Two contiguous roles: one transition, no break, so Chronology reaches its ceiling with a
        // transition really evaluated rather than with a single entry that has none.
        resume.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create("Acme"),
            "Backend Developer",
            DateRange.Create(new DateOnly(2019, 1, 1), new DateOnly(2022, 1, 1)))
        {
            Highlights = ["Reduced checkout latency by 40%", "Migrated 12 services to .NET 8"],
        });

        resume.AddExperience(new Experience(
            ExperienceType.Professional,
            OrganizationName.Create("Globex"),
            "Senior Backend Developer",
            DateRange.Create(new DateOnly(2022, 1, 1), new DateOnly(2024, 6, 1)))
        {
            Highlights = ["Lideré un equipo de 6 personas"],
        });

        return resume;
    }
}
