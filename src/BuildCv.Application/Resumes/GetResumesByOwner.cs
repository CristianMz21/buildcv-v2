namespace BuildCv.Application.Resumes;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

// Limit and Cursor arrive RAW, exactly as the query string carried them, and are validated inside the
// handler. That is the shape every other use case here already has — CreateResumeCommand takes a
// string and builds an Email — and it is what keeps the endpoint a pure mapper: a malformed cursor
// comes back as an ordinary Result failure through ToHttpResult rather than as a second, hand-rolled
// error path in the Api layer.
public sealed record GetResumesByOwnerQuery(
    AccountId RequesterId,
    AccountId OwnerId,
    int? Limit = null,
    string? Cursor = null)
    : IQuery<Result<Page<Resume>>>;

public sealed class GetResumesByOwnerHandler(
    IResumeRepository resumeRepository,
    IAccountRepository accountRepository)
    : IQueryHandler<GetResumesByOwnerQuery, Result<Page<Resume>>>
{
    public async Task<Result<Page<Resume>>> Handle(GetResumesByOwnerQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            // Authorization first, before the cursor is so much as looked at: a caller with no business
            // reading this owner's resumes gets the same 403 whatever else it sent, so the difference
            // between "forbidden" and "malformed" never becomes a probe.
            if (query.OwnerId != query.RequesterId)
            {
                var requester = await accountRepository.GetByIdAsync(query.RequesterId, cancellationToken);
                if (requester?.Role != Role.Admin)
                    return Result<Page<Resume>>.Failure("Forbidden.");
            }

            var page = PageRequest.Create(query.Limit, query.Cursor);
            if (!page.IsSuccess)
                return Result<Page<Resume>>.Failure(page.Error!);

            var resumes = await resumeRepository.GetPageByOwnerIdAsync(query.OwnerId, page.Value!, cancellationToken);
            return Result<Page<Resume>>.Success(resumes);
        }
        catch (DomainException ex)
        {
            return Result<Page<Resume>>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<Page<Resume>>.Failure(ex.Message);
        }
    }
}
