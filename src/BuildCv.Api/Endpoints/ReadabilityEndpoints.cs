using BuildCv.Api.Common;
using BuildCv.Api.Contracts;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Readability;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Readability;

namespace BuildCv.Api.Endpoints;

// Reading one stored readability run back, split from the history the way /scoring is split from
// /resumes/{id}/analyses: a report is addressed by its own id and belongs to no other resource's path,
// while the history is a fact about one CV and hangs off it.
//
// No RequireAuthorization on the group, matching /scoring. Program.cs sets a fallback policy that
// requires authentication, so the route is closed by default; the ownership decision is the handler's,
// because a report carries no owner column for a policy to read.
public static class ReadabilityEndpoints
{
    public static RouteGroupBuilder MapReadabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/readability").WithTags("Readability");

        // The same DTO POST /v1/resumes/{id}/readability answers, deliberately: a second shape for one
        // aggregate is how two contracts for one thing start, and a candidate comparing what they were
        // told last month with what they are told today must be comparing like with like.
        //
        // ReadabilityResponse.From is also where the recommendations get their order, and it matters
        // more here than on the POST. A freshly evaluated report still carries the order the Application
        // layer built it in, while one loaded from the database is an honest SET — the Recommendations
        // table has no Rank column by design — so without that call this endpoint would render whatever
        // order the server happened to return, differently between two reads of the same row.
        group.MapGet("/{reportId:guid}", async (
            Guid reportId,
            IQueryHandler<GetReadabilityReportByIdQuery, Result<ReadabilityReport>> handler,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new GetReadabilityReportByIdQuery(
                httpContext.User.GetAccountId(), new ReadabilityReportId(reportId)), cancellationToken);

            return result.ToHttpResult(report => Results.Ok(ReadabilityResponse.From(report)));
        })
        .Produces<ReadabilityResponse>(StatusCodes.Status200OK)
        // STILL HAND-LISTED rather than through ProducesResultProblems(), but the list is now the same
        // three that helper would give. The reasoning changed under it, so it is restated rather than
        // replaced: GetReadabilityReportByIdHandler makes no Domain-factory call, so nothing the HANDLER
        // returns can fall to ToHttpResult's else-arm — the 400 comes from the line above it.
        //
        // A zero GUID in the path is what reaches it. `new ReadabilityReportId(Guid.Empty)` throws
        // before the handler runs, and that used to be a bare ArgumentException matching no
        // IExceptionHandler branch, so this route and every other by-id route answered 500 with a C#
        // parameter name in the detail. It is EmptyIdentifierException now — a DomainException, which
        // DomainExceptionHandler answers as a 400 — so the response this comment used to state as
        // unreachable is the ordinary one, and declaring it is no longer documenting a lie.
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        // 401 and 429 ARE reachable — the fallback policy and the global 100/min limiter — so unlike
        // the scoring sibling, which omits both, they are declared.
        .ProducesAuthProblems()
        .WithSummary("Returns one stored readability report, readable only by the owner of the resume it graded.")
        .WithDescription(
            "A readability report has no owner of its own: it belongs to a resume, and that resume's "
            + "owner is the only account that may read it. Deleting the resume hides every report derived "
            + "from it, so a previously readable id then answers 404. "
            + "THE NUMBERS ARE AS THEY WERE TAKEN and are not re-measured on read: a report grades the CV "
            + "as it stood at `evaluatedAt`, and there is no `isStale` here — unlike an analysis, a report "
            + "records nothing about the resume's state to compare today's against. To find out where the "
            + "CV stands now, POST /v1/resumes/{id}/readability again. "
            + "`readabilityScore` is NOT `overallScore` and the two must never be added together: one "
            + "grades the resume, the other grades a match against one job posting.");

        return group;
    }
}
