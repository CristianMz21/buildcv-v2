namespace BuildCv.Application.Jobs;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;

// Limit and Cursor arrive RAW, exactly as the query string carried them, matching
// GetResumesByOwnerQuery: a malformed cursor comes back as an ordinary Result failure rather than as a
// second, hand-rolled error path in the Api layer.
//
// ONE ACCOUNT ID, not two. GetResumesByOwnerQuery carries a RequesterId and an OwnerId because an admin
// may read another account's resumes; nothing here reads another account's postings, so a second
// parameter would be a hole waiting for a handler to forget to check it.
public sealed record GetJobPostingsByOwnerQuery(
    AccountId RequesterId,
    int? Limit = null,
    string? Cursor = null)
    : IQuery<Result<Page<JobPosting>>>;

// EVERY POSTING THE CALLER OWNS, including ones they created through POST /v1/jobs — not only the
// offers they imported at POST /v1/job-offers/import. The route that serves this is /v1/job-offers, so
// the wider answer is the decision worth writing down.
//
// There is nothing on the row that could express the narrower one. Both creation paths end in the same
// factory — JobOfferDraftValidator and CreateJobPostingHandler both call JobPosting.Create(ownerId,
// title, companyName) — and both set OwnerId to the caller, so no column records which route wrote it.
// The two nearest proxies are wrong rather than merely imprecise: Status == Draft matches a recruiter's
// unpublished posting too, and CompanyId is null matches a POST /v1/jobs body that named a company by
// name instead of by organization. Filtering on either would hide postings a candidate really owns,
// which is worse than showing a recruiter their own work under a candidate-shaped route.
//
// A provenance column is the alternative and is not worth its cost: it would be a second answer to
// "whose is this and how did it get here", written on every insert forever, whose only consumer is a
// list filter — and the platform would then have to keep it true through every future creation path.
//
// The practical shape of it: a Candidate cannot reach POST /v1/jobs at all (that route requires the
// Recruiter policy), so for the account this route was built for the two answers are identical. A
// recruiter reading it gets everything they own in one list rather than two that could disagree.
public sealed class GetJobPostingsByOwnerHandler(IJobPostingRepository jobPostingRepository)
    : IQueryHandler<GetJobPostingsByOwnerQuery, Result<Page<JobPosting>>>
{
    public async Task<Result<Page<JobPosting>>> Handle(
        GetJobPostingsByOwnerQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            // NO AUTHORIZATION READ, and its absence is the authorization rather than an omission: the
            // owner this query filters on IS the requester, so there is no other account's data for a
            // check to protect. GetAnalysisHistoryHandler needs one because the resource it lists is
            // named by the caller and owned by somebody; nothing here is named by the caller at all.
            //
            // That is also why the cursor is parsed first here and second there. There is no ordering to
            // get wrong, because there is no refusal for a malformed cursor to be distinguished from.
            var page = PageRequest.Create(query.Limit, query.Cursor);
            if (!page.IsSuccess)
                return Result<Page<JobPosting>>.Failure(page.Error!);

            var postings = await jobPostingRepository.GetPageByOwnerIdAsync(
                query.RequesterId, page.Value!, cancellationToken);

            // Handed back exactly as the store produced it. Nothing here may re-shape a page, because
            // Page<T>.From is the only copy of the boundary arithmetic.
            return Result<Page<JobPosting>>.Success(postings);
        }
        catch (DomainException ex)
        {
            return Result<Page<JobPosting>>.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Result<Page<JobPosting>>.Failure(ex.Message);
        }
    }
}
