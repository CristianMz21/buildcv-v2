namespace BuildCv.Api.Contracts;

public sealed record CreateJobRequest(
    string Title,
    string CompanyName,
    Guid? CompanyId,
    string? Description);
