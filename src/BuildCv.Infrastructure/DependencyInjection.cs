using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Observability;
using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Common.Services;
using BuildCv.Application.Identity;
using BuildCv.Application.Jobs;
using BuildCv.Application.Organizations;
using BuildCv.Application.Readability;
using BuildCv.Application.Resumes;
using BuildCv.Application.Scoring;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Jobs;
using BuildCv.Domain.Organizations;
using BuildCv.Domain.Readability;
using BuildCv.Domain.Resumes;
using BuildCv.Domain.Scoring;
using BuildCv.Infrastructure.Documents;
using BuildCv.Infrastructure.Lexicon;
using BuildCv.Infrastructure.Mail;
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

        // A URL rather than a secret, so unlike the two below it has a working default instead of
        // refusing to start. The placeholder IS required though: a template without it would mail every
        // user the same tokenless link, and the only symptom would be "the reset link does not work".
        services.AddOptions<PasswordResetSettings>()
            .Bind(configuration.GetSection(PasswordResetSettings.SectionName))
            .Validate(
                s => s.ResetUrlTemplate.Contains(
                    RequestPasswordResetHandler.TokenPlaceholder, StringComparison.Ordinal),
                $"PasswordReset:ResetUrlTemplate must contain {RequestPasswordResetHandler.TokenPlaceholder}.")
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

        // The third owner of a blind-index context string, and the only one that is not a lookup: it
        // signs the import-evidence token rather than indexing a column, and it is registered behind its
        // port because the Application layer verifies through it. Same reason the two above are concrete
        // — it owns its AAD context — except that this one has a port to sit behind.
        services.AddSingleton<IImportEvidenceProtector, ImportEvidenceProtector>();
        services.AddSingleton<IPasswordResetTokenProtector, PasswordResetTokenProtector>();

        // NO PROVIDER IS CHOSEN HERE, and none needs to be: SES, Postmark, SendGrid, Resend, Mailgun and
        // a self-hosted Postfix all speak SMTP, so the choice is four environment variables rather than a
        // code change. A provider SDK would have made somebody else's decision a dependency of this
        // assembly.
        //
        // THE HOST IS THE SWITCH, and there is deliberately no Enabled flag beside it: two settings that
        // can disagree about one fact is how a deployment ends up configured to send through a host it
        // was told to ignore. With no host, UnconfiguredEmailSender refuses and says so -- which is what
        // makes POST /v1/auth/password-reset answer 503 rather than telling somebody to watch an inbox
        // that will never receive anything.
        services.AddOptions<SmtpSettings>()
            .Bind(configuration.GetSection(SmtpSettings.SectionName))
            // Validated at STARTUP rather than at send time, because the send path deliberately swallows
            // its failure to avoid leaking whether an address is registered -- so a half-configured host
            // would otherwise be invisible until somebody noticed nobody was receiving mail.
            .Validate(
                s => string.IsNullOrWhiteSpace(s.Host) || !string.IsNullOrWhiteSpace(s.FromAddress),
                $"{SmtpSettings.SectionName}:FromAddress is required when Host is set.")
            .Validate(
                s => s.Port is > 0 and <= 65535,
                $"{SmtpSettings.SectionName}:Port must be a valid port.")
            .ValidateOnStart();

        var smtpHost = configuration.GetSection(SmtpSettings.SectionName)["Host"];
        if (string.IsNullOrWhiteSpace(smtpHost))
            services.AddSingleton<IEmailSender, UnconfiguredEmailSender>();
        else
            services.AddSingleton<IEmailSender, SmtpEmailSender>();

        // EXTERNAL IDENTITY PROVIDERS, registered as a collection because the use case selects by name
        // and a second provider must not require touching the handler. A verifier is registered even
        // when unconfigured: it answers IsConfigured false and opens no socket, which keeps "is Google
        // enabled here" a question with one answer rather than a difference between the container and
        // the behaviour.
        //
        // SINGLETON, and that is load-bearing: the verifier owns a ConfigurationManager that caches and
        // rotates Google's signing keys in the background. Per-request instances would re-fetch the
        // discovery document on every sign-in, making Google's availability a dependency of every
        // request rather than of a refresh.
        var googleSettings = new GoogleAuthSettings();
        configuration.GetSection(GoogleAuthSettings.SectionName).Bind(googleSettings);
        services.AddSingleton(googleSettings);
        services.AddSingleton<IExternalIdentityVerifier, GoogleIdentityVerifier>();

        // TryAdd so the Api can register an HttpContext-backed principal without removing this one.
        // Nothing in Application consumes ICurrentUser yet; the audit interceptor does.
        services.TryAddSingleton<ICurrentUser, UnknownCurrentUser>();

        // A singleton, and that is what scopes its meter: BuildCvMetrics stamps the meter with itself,
        // so two composed hosts in one process — which is what an xUnit assembly full of
        // WebApplicationFactory instances is — publish to two distinguishable meters rather than to one
        // global. Same mechanism IMeterFactory uses, without needing the ASP.NET Core assembly it lives
        // in. See BuildCvMetrics.
        services.AddSingleton<BuildCvMetrics>();

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

        // A singleton for the same reason the scoring engine is one: it is a pure function of its
        // arguments and holds no state. It takes no dependencies at all — every readability rule reads
        // the resume and the closed action-verb vocabulary beside it, and nothing else.
        services.AddSingleton<IReadabilityEngine, ReadabilityEngine>();

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
        services.AddScoped<
            ICommandHandler<SignInWithExternalProviderCommand, Result<AuthResult>>,
            SignInWithExternalProviderHandler>();
        services.AddScoped<ICommandHandler<DeleteAccountCommand, Result>, DeleteAccountHandler>();
        services.AddScoped<ICommandHandler<RequestPasswordResetCommand, Result>, RequestPasswordResetHandler>();
        services.AddScoped<ICommandHandler<ConfirmPasswordResetCommand, Result>, ConfirmPasswordResetHandler>();
        services.AddScoped<ICommandHandler<VerifyEmailCommand, Result<AccountDto>>, VerifyEmailHandler>();
        services.AddScoped<ICommandHandler<RevokeSessionsCommand, Result>, RevokeSessionsHandler>();

        // Resumes
        services.AddScoped<ICommandHandler<CreateResumeCommand, Result<Resume>>, CreateResumeHandler>();
        services.AddScoped<ICommandHandler<CreateResumeFromDraftCommand, ResumeImportResult>, CreateResumeFromDraftHandler>();
        services.AddScoped<ICommandHandler<ExtractDocumentTextCommand, Result<DocumentExtraction>>, ExtractDocumentTextHandler>();
        services.AddScoped<ICommandHandler<ProposeResumeDraftFromDocumentCommand, Result<ResumeDraftProposal>>, ProposeResumeDraftFromDocumentHandler>();
        services.AddScoped<IQueryHandler<GetResumeQuery, Result<ResumeWithItemIds>>, GetResumeHandler>();
        services.AddScoped<IQueryHandler<GetResumesByOwnerQuery, Result<Page<Resume>>>, GetResumesByOwnerHandler>();
        services.AddScoped<ICommandHandler<DeleteResumeCommand, Result<ResumeId>>, DeleteResumeHandler>();
        services.AddScoped<ICommandHandler<UpdateContactInformationCommand, Result<Resume>>, UpdateContactInformationHandler>();
        services.AddScoped<ICommandHandler<AddExperienceCommand, Result<Resume>>, AddExperienceHandler>();
        services.AddScoped<ICommandHandler<AddEducationCommand, Result<Resume>>, AddEducationHandler>();
        services.AddScoped<ICommandHandler<AddSkillCommand, Result<Resume>>, AddSkillHandler>();
        // One registration behind all ten DELETE routes; the section travels on the command.
        services.AddScoped<ICommandHandler<RemoveResumeItemCommand, Result<Resume>>, RemoveResumeItemHandler>();
        services.AddScoped<ICommandHandler<RenameResumeCommand, Result<Resume>>, RenameResumeHandler>();
        services.AddScoped<ICommandHandler<AddProjectCommand, Result<Resume>>, AddProjectHandler>();
        services.AddScoped<ICommandHandler<AddCertificateCommand, Result<Resume>>, AddCertificateHandler>();
        services.AddScoped<ICommandHandler<AddLanguageCommand, Result<Resume>>, AddLanguageHandler>();
        services.AddScoped<ICommandHandler<AddAwardCommand, Result<Resume>>, AddAwardHandler>();
        services.AddScoped<ICommandHandler<AddPublicationCommand, Result<Resume>>, AddPublicationHandler>();
        services.AddScoped<ICommandHandler<AddInterestCommand, Result<Resume>>, AddInterestHandler>();
        services.AddScoped<ICommandHandler<AddReferenceCommand, Result<Resume>>, AddReferenceHandler>();

        // Candidate profile — every collection is shared with Resumes, so the ten Add* commands and
        // handlers share names with their resume twins; hence the full qualification here. The three
        // non-twin types (GetCandidateProfile, UpsertProfileContact, RemoveProfileItem) are profile-only.
        services.AddScoped<IQueryHandler<BuildCv.Application.Candidates.GetCandidateProfileQuery, Result<CandidateProfileWithItemIds>>, BuildCv.Application.Candidates.GetCandidateProfileHandler>();
        services.AddScoped<ICommandHandler<BuildCv.Application.Candidates.UpsertProfileContactCommand, Result<CandidateProfile>>, BuildCv.Application.Candidates.UpsertProfileContactHandler>();
        services.AddScoped<ICommandHandler<BuildCv.Application.Candidates.AddExperienceCommand, Result<CandidateProfile>>, BuildCv.Application.Candidates.AddExperienceHandler>();
        services.AddScoped<ICommandHandler<BuildCv.Application.Candidates.AddEducationCommand, Result<CandidateProfile>>, BuildCv.Application.Candidates.AddEducationHandler>();
        services.AddScoped<ICommandHandler<BuildCv.Application.Candidates.AddSkillCommand, Result<CandidateProfile>>, BuildCv.Application.Candidates.AddSkillHandler>();
        services.AddScoped<ICommandHandler<BuildCv.Application.Candidates.AddProjectCommand, Result<CandidateProfile>>, BuildCv.Application.Candidates.AddProjectHandler>();
        services.AddScoped<ICommandHandler<BuildCv.Application.Candidates.AddCertificateCommand, Result<CandidateProfile>>, BuildCv.Application.Candidates.AddCertificateHandler>();
        services.AddScoped<ICommandHandler<BuildCv.Application.Candidates.AddLanguageCommand, Result<CandidateProfile>>, BuildCv.Application.Candidates.AddLanguageHandler>();
        services.AddScoped<ICommandHandler<BuildCv.Application.Candidates.AddAwardCommand, Result<CandidateProfile>>, BuildCv.Application.Candidates.AddAwardHandler>();
        services.AddScoped<ICommandHandler<BuildCv.Application.Candidates.AddPublicationCommand, Result<CandidateProfile>>, BuildCv.Application.Candidates.AddPublicationHandler>();
        services.AddScoped<ICommandHandler<BuildCv.Application.Candidates.AddInterestCommand, Result<CandidateProfile>>, BuildCv.Application.Candidates.AddInterestHandler>();
        services.AddScoped<ICommandHandler<BuildCv.Application.Candidates.AddReferenceCommand, Result<CandidateProfile>>, BuildCv.Application.Candidates.AddReferenceHandler>();
        services.AddScoped<ICommandHandler<BuildCv.Application.Candidates.RemoveProfileItemCommand, Result<CandidateProfile>>, BuildCv.Application.Candidates.RemoveProfileItemHandler>();

        // Jobs
        services.AddScoped<ICommandHandler<CreateJobPostingCommand, Result<JobPosting>>, CreateJobPostingHandler>();
        services.AddScoped<IQueryHandler<GetJobPostingQuery, Result<JobPosting>>, GetJobPostingHandler>();
        services.AddScoped<IQueryHandler<GetJobPostingsByOwnerQuery, Result<Page<JobPosting>>>, GetJobPostingsByOwnerHandler>();
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
        services.AddScoped<ICommandHandler<ScoreResumeCommand, Result<ScoredAnalysisView>>, ScoreResumeHandler>();
        services.AddScoped<IQueryHandler<GetAnalysisByIdQuery, Result<AnalysisView>>, GetAnalysisByIdHandler>();
        services.AddScoped<IQueryHandler<GetAnalysisHistoryQuery, Result<Page<AnalysisView>>>, GetAnalysisHistoryHandler>();

        // Readability
        services.AddScoped<ICommandHandler<EvaluateResumeReadabilityCommand, Result<ReadabilityReport>>, EvaluateResumeReadabilityHandler>();
        services.AddScoped<IQueryHandler<GetReadabilityReportByIdQuery, Result<ReadabilityReport>>, GetReadabilityReportByIdHandler>();
        services.AddScoped<IQueryHandler<GetReadabilityHistoryQuery, Result<Page<ReadabilityReport>>>, GetReadabilityHistoryHandler>();

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

        // Scoped, because it holds the scoped DbContext. The readiness health check resolves it inside
        // the scope HealthCheckService creates per check, so a probe never shares a context with a
        // request.
        services.AddScoped<IPersistenceProbe, EfCorePersistenceProbe>();

        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IResumeRepository, ResumeRepository>();
        services.AddScoped<IJobPostingRepository, JobPostingRepository>();
        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<IAnalysisRepository, AnalysisRepository>();
        services.AddScoped<IReadabilityReportRepository, ReadabilityReportRepository>();
        services.AddScoped<ICandidateProfileRepository, CandidateProfileRepository>();
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

        // Registered on BOTH branches, so the readiness endpoint always has a probe to ask. A null
        // check in the health check instead would report ready whenever the registration was missed,
        // which is the failure direction a readiness probe must never take.
        services.AddSingleton<IPersistenceProbe, InMemoryPersistenceProbe>();

        services.AddSingleton<IAccountRepository, InMemoryAccountRepository>();
        services.AddSingleton<IRefreshTokenRepository, InMemoryRefreshTokenRepository>();
        services.AddSingleton<IJobPostingRepository, InMemoryJobPostingRepository>();
        services.AddSingleton<IOrganizationRepository, InMemoryOrganizationRepository>();

        // CONCRETE FIRST, THEN FORWARDED — and for BOTH of the stores keyed by ResumeId, because in each
        // case the forwarding is load-bearing rather than a test convenience.
        // InMemoryResumeRepository.DeleteAsync has to reach the analyses AND the readability reports
        // derived from the resume it is deleting, mirroring ResumeRepository.CascadeToAnalysesAsync and
        // CascadeToReadabilityReportsAsync, and the methods that do it are deliberately not on the ports.
        // Registered as the interfaces alone, the singletons would be different objects and each cascade
        // would empty a store nothing reads.
        //
        // The readability store is ALSO resolved concretely by the Api tests, which read its Count to
        // observe that a request wrote at all — a claim no assertion about a response body can make.
        services.AddSingleton<InMemoryAnalysisRepository>();
        services.AddSingleton<IAnalysisRepository>(
            provider => provider.GetRequiredService<InMemoryAnalysisRepository>());
        services.AddSingleton<InMemoryReadabilityReportRepository>();
        services.AddSingleton<IReadabilityReportRepository>(
            provider => provider.GetRequiredService<InMemoryReadabilityReportRepository>());
        services.AddSingleton<IResumeRepository, InMemoryResumeRepository>();

        // CONCRETE FIRST here too, for the second of the two reasons above and not the first: nothing
        // cascades into a profile, but the Api tests read its Count to observe that an import wrote the
        // candidate's master data and not only a CV — which is the whole behaviour that separates the
        // two, and one no assertion about a response body can make.
        services.AddSingleton<InMemoryCandidateProfileRepository>();
        services.AddSingleton<ICandidateProfileRepository>(
            provider => provider.GetRequiredService<InMemoryCandidateProfileRepository>());
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
