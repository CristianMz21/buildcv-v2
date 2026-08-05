using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Application.Identity;
using BuildCv.Application.Jobs;
using BuildCv.Application.Organizations;
using BuildCv.Application.Resumes;
using BuildCv.Application.Scoring;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using BuildCv.Infrastructure.Documents;
using BuildCv.Infrastructure.Lexicon;
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Persistence.BlindIndexes;
using BuildCv.Infrastructure.Persistence.EfCore;
using BuildCv.Infrastructure.Persistence.Interceptors;
using BuildCv.Infrastructure.Security;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BuildCv.Infrastructure;

public static class DependencyInjection
{
    // Matches Microsoft.Extensions.Hosting's Environments without taking a dependency on the hosting
    // abstractions for two strings.
    private const string DevelopmentEnvironment = "Development";
    private const string ProductionEnvironment = "Production";

    /// <param name="environmentName">
    /// The host's environment name. Required, and deliberately has no default: it is what decides
    /// whether the in-memory store may be registered and whether the local connection string may be
    /// used, so a caller that omitted it would fail OPEN — a new host that forgot the argument would
    /// silently inherit permission to keep accounts in a dictionary and to dial localhost with the
    /// committed development credentials. Composition without a real host (registration tests,
    /// tooling) has to name an environment like everything else.
    /// </param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        services.AddOptions<JwtSettings>()
            .Bind(configuration.GetSection(JwtSettings.SectionName))
            .Validate(s => s.SigningKey is not null && s.SigningKey.Length >= 32,
                "Jwt:SigningKey must be at least 32 characters.")
            .ValidateOnStart();

