namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record GetResumeQuery(AccountId RequesterId, ResumeId ResumeId)
    : IQuery<Result<ResumeWithItemIds>>;

// THE ONE READ THAT CARRIES ENTRY IDS, and the only one that should. Every other path to a resume —
// scoring, readability, each section append — reads the aggregate to compute or to mutate, and none of
// them names a single entry, so none of them pays for the tracked load the ids require.
//
// This is the read a candidate's own editor performs, and it is the read that has to answer "which
// bullet point is this one" so a later DELETE can mean something. Without ids the only way to address
// an entry is its position in this response, and these collections come back from the store as SETS:
// the position that named a certificate in one request can name a different one in the next.
public sealed class GetResumeHandler(
    IResumeRepository resumeRepository,
    IAccountRepository accountRepository)
    : IQueryHandler<GetResumeQuery, Result<ResumeWithItemIds>>
{
    public async Task<Result<ResumeWithItemIds>> Handle(
        GetResumeQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var loaded = await resumeRepository.GetByIdWithItemIdsAsync(query.ResumeId, cancellationToken);
            if (loaded is null)
                return Result<ResumeWithItemIds>.Failure("Resume not found.");

            if (loaded.Resume.OwnerId != query.RequesterId)
            {
                var requester = await accountRepository.GetByIdAsync(query.RequesterId, cancellationToken);
                if (requester?.Role != Role.Admin)
                    return Result<ResumeWithItemIds>.Failure("Forbidden.");
            }

            return Result<ResumeWithItemIds>.Success(loaded);
        }
        catch (DomainException ex)
        {
            return Result<ResumeWithItemIds>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<ResumeWithItemIds>.Failure(ex.Message);
        }
    }
}
