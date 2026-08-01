using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Persistence.BlindIndexes;
using BuildCv.Infrastructure.Persistence.EfCore;
using BuildCv.Infrastructure.Security.Encryption;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

// Builds the real repositories over a real context, with the real blind-index and encryption
// primitives. Nothing here is a stand-in: a fake index would make every lookup test pass while proving
// nothing about the digest that actually reaches the WHERE clause.
internal static class TestRepositories
{
    public static AccountRepository Accounts(BuildCvDbContext context, IBlindIndex? blindIndex = null) =>
        new(context, new AccountEmailIndex(blindIndex ?? PersistenceTestContext.BlindIndex()), TimeProvider.System);

    public static RefreshTokenRepository RefreshTokens(BuildCvDbContext context, IBlindIndex? blindIndex = null) =>
        new(context, new RefreshTokenIndex(blindIndex ?? PersistenceTestContext.BlindIndex()));

    public static ResumeRepository Resumes(BuildCvDbContext context) => new(context);

    public static JobPostingRepository JobPostings(BuildCvDbContext context) => new(context);

    public static OrganizationRepository Organizations(BuildCvDbContext context) =>
        new(context, TimeProvider.System);

    public static AnalysisRepository Analyses(BuildCvDbContext context) => new(context);
}
