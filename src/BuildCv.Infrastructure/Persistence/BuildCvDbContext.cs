using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using BuildCv.Infrastructure.Persistence.Configurations;
using BuildCv.Infrastructure.Persistence.Converters;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;

namespace BuildCv.Infrastructure.Persistence;

public sealed class BuildCvDbContext : DbContext
{
    private readonly IFieldEncryptor _encryptor;

    public BuildCvDbContext(DbContextOptions<BuildCvDbContext> options, IFieldEncryptor encryptor)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(encryptor);
        _encryptor = encryptor;
    }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Resume> Resumes => Set<Resume>();
    public DbSet<JobPosting> JobPostings => Set<JobPosting>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Analysis> Analyses => Set<Analysis>();
    public DbSet<ReadabilityReport> ReadabilityReports => Set<ReadabilityReport>();
    public DbSet<CandidateProfile> CandidateProfiles => Set<CandidateProfile>();

    // Registered one by one instead of ApplyConfigurationsFromAssembly. Two reasons, and the first
    // is not stylistic: these configurations take a constructor dependency, and assembly scanning
    // can only new up parameterless ones. The second is the repo's standing rule — a reader should
    // be able to see every table in the model from one screen, not infer it from a scan.
    //
    // The encryptor is captured in the model, so the model is only valid for the key ring it was
    // built against. That is fine today, when the ring is process-wide. If keys ever become
    // per-tenant, this becomes a correctness bug the moment two tenants share a model instance: the
    // key-ring version must then be added to the model cache key
    // (IModelCacheKeyFactory / DbContextOptionsBuilder.ReplaceService) or tenant A's rows will be
    // encrypted under tenant B's key.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new AccountConfiguration(_encryptor));
        modelBuilder.ApplyConfiguration(new RefreshTokenConfiguration(_encryptor));
        modelBuilder.ApplyConfiguration(new ResumeConfiguration(_encryptor));
        modelBuilder.ApplyConfiguration(new JobPostingConfiguration(_encryptor));
        modelBuilder.ApplyConfiguration(new OrganizationConfiguration(_encryptor));
        modelBuilder.ApplyConfiguration(new AnalysisConfiguration(_encryptor));
        modelBuilder.ApplyConfiguration(new ReadabilityReportConfiguration(_encryptor));
        modelBuilder.ApplyConfiguration(new CandidateProfileConfiguration(_encryptor));
    }

    // Global type-level conversions. Registering them here rather than per property is what covers
    // SHADOW foreign keys: an owned collection's FK to its owner is created by convention with the
    // owner's CLR key type, and there is no PropertyBuilder to hang a converter on.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<AccountId>().HaveConversion<AccountIdConverter>();
        configurationBuilder.Properties<ResumeId>().HaveConversion<ResumeIdConverter>();
        configurationBuilder.Properties<JobPostingId>().HaveConversion<JobPostingIdConverter>();
        configurationBuilder.Properties<OrganizationId>().HaveConversion<OrganizationIdConverter>();
        configurationBuilder.Properties<AnalysisId>().HaveConversion<AnalysisIdConverter>();
        configurationBuilder.Properties<ReadabilityReportId>().HaveConversion<ReadabilityReportIdConverter>();
        configurationBuilder.Properties<CandidateProfileId>().HaveConversion<CandidateProfileIdConverter>();

        // Value objects whose classification is the same everywhere they appear. A DateRange is a
        // span of time and a Technology is a skill name — neither is ever confidential, so one
        // model-wide rule is safer than repeating the mapping at a dozen call sites.
        //
        // Deliberately NOT here: OrganizationName, Email, PersonName, PhoneNumber and Url. Those
        // appear on both sides of the classification (Organization.Name is public, an employer on a
        // resume is not), so every one of them is configured per property. A missed configuration
        // then fails the model build instead of quietly defaulting to plaintext.
        configurationBuilder.Properties<DateRange>()
            .HaveConversion<DateRangeConverter>()
            .HaveMaxLength(DateRangeConverter.MaxLength)
            .AreUnicode(false);

        configurationBuilder.Properties<Technology>()
            .HaveConversion<TechnologyConverter>()
            .HaveMaxLength(TechnologyConverter.MaxLength);

        configurationBuilder.Properties<Slug>()
            .HaveConversion<SlugConverter>()
            .HaveMaxLength(SlugConverter.MaxLength)
            .AreUnicode(false);
    }
}