        // Field encryption keys are required, exactly like the JWT signing key: a host with no
        // usable key ring must refuse to start rather than discover the problem on the first write.
        // EncryptionSettingsValidator reports which key id is wrong, which a single Validate
        // predicate cannot do.
        services.AddOptions<EncryptionSettings>()
            .Bind(configuration.GetSection(EncryptionSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EncryptionSettings>, EncryptionSettingsValidator>();

        services.AddSingleton(provider =>
            new EncryptionKeyRing(provider.GetRequiredService<IOptions<EncryptionSettings>>().Value));
        // Narrowed here, deliberately: the ring never sees Encryption:ActiveKeyId, so the two
        // rotation pointers cannot be re-coupled by a one-line fallback inside it.
        services.AddSingleton(provider =>
            new BlindIndexKeyRing(provider.GetRequiredService<IOptions<EncryptionSettings>>().Value.BlindIndex));
        services.AddSingleton<IFieldEncryptor, AesGcmFieldEncryptor>();
        services.AddSingleton<IBlindIndex, HmacBlindIndex>();

        // The only supported entry points to a blind-index digest. Registered as concrete types
        // rather than behind an interface on purpose: they exist to make the CONTEXT string and the
        // normalized input impossible to get wrong, and an interface that took (string, string)
        // would hand both back to every caller.
        services.AddSingleton<AccountEmailIndex>();
        services.AddSingleton<RefreshTokenIndex>();

        // TryAdd so the Api can register an HttpContext-backed principal without removing this one.
        // Nothing in Application consumes ICurrentUser yet; the audit interceptor does.
        services.TryAddSingleton<ICurrentUser, UnknownCurrentUser>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<ITokenService, TokenService>();

        AddPersistence(services, configuration, environmentName);

        // Parsed HERE rather than behind a lazy static, so a malformed lexicon stops the host at
        // composition with a message naming the offending line — the same posture as the in-memory
        // persistence guard below. It is a scoring INPUT, so an instance shared for the host's lifetime
        // is what lets the singleton engine hold one.
        services.AddSingleton<ISkillLexicon>(SkillLexicon.Load());
        services.AddSingleton<IScoringEngine, ScoringEngine>();

        // Document text extraction. The dispatcher is the port; the per-format adapters are registered
        // as themselves so the graph stays explicit — there is no reflection-driven "all extractors"
        // collection to quietly pick up a fourth format nobody reviewed.
        services.AddSingleton<PdfPigTextExtractor>();
        services.AddSingleton<OpenXmlDocxTextExtractor>();
        services.AddSingleton<PlainTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();

        // PDF-only column-layout detection, a separate best-effort signal that feeds ResumeTextParser's
        // two-column warning. Not part of the text extractor: only a PDF carries the word geometry.
        services.AddSingleton<IPdfColumnDetector, PdfColumnDetector>();

        // Identity
        services.AddScoped<ICommandHandler<RegisterAccountCommand, Result<AccountDto>>, RegisterAccountHandler>();
        services.AddScoped<ICommandHandler<LoginCommand, Result<AuthResult>>, LoginHandler>();
        services.AddScoped<ICommandHandler<RefreshAccessTokenCommand, Result<AuthResult>>, RefreshAccessTokenHandler>();
        services.AddScoped<IQueryHandler<GetAccountQuery, Result<AccountDto>>, GetAccountHandler>();
        services.AddScoped<ICommandHandler<ChangePasswordCommand, Result<AccountDto>>, ChangePasswordHandler>();
        services.AddScoped<ICommandHandler<VerifyEmailCommand, Result<AccountDto>>, VerifyEmailHandler>();
        services.AddScoped<ICommandHandler<RevokeSessionsCommand, Result>, RevokeSessionsHandler>();

        // Resumes
        services.AddScoped<ICommandHandler<CreateResumeCommand, Result<Resume>>, CreateResumeHandler>();
        services.AddScoped<ICommandHandler<CreateResumeFromDraftCommand, ResumeImportResult>, CreateResumeFromDraftHandler>();
        services.AddScoped<ICommandHandler<ExtractDocumentTextCommand, Result<DocumentExtraction>>, ExtractDocumentTextHandler>();
        services.AddScoped<ICommandHandler<ProposeResumeDraftFromDocumentCommand, Result<ResumeDraftProposal>>, ProposeResumeDraftFromDocumentHandler>();
        services.AddScoped<IQueryHandler<GetResumeQuery, Result<Resume>>, GetResumeHandler>();
        services.AddScoped<IQueryHandler<GetResumesByOwnerQuery, Result<Page<Resume>>>, GetResumesByOwnerHandler>();
        services.AddScoped<ICommandHandler<DeleteResumeCommand, Result<ResumeId>>, DeleteResumeHandler>();
        services.AddScoped<ICommandHandler<UpdateContactInformationCommand, Result<Resume>>, UpdateContactInformationHandler>();
        services.AddScoped<ICommandHandler<AddExperienceCommand, Result<Resume>>, AddExperienceHandler>();
        services.AddScoped<ICommandHandler<AddEducationCommand, Result<Resume>>, AddEducationHandler>();
        services.AddScoped<ICommandHandler<AddSkillCommand, Result<Resume>>, AddSkillHandler>();
        services.AddScoped<ICommandHandler<AddProjectCommand, Result<Resume>>, AddProjectHandler>();
        services.AddScoped<ICommandHandler<AddCertificateCommand, Result<Resume>>, AddCertificateHandler>();
        services.AddScoped<ICommandHandler<AddLanguageCommand, Result<Resume>>, AddLanguageHandler>();
        services.AddScoped<ICommandHandler<AddAwardCommand, Result<Resume>>, AddAwardHandler>();
        services.AddScoped<ICommandHandler<AddPublicationCommand, Result<Resume>>, AddPublicationHandler>();
        services.AddScoped<ICommandHandler<AddInterestCommand, Result<Resume>>, AddInterestHandler>();
        services.AddScoped<ICommandHandler<AddReferenceCommand, Result<Resume>>, AddReferenceHandler>();

        // Jobs
        services.AddScoped<ICommandHandler<CreateJobPostingCommand, Result<JobPosting>>, CreateJobPostingHandler>();
        services.AddScoped<IQueryHandler<GetJobPostingQuery, Result<JobPosting>>, GetJobPostingHandler>();
        services.AddScoped<ICommandHandler<PublishJobPostingCommand, Result<JobPosting>>, PublishJobPostingHandler>();
        services.AddScoped<ICommandHandler<CloseJobPostingCommand, Result<JobPosting>>, CloseJobPostingHandler>();
        services.AddScoped<ICommandHandler<ImportJobOfferCommand, JobOfferImportResult>, ImportJobOfferHandler>();
        services.AddScoped<IQueryHandler<ExtractJobOfferRequirementsQuery, Result<IReadOnlyList<ProposedRequirement>>>, ExtractJobOfferRequirementsHandler>();

        // Organizations
        services.AddScoped<ICommandHandler<CreateOrganizationCommand, Result<Organization>>, CreateOrganizationHandler>();
        services.AddScoped<ICommandHandler<AddMemberCommand, Result<Organization>>, AddMemberHandler>();
        services.AddScoped<ICommandHandler<RemoveMemberCommand, Result<Organization>>, RemoveMemberHandler>();
        services.AddScoped<IQueryHandler<GetOrganizationQuery, Result<Organization>>, GetOrganizationHandler>();
        services.AddScoped<IQueryHandler<GetOrganizationBySlugQuery, Result<Organization>>, GetOrganizationBySlugHandler>();

        // Scoring
        services.AddScoped<ICommandHandler<ScoreResumeCommand, Result<AnalysisView>>, ScoreResumeHandler>();
        services.AddScoped<IQueryHandler<GetAnalysisByIdQuery, Result<AnalysisView>>, GetAnalysisByIdHandler>();
        services.AddScoped<IQueryHandler<GetAnalysisHistoryQuery, Result<Page<AnalysisView>>>, GetAnalysisHistoryHandler>();

        return services;
    }

    // The one place that decides where aggregates actually live.
    private static void AddPersistence(
        IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        var provider = PersistenceConfiguration.ResolveProvider(configuration);

        if (string.Equals(provider, PersistenceConfiguration.InMemoryProvider, StringComparison.OrdinalIgnoreCase))
        {
            AddInMemoryPersistence(services, configuration, environmentName);
            return;
        }

        if (string.Equals(provider, PersistenceConfiguration.SqlServerProvider, StringComparison.OrdinalIgnoreCase))
        {
            AddSqlServerPersistence(services, configuration, environmentName);
            return;
        }

        // Naming both accepted values, because the realistic way to reach this line is a typo in an
        // environment variable on a host that is otherwise configured correctly.
        throw new InvalidOperationException(
            $"{PersistenceConfiguration.ProviderKey} is '{provider}'. Supported values are "
            + $"'{PersistenceConfiguration.SqlServerProvider}' and '{PersistenceConfiguration.InMemoryProvider}'.");
    }

    private static void AddSqlServerPersistence(
        IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        // Scoped, both of them: AuditSaveChangesInterceptor depends on ICurrentUser, which the Api
        // replaces with an HttpContext-backed implementation that only means anything inside a request.
        services.AddScoped<BlindIndexSaveChangesInterceptor>();
        services.AddScoped<AuditSaveChangesInterceptor>();

        services.AddDbContext<BuildCvDbContext>((serviceProvider, options) => options
            // Resolved here rather than at registration time so composing the services never needs a
            // reachable database — several tests build the whole graph and only ever resolve a hasher.
            .UseSqlServer(
                ResolveConnectionString(configuration, environmentName),
                sqlServer => sqlServer.EnableRetryOnFailure())

            // NoTracking is the default because most reads are reads. The repositories say AsTracking()
            // explicitly on the ones that feed a mutation, which makes "this entity is about to change"
            // a visible decision at the call site instead of an ambient property of the context.
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)

            // ORDER IS PINNED, and PersistenceRegistrationTests asserts it.
            //
            // The blind-index pass runs FIRST so it only ever observes entity states the application
            // produced. The audit pass rewrites states — it converts a Deleted root into a Modified
            // tombstone — and running it first would hand the blind-index pass an Account it now sees as
            // Modified, sending it back through Compute() under the ACTIVE key. That happens to be
            // harmless today only because reassigning an equal byte[] is a no-op under EF's structural
            // comparer: a property of EF, not of this code, and not one to build a rotation on. In this
            // order the two interceptors are independent by construction.
            .AddInterceptors(
                serviceProvider.GetRequiredService<BlindIndexSaveChangesInterceptor>(),
                serviceProvider.GetRequiredService<AuditSaveChangesInterceptor>())

            // Stated rather than left to the default, and it must stay false in EVERY environment,
            // Development included. Sensitive-data logging writes parameter values into the log, and the
            // parameters on this context are blind-index digests and freshly sealed envelopes — the exact
            // material the encryption exists to keep out of a dump. A future `if (IsDevelopment())`
            // branch here would be a data leak, so PersistenceRegistrationTests reads this back off the
            // COMPOSED provider.
            .EnableSensitiveDataLogging(false));

        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IResumeRepository, ResumeRepository>();
        services.AddScoped<IJobPostingRepository, JobPostingRepository>();
        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<IAnalysisRepository, AnalysisRepository>();
    }

