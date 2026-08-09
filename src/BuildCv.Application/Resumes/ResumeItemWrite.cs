namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

/// <summary>
/// The write every "put an entry into one of a CV's ten collections" use case performs, whether it is
/// appending a new entry or replacing an existing one.
/// </summary>
/// <remarks>
/// <para>
/// ONE COPY OF THE PLUMBING, for the reason spelled out on <see cref="RemoveResumeItemHandler"/>: the
/// ten Add handlers differed in exactly one expression — the two lines that build the value and hand
/// it to the aggregate — and were otherwise the same load, the same ownership check and the same two
/// catch blocks, written ten times. Adding the replace form to each of them would have made that
/// twenty. The per-collection part stays in the per-collection file, where it belongs.
/// </para>
/// <para>
/// THE VALUE IS BUILT BEFORE ANYTHING IS LOADED, which is what <c>Func&lt;Action&lt;Resume&gt;&gt;</c>
/// buys and a plain <c>Action&lt;Resume&gt;</c> would not: a lambda that both constructs and appends
/// runs its constructor at append time, which on a replace is AFTER the entry it replaces has been
/// removed. That matters because the in-memory store hands out the stored instance itself
/// (<c>InMemoryResumeRepository.GetByIdAsync</c> returns <c>row.Item</c>), so a mutation that is never
/// saved is still a mutation there — and the whole Api suite runs on that store. A rejected value
/// would have deleted the entry the caller was trying to fix.
/// </para>
/// <para>
/// A REPLACE REMOVES BEFORE IT ADDS, and both happen inside one <c>UpdateAsync</c>. Four of these
/// collections refuse an entry whose name duplicates one already there, so adding first would refuse
/// every edit that changes something OTHER than the name — the common case. The order also cannot be
/// left to the client as delete-then-post: a post that fails after a successful delete loses what the
/// candidate wrote, which is the whole reason this route exists rather than two.
/// </para>
/// </remarks>
internal static class ResumeItemWrite
{
    public static async Task<Result<Resume>> Execute(
        IResumeRepository resumeRepository,
        AccountId requesterId,
        ResumeId resumeId,
        ResumeSection section,
        int? replacingItemId,
        Func<Action<Resume>> build,
        CancellationToken cancellationToken)
    {
        try
        {
            var append = build();

            // An append needs no ids, and GetByIdWithItemIdsAsync exists precisely so that the paths
            // which do not address an entry are not made to pay for tracking. See its remarks.
            if (replacingItemId is null)
            {
                var appendTo = await resumeRepository.GetByIdAsync(resumeId, cancellationToken);
                if (appendTo is null)
                    return Result<Resume>.Failure("Resume not found.");

                if (appendTo.OwnerId != requesterId)
                    return Result<Resume>.Failure("Forbidden.");

                append(appendTo);
                await resumeRepository.UpdateAsync(appendTo, cancellationToken);
                return Result<Resume>.Success(appendTo);
            }

            var loaded = await resumeRepository.GetByIdWithItemIdsAsync(resumeId, cancellationToken);
            if (loaded is null)
                return Result<Resume>.Failure("Resume not found.");

            var resume = loaded.Resume;
            if (resume.OwnerId != requesterId)
                return Result<Resume>.Failure("Forbidden.");

            // "not found", never "forbidden", and resolved only against a resume the caller was already
            // allowed to load — the same ordering RemoveResumeItemHandler documents. Ids are unique
            // within one CV, so aiming a valid id at somebody else's CV teaches the caller nothing.
            var position = loaded.ItemIds.PositionOf(section, replacingItemId.Value);
            if (position is null)
                return Result<Resume>.Failure($"{section} entry not found.");

            ResumeItems.RemoveAt(resume, section, position.Value);
            append(resume);

            await resumeRepository.UpdateAsync(resume, cancellationToken);
            return Result<Resume>.Success(resume);
        }
        catch (DomainException ex)
        {
            return Result<Resume>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<Resume>.Failure(ex.Message);
        }
    }
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
