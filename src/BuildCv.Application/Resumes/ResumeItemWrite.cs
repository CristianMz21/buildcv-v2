namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

/// <summary>
/// The write every "put an entry into one of a CV's ten collections" use case performs, whether it is
/// appending a new entry or replacing an existing one.
/// </summary>
/// <remarks>
/// THE RESUME HALF OF <see cref="ItemWrite"/>. Every one of these use cases performs the same
/// load/check/remove/save, and that plumbing is the generic core; this type supplies the resume part
/// and keeps the public signature the ten Add handlers call. The load is the two-shape one:
/// <see cref="IResumeRepository.GetByIdAsync"/> for an append — returning <c>(resume, null)</c>, so a
/// path that addresses no entry is spared the per-entry id walk — and
/// <see cref="IResumeRepository.GetByIdWithItemIdsAsync"/> for a replace, returning <c>(resume, ids)</c>.
/// The per-collection build — the one expression the ten Add handlers differ in — stays in the
/// per-collection file, where it belongs. The rules (the value is built before anything is loaded, a
/// replace removes before it adds, "not found" never "forbidden") are on <see cref="ItemWrite"/> and are
/// not restated here, so they cannot drift.
/// </remarks>
internal static class ResumeItemWrite
{
    public static Task<Result<Resume>> Execute(
        IResumeRepository resumeRepository,
        AccountId requesterId,
        ResumeId resumeId,
        ResumeSection section,
        int? replacingItemId,
        Func<Action<Resume>> build,
        CancellationToken cancellationToken)
        => ItemWrite.Execute(
            load: async token =>
            {
                // An append needs no ids, and GetByIdWithItemIdsAsync exists precisely so that the paths
                // which do not address an entry are spared the per-entry id walk. See its remarks.
                if (replacingItemId is null)
                {
                    var resume = await resumeRepository.GetByIdAsync(resumeId, token);
                    return (resume, (ResumeItemIds?)null);
                }

                var loaded = await resumeRepository.GetByIdWithItemIdsAsync(resumeId, token);
                return loaded is null ? (null, null) : (loaded.Resume, loaded.ItemIds);
            },
            ownerIdOf: resume => resume.OwnerId,
            removeAt: ResumeItems.RemoveAt,
            save: resumeRepository.UpdateAsync,
            requesterId: requesterId,
            section: section,
            replacingItemId: replacingItemId,
            notFoundMessage: "Resume not found.",
            build: build,
            cancellationToken: cancellationToken);
}

/// <summary>
/// Addresses one entry of one of a resume's ten collections by its position.
/// </summary>
/// <remarks>
/// BY POSITION, not by value. Six of these collections accept duplicates, and removing by value takes
/// the first match — which would delete an entry the caller never named while leaving the one it did.
/// See the remarks on <c>Resume.RemoveAt</c>. The switch is exhaustive over a closed enum, so a
/// collection added to the aggregate without a case here does not compile.
/// </remarks>
internal static class ResumeItems
{
    public static void RemoveAt(Resume resume, ResumeSection section, int position)
    {
        switch (section)
        {
            case ResumeSection.Experiences: resume.RemoveExperienceAt(position); break;
            case ResumeSection.Educations: resume.RemoveEducationAt(position); break;
            case ResumeSection.Skills: resume.RemoveSkillAt(position); break;
            case ResumeSection.Projects: resume.RemoveProjectAt(position); break;
            case ResumeSection.Certificates: resume.RemoveCertificateAt(position); break;
            case ResumeSection.Languages: resume.RemoveLanguageAt(position); break;
            case ResumeSection.Awards: resume.RemoveAwardAt(position); break;
            case ResumeSection.Publications: resume.RemovePublicationAt(position); break;
            case ResumeSection.Interests: resume.RemoveInterestAt(position); break;
            case ResumeSection.References: resume.RemoveReferenceAt(position); break;
            default: throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown section.");
        }
    }
}