    // The in-memory store is a development convenience and a test double. It is registered as singletons
    // because it IS the storage, and it loses everything on restart.
    private static void AddInMemoryPersistence(
        IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        // Fails at registration, not at the first write. A host that has been told to keep user accounts
        // in a dictionary must refuse to start rather than serve traffic and lose it, and the only way to
        // find that out at runtime is that everyone is logged out after a deploy.
        if (!InMemoryIsAllowedIn(configuration, environmentName))
        {
            throw new InvalidOperationException(
                $"{PersistenceConfiguration.ProviderKey} is '{PersistenceConfiguration.InMemoryProvider}' in the "
                + $"'{environmentName}' environment, which would discard all data on restart. Use "
                + $"'{PersistenceConfiguration.SqlServerProvider}'."
                + (IsProduction(environmentName)
                    ? string.Empty
                    : $" If this really is a test host, set "
                        + $"{PersistenceConfiguration.AllowInMemoryOutsideDevelopmentKey}=true."));
        }

        services.AddSingleton<IAccountRepository, InMemoryAccountRepository>();
        services.AddSingleton<IRefreshTokenRepository, InMemoryRefreshTokenRepository>();
        services.AddSingleton<IResumeRepository, InMemoryResumeRepository>();
        services.AddSingleton<IJobPostingRepository, InMemoryJobPostingRepository>();
        services.AddSingleton<IOrganizationRepository, InMemoryOrganizationRepository>();
        services.AddSingleton<IAnalysisRepository, InMemoryAnalysisRepository>();
    }

