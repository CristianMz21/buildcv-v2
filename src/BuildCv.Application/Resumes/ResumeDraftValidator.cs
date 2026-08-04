namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using static BuildCv.Application.Common.FieldErrorCollector;

/// <summary>
/// Turns a reviewed <see cref="ResumeDraft"/> into either a complete <see cref="Resume"/> or the full
/// list of <see cref="FieldError"/>s that stopped it.
/// </summary>
/// <remarks>
/// VALIDATION AND CONSTRUCTION ARE THE SAME PASS, ON PURPOSE. Nothing here re-implements a rule: every
/// verdict is produced by calling the real factory — <see cref="PhoneNumber.Create"/>,
/// <see cref="DateRange.Create"/>, <see cref="Resume.AddSkill"/> — and catching what it threw. A
/// separate "check first, build later" validator would be two statements of the same rule, and the
/// day they disagree the API answers 200 to a request that then throws inside the handler, or 400 to
/// one the domain would have accepted. Here that divergence is not merely untested, it is
/// unrepresentable: there is only one construction.
/// <para>
/// It COLLECTS rather than fails fast. The domain throws on the first bad field, so a draft with five
/// problems would otherwise cost five round trips; every helper below records its error and returns
/// null so the walk continues. Precisely: every FIELD failure in the draft is reported, and the
/// aggregate-level rules — the case-insensitive duplicate guards on <see cref="Resume.AddSkill"/>,
/// <see cref="Resume.AddCertificate"/>, <see cref="Resume.AddLanguage"/> and
/// <see cref="Resume.AddInterest"/> — are reported too for every item whose NAME parsed, even when
/// another field of that same item failed. That is why <c>AddSkills</c> rebuilds a skill whose years
/// were rejected and <c>AddCertificates</c> substitutes an issuer. What cannot be reported is a
/// duplicate of an item whose own name failed, because there is then no name to compare.
/// </para>
/// <para>
/// The six collections with no aggregate-level rule (experiences, educations, projects, awards,
/// publications, references) do return early when a required field of an item fails, which costs
/// nothing today. If a duplicate guard is ever added to one of them, that early return has to go the
/// same way skills' did, or the guard is silently suppressed for any item that also has a field error.
/// </para>
/// <para>
/// It is ALL-OR-NOTHING. The aggregate is assembled in memory and the single return at the end hands
/// it back only when no error was recorded. A half-imported resume is worse than a rejected one: the
/// candidate cannot tell what landed, and re-importing duplicates whatever did.
/// </para>
/// </remarks>
public static class ResumeDraftValidator
{
    // Word for word what the endpoints in ResumeEndpoints.cs already answer for the same input, so a
    // candidate is not told two different things about one field depending on which route they used.
    private const string InvalidExperienceTypeMessage = "Invalid experience type.";
    private const string InvalidEducationLevelMessage = "Invalid education level.";
    private const string InvalidSkillLevelMessage = "Invalid skill level.";
    private const string InvalidLanguageProficiencyMessage = "Invalid language proficiency.";

    // Resume.Create demands a ContactInformation, and the duplicate rules this pass has to exercise
    // live on Resume.AddSkill / AddCertificate / AddLanguage / AddInterest — so the collections cannot
    // be validated without a Resume to add them to, even when the contact block itself failed. This
    // stands in for that case only.
    //
    // It cannot escape. It is used exactly where BuildContact returned null, which happens only after
    // an error was recorded, and the single return at the end of Validate hands back a rejection —
    // never the Resume — whenever the error list is non-empty.
    private static readonly ContactInformation UnusableContact =
        new(PersonName.Create("unused"), Email.Create("unused@invalid.example"));

    // Stands in for a certificate issuer that failed to build, so the item can still enter the aggregate
    // and have its NAME checked against the duplicate guard. Reached only after an error was recorded,
    // so the same single return that keeps UnusableContact in also keeps this one in.
    private static readonly OrganizationName UnusableOrganization = OrganizationName.Create("unused");

    public static ResumeImportResult Validate(AccountId ownerId, ResumeDraft draft)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(draft);

        var errors = new List<FieldError>();
        var contactDraft = draft.Contact ?? new ContactDraft();

        var profiles = BuildProfiles(contactDraft.Profiles, errors);
        var contact = BuildContact(contactDraft, profiles, errors);
        var resume = Resume.Create(ownerId, contact ?? UnusableContact);

