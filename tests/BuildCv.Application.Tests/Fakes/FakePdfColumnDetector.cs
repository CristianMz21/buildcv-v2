using BuildCv.Application.Common.Services;
using BuildCv.Domain.Resumes;

namespace BuildCv.Application.Tests.Fakes;

public sealed class FakePdfColumnDetector : IPdfColumnDetector
{
    public ColumnLayout NextLayout { get; set; } = ColumnLayout.Unknown;
    public int CallCount { get; private set; }

    public Task<ColumnLayout> DetectAsync(Stream pdfContent, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(NextLayout);
    }
}
