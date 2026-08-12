using BuildCv.Application.Resumes;

namespace BuildCv.Api.Endpoints;

/// <summary>
/// The URL segment each collection is addressed by, paired with the section it names. Stated once
/// because BOTH CV-shaped aggregates register the same ten DELETE routes — resumes under
/// <c>/{id}/{segment}/{itemId}</c> and the candidate profile under <c>/{segment}/{itemId}</c> — and
/// the deletes are looped off this table rather than hand-written. The POST and PUT segment strings are
/// still literals at each registration site, so a typo there fails OPEN the same way a typo here would:
/// the route simply never exists. This table protects the ten deletes; it does not claim to protect the
/// other two verbs.
/// </summary>
internal static class ItemSections
{
    public static readonly (string Segment, ResumeSection Section)[] All =
    [
        ("experiences", ResumeSection.Experiences),
        ("educations", ResumeSection.Educations),
        ("skills", ResumeSection.Skills),
        ("projects", ResumeSection.Projects),
        ("certificates", ResumeSection.Certificates),
        ("languages", ResumeSection.Languages),
        ("awards", ResumeSection.Awards),
        ("publications", ResumeSection.Publications),
        ("interests", ResumeSection.Interests),
        ("references", ResumeSection.References)
    ];
}
