namespace BuildCv.Application.Tests.Fakes;

using BuildCv.Application.Common.Repositories;
using BuildCv.Domain.Readability;

public sealed class FakeReadabilityReportRepository : IReadabilityReportRepository
{
    private readonly List<ReadabilityReport> _reports = [];

    public IReadOnlyList<ReadabilityReport> Reports => _reports.AsReadOnly();

    // Counts the inserts. "The result says success" and "a row was written" are different claims, and
    // only this one is about the store — a handler that returned a report it never persisted would pass
    // every assertion about the response.
    public int WriteCount { get; private set; }

    public Task AddAsync(ReadabilityReport report, CancellationToken cancellationToken = default)
    {
        WriteCount++;
        _reports.Add(report);
        return Task.CompletedTask;
    }
}