    // Development gets it for free. Anything else has to say so — EXCEPT Production, which cannot say so
    // at all: the acknowledgement exists for a test host that deliberately builds production-SHAPED
    // configuration (Staging, in this repo's Api tests), and there is no such thing as a test host that
    // has to call itself Production. Leaving the hatch open there would mean one configuration line
    // between a live deployment and a store that forgets every account on restart.
    private static bool InMemoryIsAllowedIn(IConfiguration configuration, string environmentName)
    {
        if (IsDevelopment(environmentName))
            return true;

        if (IsProduction(environmentName))
            return false;

        return configuration.GetValue(PersistenceConfiguration.AllowInMemoryOutsideDevelopmentKey, false);
    }

    // ConnectionStrings:BuildCv is the application's setting. When it is absent the local default comes
    // from BuildCvDbContextFactory, which is the ONE committed copy of that string — appsettings used to
    // carry a second copy that nothing read and that was free to drift away from the one `dotnet ef`
    // uses. Outside Development there is no default: that string carries committed development
    // credentials, and pointing a deployed host at localhost silently is worse than refusing to start.
    private static string ResolveConnectionString(IConfiguration configuration, string environmentName)
    {
        var configured = configuration.GetConnectionString(PersistenceConfiguration.ConnectionStringName);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return IsDevelopment(environmentName)
            ? BuildCvDbContextFactory.DefaultConnectionString
            : throw new InvalidOperationException(
                $"ConnectionStrings:{PersistenceConfiguration.ConnectionStringName} must be configured in the "
                + $"'{environmentName}' environment.");
    }

    private static bool IsDevelopment(string environmentName) =>
        string.Equals(environmentName, DevelopmentEnvironment, StringComparison.OrdinalIgnoreCase);

    private static bool IsProduction(string environmentName) =>
        string.Equals(environmentName, ProductionEnvironment, StringComparison.OrdinalIgnoreCase);
}
