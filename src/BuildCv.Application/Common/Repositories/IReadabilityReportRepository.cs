namespace BuildCv.Application.Common.Repositories;

using BuildCv.Domain.Readability;

// Readability reports. Append-only, exactly like IAnalysisRepository: a report is a fact about a moment,
// never edited afterwards, so there is no UpdateAsync to write.
//
// ONE METHOD, deliberately. Nothing in the API reads a stored report back yet -- the endpoint evaluates
// and answers in one request -- and a port method with no caller is a surface a reviewer has to reason
// about and a test has to cover for no behaviour. The keyset-paginated history read arrives with the
// endpoint that needs it, and when it does it is a GetPage*Async(key, PageRequest, ct) like every other
// list in this codebase; there are no unbounded list methods on any repository port here.
//
// A report has NO OWNER of its own, for the same reason an Analysis has none: it names a resume, and
// that resume's owner is the owner. Authorization is therefore a read in the handler rather than a
// parameter here -- denormalizing an AccountId onto an append-only fact to save that read would give the
// platform two answers to "whose is this" and no way to tell which is stale.
public interface IReadabilityReportRepository
{
    Task AddAsync(ReadabilityReport report, CancellationToken cancellationToken = default);
}
