namespace BuildCv.Application.Resumes;

using System.Globalization;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

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
/// null so the walk continues.
/// </para>
/// <para>
/// It is ALL-OR-NOTHING. The aggregate is assembled in memory and the single return at the end hands
/// it back only when no error was recorded. A half-imported resume is worse than a rejected one: the
/// candidate cannot tell what landed, and re-importing duplicates whatever did.
/// </para>
/// </remarks>
public static class ResumeDraftValidator
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string RequiredMessage = "Value is required.";
    private const string InvalidDateMessage = "Invalid date. Expected yyyy-MM-dd.";
    private const string InvalidNumberMessage = "Invalid number.";

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
        IReadOnlyList<ProfileDraft>? drafts, List<FieldError> errors)
    {
        var profiles = new List<Profile>();
        ForEachCapped(drafts, "contact.profiles", ResumeDraftLimits.Profiles, errors, (item, path) =>
        {
            var network = Required($"{path}.network", item.Network, errors);
            var url = BuildOptional($"{path}.url", item.Url, Url.Create, errors);

            if (network is not null)
                profiles.Add(new Profile(network, Optional(item.Username), url));
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

        return fullName is null || email is null
            ? null
            : new ContactInformation(
                fullName, email, phoneNumber, Optional(draft.Location), website, Optional(draft.Summary))
            {
                Profiles = profiles
            };
    }

    // AddExperience, not AddWorkExperience: the latter additionally throws unless the type is
    // ExperienceType.Professional, so using it would reject every volunteer entry a draft carries.
    // The draft states its own type, and AddExperienceHandler makes the same choice.
    private static void AddExperiences(
        Resume resume, IReadOnlyList<ExperienceDraft>? drafts, List<FieldError> errors) =>
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
        Resume resume, IReadOnlyList<EducationDraft>? drafts, List<FieldError> errors) =>
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

    private static void AddSkills(Resume resume, IReadOnlyList<SkillDraft>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "skills", ResumeDraftLimits.Skills, errors, (item, path) =>
        {
            var name = BuildRequired($"{path}.name", item.Name, Technology.Create, errors);
            var level = ParseOptionalEnum<SkillLevel>($"{path}.level", item.Level, InvalidSkillLevelMessage, errors);
            var years = ParseInt($"{path}.yearsOfExperience", item.YearsOfExperience, errors);

            if (name is null)
                return;

            // Reported at yearsOfExperience because that is the only value Skill.Create can reject once
            // its name argument is non-null: its single rule is the 0..60 range on that parameter.
            var skill = Build($"{path}.yearsOfExperience", errors, () => Skill.Create(name, level, years));
            if (skill is null)
                return;

            // The duplicate guard lives in Resume.AddSkill and is case-insensitive. Because the walk is
            // in draft order, the first "React" is already in the aggregate and it is the LATER
            // occurrence that throws — which is the line the candidate has to delete, so that is the
            // index the path carries.
            Add($"{path}.name", errors, () => resume.AddSkill(skill));
        });

    private static void AddProjects(Resume resume, IReadOnlyList<ProjectDraft>? drafts, List<FieldError> errors) =>
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
        Resume resume, IReadOnlyList<CertificateDraft>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "certificates", ResumeDraftLimits.Certificates, errors, (item, path) =>
        {
            var name = Required($"{path}.name", item.Name, errors);
            var issuer = BuildRequired($"{path}.issuer", item.Issuer, OrganizationName.Create, errors);
            var credentialUrl = BuildOptional($"{path}.credentialUrl", item.CredentialUrl, Url.Create, errors);
            var validity = BuildOptionalPeriod(
                $"{path}.validityStart", item.ValidityStart, $"{path}.validityEnd", item.ValidityEnd, errors);

            if (name is null || issuer is null)
                return;

            Add($"{path}.name", errors, () => resume.AddCertificate(
                new Certificate(name, issuer, Optional(item.CredentialId), credentialUrl, validity)));
        });

    private static void AddLanguages(Resume resume, IReadOnlyList<LanguageDraft>? drafts, List<FieldError> errors) =>
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
            Add($"{path}.name", errors, () => resume.AddLanguage(new Language(name, Optional(item.Fluency), level)));
        });

    private static void AddAwards(Resume resume, IReadOnlyList<AwardDraft>? drafts, List<FieldError> errors) =>
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
        Resume resume, IReadOnlyList<PublicationDraft>? drafts, List<FieldError> errors) =>
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

    private static void AddInterests(Resume resume, IReadOnlyList<InterestDraft>? drafts, List<FieldError> errors) =>
        ForEachCapped(drafts, "interests", ResumeDraftLimits.Interests, errors, (item, path) =>
        {
            var name = Required($"{path}.name", item.Name, errors);
            var keywords = BuildTextList(item.Keywords, $"{path}.keywords", errors);

            if (name is null)
                return;

            Add($"{path}.name", errors, () => resume.AddInterest(new Interest(name) { Keywords = keywords }));
        });

    private static void AddReferences(Resume resume, IReadOnlyList<ReferenceDraft>? drafts, List<FieldError> errors) =>
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

    // Walks one section, or refuses to. Over-cap returns WITHOUT iterating — building a hundred
    // thousand value objects is exactly the work the cap exists to decline — but only this section is
    // skipped, so the errors in the sections beside it are still collected.
    private static void ForEachCapped<TDraft>(
        IReadOnlyList<TDraft>? items, string path, int limit, List<FieldError> errors, Action<TDraft, string> handle)
    {
        if (items is null || items.Count == 0)
            return;

        if (items.Count > limit)
        {
            errors.Add(new FieldError(path, $"Too many items. At most {limit} are accepted."));
            return;
        }

        for (var index = 0; index < items.Count; index++)
            handle(items[index], $"{path}[{index}]");
    }

    private static IReadOnlyList<string> BuildTextList(
        IReadOnlyList<string?>? values, string path, List<FieldError> errors)
    {
        var accepted = new List<string>();
        ForEachCapped(values, path, ResumeDraftLimits.TextItems, errors, (value, itemPath) =>
        {
            var text = Required(itemPath, value, errors);
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

    // The ONE place a domain rule becomes a field error. Every factory in the Domain signals a
    // violation as a DomainException (invariants) or an ArgumentException (null, blank, out of range),
    // and the message it carries is the message the candidate reads.
    private static T? Build<T>(string path, List<FieldError> errors, Func<T> create) where T : class
    {
        try
        {
            return create();
        }
        catch (DomainException exception)
        {
            errors.Add(new FieldError(path, exception.Message));
            return null;
        }
        catch (ArgumentException exception)
        {
            errors.Add(new FieldError(path, exception.Message));
            return null;
        }
    }

    // Same two catches for the mutating half — Resume.AddSkill and friends enforce the duplicate rules,
    // which no constructor can see because they are about the aggregate, not the item.
    private static void Add(string path, List<FieldError> errors, Action add)
    {
        try
        {
            add();
        }
        catch (DomainException exception)
        {
            errors.Add(new FieldError(path, exception.Message));
        }
        catch (ArgumentException exception)
        {
            errors.Add(new FieldError(path, exception.Message));
        }
    }

    // Blank is reported here rather than left to the factory only because the factory's own message for
    // it names a C# parameter ("Value cannot be null. (Parameter 'value')"), which is not something to
    // put on a review screen. The VERDICT is unchanged: every factory used below rejects null and blank
    // through ArgumentException.ThrowIfNullOrWhiteSpace, so this cannot accept something they refuse.
    private static T? BuildRequired<T>(string path, string? value, Func<string, T> create, List<FieldError> errors)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new FieldError(path, RequiredMessage));
            return null;
        }

        return Build(path, errors, () => create(value));
    }

    // Blank is ABSENT, not invalid. Extraction routinely yields "" for a field it could not find, and
    // the per-section handlers already treat a missing optional as null.
    private static T? BuildOptional<T>(string path, string? value, Func<string, T> create, List<FieldError> errors)
        where T : class =>
        string.IsNullOrWhiteSpace(value) ? null : Build(path, errors, () => create(value));

    // Required plain text, for the fields a Domain record declares non-nullable while enforcing nothing
    // else about them — Experience.Position, Project.Name, Certificate.Name, Language.Name, Award.Title,
    // Publication.Title, Interest.Name, Reference.Name and Profile.Network. The non-null declaration IS
    // the rule, and it is the only rule they have.
    //
    // This is the one place the import is STRICTER than the per-section routes: nullable reference types
    // are not enforced by System.Text.Json, so POST /resumes/{id}/interests with a null name stores a
    // null name today. A draft is a whole CV, and a nameless row in it is not something a candidate can
    // see or fix on a review screen.
    private static string? Required(string path, string? value, List<FieldError> errors)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        errors.Add(new FieldError(path, RequiredMessage));
        return null;
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // yyyy-MM-dd exactly, because that is what the rest of the API already speaks: the per-section
    // routes bind DateOnly through System.Text.Json, whose converter accepts the ISO 8601 full-date
    // form and nothing else. TryParse with the ambient culture would make 03/04/2020 mean different
    // days on different servers.
    private static DateOnly? ParseDate(string path, string? value, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        errors.Add(new FieldError(path, InvalidDateMessage));
        return null;
    }

    private static int? ParseInt(string path, string? value, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        errors.Add(new FieldError(path, InvalidNumberMessage));
        return null;
    }

    private static DateRange? BuildRequiredPeriod(
        string startPath, string? start, string endPath, string? end, List<FieldError> errors)
    {
        var startDate = ParseDate(startPath, start, errors);
        var endDate = ParseDate(endPath, end, errors);

        if (startDate is null)
        {
            // A blank start is missing data. An unparseable one was already reported by ParseDate, and
            // reporting it twice would put two messages on one input.
            if (string.IsNullOrWhiteSpace(start))
                errors.Add(new FieldError(startPath, RequiredMessage));
            return null;
        }

        // An end that was sent and did not parse is already a recorded error, so nothing is silently
        // dropped by stopping here — but checking the range rule against a pair the candidate did not
        // send would answer a question they never asked.
        if (endDate is null && !string.IsNullOrWhiteSpace(end))
            return null;

        // "End date must be null or on/after start date." is DateRange.Create's own message, reported at
        // the END path because that is the input a review screen can usefully highlight.
        return Build(endPath, errors, () => DateRange.Create(startDate.Value, endDate));
    }

    private static DateRange? BuildOptionalPeriod(
        string startPath, string? start, string endPath, string? end, List<FieldError> errors)
    {
        var startDate = ParseDate(startPath, start, errors);
        var endDate = ParseDate(endPath, end, errors);

        if (startDate is null)
        {
            // AddCertificateHandler drops a lone end date on the floor (`ValidityStart is null ? null :
            // ...`). A draft is extracted text a candidate is correcting, so silently discarding a value
            // they can see on their own screen is the wrong answer; it is refused instead.
            if (string.IsNullOrWhiteSpace(start) && !string.IsNullOrWhiteSpace(end))
                errors.Add(new FieldError(startPath, RequiredMessage));
            return null;
        }

        if (endDate is null && !string.IsNullOrWhiteSpace(end))
            return null;

        return Build(endPath, errors, () => DateRange.Create(startDate.Value, endDate));
    }

    private static TEnum? ParseRequiredEnum<TEnum>(
        string path, string? value, string invalidMessage, List<FieldError> errors)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new FieldError(path, RequiredMessage));
            return null;
        }

        return ParseOptionalEnum<TEnum>(path, value, invalidMessage, errors);
    }

    // IsDefined is not belt-and-braces on top of TryParse. TryParse ACCEPTS any numeric string, and all
    // four of these enums persist as tinyint (ResumeConfiguration) through an unchecked conversion:
    // "-1" wraps to 255 — above LanguageProficiency.Native — and "99" stores as 99, silently.
    //
    // ResumeEndpoints.cs has four parse sites and only TWO of them guard this way: the education-level
    // and language-proficiency lambdas do, the skill-level and experience-type ones do not, and
    // Skill.Level and Experience.Type are tinyint columns as well. Applying it to all four here closes
    // that pre-existing gap for the import path without touching the routes that still have it.
    //
    // It lives HERE rather than in the import endpoint so there is one enum rule per draft field, and so
    // a bad level comes back as a field path beside the other failures instead of as a bare 400.
    private static TEnum? ParseOptionalEnum<TEnum>(
        string path, string? value, string invalidMessage, List<FieldError> errors)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            return parsed;

        errors.Add(new FieldError(path, invalidMessage));
        return null;
    }
}
