namespace BuildCv.Application.Jobs;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Domain.Common.ValueObjects;

/// <summary>
/// Proposes skill requirements from pasted offer text, for a candidate to review before importing.
/// </summary>
/// <remarks>
/// A query, not a command: it has no side effect and creates nothing. Its output feeds a review screen
/// whose confirmed result is posted to <see cref="ImportJobOfferCommand"/> — which reads only the
/// submitted draft, never this proposal, so a candidate who edits or discards a proposed requirement
/// gets the offer they confirmed rather than the one the heuristic guessed.
/// </remarks>
public sealed record ExtractJobOfferRequirementsQuery(string Text)
    : IQuery<Result<IReadOnlyList<ProposedRequirement>>>;

public sealed class ExtractJobOfferRequirementsHandler
    : IQueryHandler<ExtractJobOfferRequirementsQuery, Result<IReadOnlyList<ProposedRequirement>>>
{
    public Task<Result<IReadOnlyList<ProposedRequirement>>> Handle(
        ExtractJobOfferRequirementsQuery query, CancellationToken cancellationToken = default)
    {
        // Blank text is a 400 rather than an empty proposal: an empty paste is a client mistake, and
        // "Value is required." routes to 400 through ResultExtensions.ToHttpResult. Text that simply
        // names no recognised technology is a legitimate empty proposal, not an error.
        if (string.IsNullOrWhiteSpace(query.Text))
            return Task.FromResult(Result<IReadOnlyList<ProposedRequirement>>.Failure("Value is required."));

        var proposed = JobRequirementExtractor.Extract(query.Text);
        return Task.FromResult(Result<IReadOnlyList<ProposedRequirement>>.Success(proposed));
    }
}
