using BuildCv.Application.Common.Abstractions;
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
using BuildCv.Infrastructure.Persistence;
using BuildCv.Infrastructure.Persistence.BlindIndexes;
using BuildCv.Infrastructure.Security;
using BuildCv.Infrastructure.Security.Encryption;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BuildCv.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
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
        services.AddSingleton<IAccountRepository, InMemoryAccountRepository>();
        services.AddSingleton<IRefreshTokenRepository, InMemoryRefreshTokenRepository>();
        services.AddSingleton<IResumeRepository, InMemoryResumeRepository>();
        services.AddSingleton<IJobPostingRepository, InMemoryJobPostingRepository>();
        services.AddSingleton<IOrganizationRepository, InMemoryOrganizationRepository>();
        services.AddSingleton<IAnalysisRepository, InMemoryAnalysisRepository>();

        services.AddSingleton<IScoringEngine, ScoringEngine>();

        // Identity
        services.AddScoped<ICommandHandler<RegisterAccountCommand, Result<AccountDto>>, RegisterAccountHandler>();
        services.AddScoped<ICommandHandler<LoginCommand, Result<AuthResult>>, LoginHandler>();
        services.AddScoped<ICommandHandler<RefreshAccessTokenCommand, Result<AuthResult>>, RefreshAccessTokenHandler>();
        services.AddScoped<IQueryHandler<GetAccountQuery, Result<AccountDto>>, GetAccountHandler>();
        services.AddScoped<ICommandHandler<ChangePasswordCommand, Result<AccountDto>>, ChangePasswordHandler>();
        services.AddScoped<ICommandHandler<VerifyEmailCommand, Result<AccountDto>>, VerifyEmailHandler>();

        // Resumes
        services.AddScoped<ICommandHandler<CreateResumeCommand, Result<Resume>>, CreateResumeHandler>();
        services.AddScoped<IQueryHandler<GetResumeQuery, Result<Resume>>, GetResumeHandler>();
        services.AddScoped<IQueryHandler<GetResumesByOwnerQuery, Result<IReadOnlyList<Resume>>>, GetResumesByOwnerHandler>();
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

        // Organizations
        services.AddScoped<ICommandHandler<CreateOrganizationCommand, Result<Organization>>, CreateOrganizationHandler>();
        services.AddScoped<ICommandHandler<AddMemberCommand, Result<Organization>>, AddMemberHandler>();
        services.AddScoped<ICommandHandler<RemoveMemberCommand, Result<Organization>>, RemoveMemberHandler>();
        services.AddScoped<IQueryHandler<GetOrganizationQuery, Result<Organization>>, GetOrganizationHandler>();
        services.AddScoped<IQueryHandler<GetOrganizationBySlugQuery, Result<Organization>>, GetOrganizationBySlugHandler>();

        // Scoring
        services.AddScoped<ICommandHandler<ScoreResumeCommand, Result<Analysis>>, ScoreResumeHandler>();

        return services;
    }
}
