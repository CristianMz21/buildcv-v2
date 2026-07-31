using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BuildCv.Infrastructure.Persistence.Converters;

// One converter per aggregate id. Reading goes through the id constructor, which rejects
// Guid.Empty, so a zeroed foreign key surfaces as a load failure rather than a phantom reference.
internal sealed class AccountIdConverter() : ValueConverter<AccountId, Guid>(
    id => id.Value,
    value => new AccountId(value));

internal sealed class ResumeIdConverter() : ValueConverter<ResumeId, Guid>(
    id => id.Value,
    value => new ResumeId(value));

internal sealed class JobPostingIdConverter() : ValueConverter<JobPostingId, Guid>(
    id => id.Value,
    value => new JobPostingId(value));

internal sealed class OrganizationIdConverter() : ValueConverter<OrganizationId, Guid>(
    id => id.Value,
    value => new OrganizationId(value));

internal sealed class AnalysisIdConverter() : ValueConverter<AnalysisId, Guid>(
    id => id.Value,
    value => new AnalysisId(value));