        AddExperiences(resume, draft.Experiences, errors);
        AddEducations(resume, draft.Educations, errors);
        AddSkills(resume, draft.Skills, errors);
        AddProjects(resume, draft.Projects, errors);
        AddCertificates(resume, draft.Certificates, errors);
        AddLanguages(resume, draft.Languages, errors);
        AddAwards(resume, draft.Awards, errors);
        AddPublications(resume, draft.Publications, errors);
        AddInterests(resume, draft.Interests, errors);
        AddReferences(resume, draft.References, errors);

        return errors.Count == 0
            ? ResumeImportResult.Imported(resume)
            : ResumeImportResult.Rejected(errors);
    }

    private static IReadOnlyList<Profile> BuildProfiles(
        IReadOnlyList<ProfileDraft?>? drafts, List<FieldError> errors)
    {
        var profiles = new List<Profile>();
        ForEachCapped(drafts, "contact.profiles", ResumeDraftLimits.Profiles, errors, (item, path) =>
        {
            var network = Required($"{path}.network", item.Network, errors);
            var url = BuildOptional($"{path}.url", item.Url, Url.Create, errors);

            if (network is null)
                return;

            // Through Build even though Profile is a validation-free record today. CreateResumeFromDraft
            // has no try/catch on the premise that this pass makes every Domain call inside the harness;
            // a construction outside it is the one thing that would falsify that premise later.
            var profile = Build(path, errors, () => new Profile(network, Optional(item.Username), url));
            if (profile is not null)
                profiles.Add(profile);
        });
        return profiles;
    }

    private static ContactInformation? BuildContact(
        ContactDraft draft, IReadOnlyList<Profile> profiles, List<FieldError> errors)
    {
        var fullName = BuildRequired("contact.fullName", draft.FullName, PersonName.Create, errors);
        var email = BuildRequired("contact.email", draft.Email, Email.Create, errors);
        var phoneNumber = BuildOptional("contact.phoneNumber", draft.PhoneNumber, PhoneNumber.Create, errors);
        var website = BuildOptional("contact.website", draft.Website, Url.Create, errors);

        // Through Build for the same reason as Profile above: every Domain construction this pass makes
        // stays inside the catch harness, so the handler's lack of a try/catch keeps being justified.
        return fullName is null || email is null
            ? null
            : Build("contact", errors, () => new ContactInformation(
                fullName, email, phoneNumber, Optional(draft.Location), website, Optional(draft.Summary))
            {
                Profiles = profiles
            });
    }

    // AddExperience, not AddWorkExperience: the latter additionally throws unless the type is
    // ExperienceType.Professional, so using it would reject every volunteer entry a draft carries.
    // The draft states its own type, and AddExperienceHandler makes the same choice.
    private static void AddExperiences(
        Resume resume, IReadOnlyList<ExperienceDraft?>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "experiences", ResumeDraftLimits.Experiences, errors, (item, path) =>
        {
            var type = ParseRequiredEnum<ExperienceType>(
                $"{path}.type", item.Type, InvalidExperienceTypeMessage, errors);
            var organization = BuildRequired($"{path}.organization", item.Organization, OrganizationName.Create, errors);
            var position = Required($"{path}.position", item.Position, errors);
            var period = BuildRequiredPeriod($"{path}.start", item.Start, $"{path}.end", item.End, errors);
            var highlights = BuildTextList(item.Highlights, $"{path}.highlights", errors);

            if (type is null || organization is null || position is null || period is null)
                return;

            Add(path, errors, () => resume.AddExperience(
                new Experience(type.Value, organization, position, period, Optional(item.Summary))
                {
                    Highlights = highlights
                }));
        });

    private static void AddEducations(
        Resume resume, IReadOnlyList<EducationDraft?>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "educations", ResumeDraftLimits.Educations, errors, (item, path) =>
        {
            var institution = BuildRequired($"{path}.institution", item.Institution, OrganizationName.Create, errors);
            var period = BuildRequiredPeriod($"{path}.start", item.Start, $"{path}.end", item.End, errors);
            var level = ParseOptionalEnum<EducationLevel>(
                $"{path}.level", item.Level, InvalidEducationLevelMessage, errors);

            if (institution is null || period is null)
                return;

            // Degree is passed through untouched and is never parsed into Level — see the comment on
            // Domain.Resumes.Education.Level for why deriving one from the other is forbidden.
            Add(path, errors, () => resume.AddEducation(new Education(
                institution, Optional(item.Degree), Optional(item.FieldOfStudy), period, Optional(item.Grade), level)));
        });

    private static void AddSkills(Resume resume, IReadOnlyList<SkillDraft?>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "skills", ResumeDraftLimits.Skills, errors, (item, path) =>
        {
            var name = BuildRequired($"{path}.name", item.Name, Technology.Create, errors);
            var level = ParseOptionalEnum<SkillLevel>($"{path}.level", item.Level, InvalidSkillLevelMessage, errors);
            var years = ParseInt($"{path}.yearsOfExperience", item.YearsOfExperience, errors);

            if (name is null)
                return;

            // Reported at yearsOfExperience because that is the only value Skill.Create can reject once
            // its name argument is non-null: its single rule is the 0..60 range on that parameter.
            //
            // When it DOES reject, the skill is rebuilt without the offending value rather than skipped.
            // Skipping it meant the item never reached AddSkill, so its duplicate went unreported and a
            // candidate who wrote React twice AND mistyped the years was told about the years, fixed
            // them, resubmitted, and only then learned about the duplicate — the second round trip this
            // endpoint exists to remove. The rebuild cannot throw (the name is non-null and the years are
            // dropped) and the aggregate is discarded on any error, so it only serves the duplicate scan.
            var skill = Build($"{path}.yearsOfExperience", errors, () => Skill.Create(name, level, years))
                ?? Skill.Create(name, level, null);

            // The duplicate guard lives in Resume.AddSkill and is case-insensitive. Because the walk is
            // in draft order, the first "React" is already in the aggregate and it is the LATER
            // occurrence that throws — which is the line the candidate has to delete, so that is the
            // index the path carries.
            Add($"{path}.name", errors, () => resume.AddSkill(skill));
        });

    private static void AddProjects(Resume resume, IReadOnlyList<ProjectDraft?>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "projects", ResumeDraftLimits.Projects, errors, (item, path) =>
        {
            var name = Required($"{path}.name", item.Name, errors);
            var period = BuildRequiredPeriod($"{path}.start", item.Start, $"{path}.end", item.End, errors);
            var repositoryUrl = BuildOptional($"{path}.repositoryUrl", item.RepositoryUrl, Url.Create, errors);
            var liveDemoUrl = BuildOptional($"{path}.liveDemoUrl", item.LiveDemoUrl, Url.Create, errors);
            var technologies = BuildTechnologyList(item.Technologies, $"{path}.technologies", errors);
            var highlights = BuildTextList(item.Highlights, $"{path}.highlights", errors);

            if (name is null || period is null)
                return;

            Add(path, errors, () => resume.AddProject(
                new Project(name, period, Optional(item.Description), repositoryUrl, liveDemoUrl)
                {
                    Technologies = technologies,
                    Highlights = highlights
                }));
        });

    private static void AddCertificates(
        Resume resume, IReadOnlyList<CertificateDraft?>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "certificates", ResumeDraftLimits.Certificates, errors, (item, path) =>
        {
            var name = Required($"{path}.name", item.Name, errors);
            var issuer = BuildRequired($"{path}.issuer", item.Issuer, OrganizationName.Create, errors);
            var credentialUrl = BuildOptional($"{path}.credentialUrl", item.CredentialUrl, Url.Create, errors);
            var validity = BuildOptionalPeriod(
                $"{path}.validityStart", item.ValidityStart, $"{path}.validityEnd", item.ValidityEnd, errors);

            if (name is null)
                return;

            // Same reason as skills: AddCertificate's duplicate guard compares the NAME, so an item whose
            // issuer failed still has to enter the aggregate or its duplicate goes unreported until the
            // next request. UnusableOrganization stands in for the issuer that could not be built; an
            // error was recorded when it failed, so this Certificate can never be persisted.
            Add($"{path}.name", errors, () => resume.AddCertificate(new Certificate(
                name, issuer ?? UnusableOrganization, Optional(item.CredentialId), credentialUrl, validity)));
        });

    private static void AddLanguages(Resume resume, IReadOnlyList<LanguageDraft?>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "languages", ResumeDraftLimits.Languages, errors, (item, path) =>
        {
            var name = Required($"{path}.name", item.Name, errors);
            var level = ParseOptionalEnum<LanguageProficiency>(
                $"{path}.level", item.Level, InvalidLanguageProficiencyMessage, errors);

            if (name is null)
                return;

            // Fluency is carried through verbatim and nothing here derives Level from it — see the
            // comment on Domain.Resumes.Language.Level. An extractor that read "Bilingüe" fills in the
            // free text; the LEVEL is the candidate's own answer on the review screen.
            Add($"{path}.name", errors, () => resume.AddLanguage(Language.Create(name, Optional(item.Fluency), level)));
        });

    private static void AddAwards(Resume resume, IReadOnlyList<AwardDraft?>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "awards", ResumeDraftLimits.Awards, errors, (item, path) =>
        {
            var title = Required($"{path}.title", item.Title, errors);
            var awarder = BuildOptional($"{path}.awarder", item.Awarder, OrganizationName.Create, errors);
            var date = ParseDate($"{path}.date", item.Date, errors);

            if (title is null)
                return;

            Add(path, errors, () => resume.AddAward(new Award(title, awarder, date, Optional(item.Summary))));
        });

    private static void AddPublications(
        Resume resume, IReadOnlyList<PublicationDraft?>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "publications", ResumeDraftLimits.Publications, errors, (item, path) =>
        {
            var title = Required($"{path}.title", item.Title, errors);
            var publisher = BuildOptional($"{path}.publisher", item.Publisher, OrganizationName.Create, errors);
            var url = BuildOptional($"{path}.url", item.Url, Url.Create, errors);
            var releaseDate = ParseDate($"{path}.releaseDate", item.ReleaseDate, errors);

            if (title is null)
                return;

            Add(path, errors, () => resume.AddPublication(
                new Publication(title, publisher, url, releaseDate, Optional(item.Summary))));
        });

    private static void AddInterests(Resume resume, IReadOnlyList<InterestDraft?>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "interests", ResumeDraftLimits.Interests, errors, (item, path) =>
        {
            var name = Required($"{path}.name", item.Name, errors);
            var keywords = BuildTextList(item.Keywords, $"{path}.keywords", errors);

            if (name is null)
                return;

            Add($"{path}.name", errors, () => resume.AddInterest(new Interest(name) { Keywords = keywords }));
        });

    private static void AddReferences(Resume resume, IReadOnlyList<ReferenceDraft?>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "references", ResumeDraftLimits.References, errors, (item, path) =>
        {
            var name = Required($"{path}.name", item.Name, errors);
            var company = BuildOptional($"{path}.company", item.Company, OrganizationName.Create, errors);
            var email = BuildOptional($"{path}.email", item.Email, Email.Create, errors);
            var phoneNumber = BuildOptional($"{path}.phoneNumber", item.PhoneNumber, PhoneNumber.Create, errors);

            if (name is null)
                return;

            Add(path, errors, () => resume.AddReference(new Reference(
                name, Optional(item.Position), company, email, phoneNumber, Optional(item.ReferenceText))));
        });

    // Highlights and keywords, which take RequiredText rather than Required: the Domain accepts control
    // characters in these lists today (they are unvalidated encrypted string lists, and the per-section
    // routes store them as sent), so refusing a bullet that happens to contain a newline would be this
    // endpoint inventing a rule rather than enforcing one.
    private static IReadOnlyList<string> BuildTextList(
        IReadOnlyList<string?>? values, string path, List<FieldError> errors)
    {
        var accepted = new List<string>();
        ForEachCapped(values, path, ResumeDraftLimits.TextItems, errors, (value, itemPath) =>
        {
            var text = RequiredText(itemPath, value, errors);
            if (text is not null)
                accepted.Add(text);
        });
        return accepted;
    }

    private static IReadOnlyList<Technology> BuildTechnologyList(
        IReadOnlyList<string?>? values, string path, List<FieldError> errors)
    {
        var accepted = new List<Technology>();
        ForEachCapped(values, path, ResumeDraftLimits.TextItems, errors, (value, itemPath) =>
        {
            var technology = BuildRequired(itemPath, value, Technology.Create, errors);
            if (technology is not null)
                accepted.Add(technology);
        });
        return accepted;
    }
}
