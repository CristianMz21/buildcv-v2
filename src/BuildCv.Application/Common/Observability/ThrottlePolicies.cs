namespace BuildCv.Application.Common.Observability;

/// <summary>
/// Every value the <c>policy</c> tag on <c>buildcv.throttle.rejections</c> may take: one per limiter
/// this API runs, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The names mirror the limiters rather than the routes, because a limiter is what a 429 is evidence
/// about. Two are named ASP.NET Core policies, one is the global limiter that has no policy name at
/// all, and three are the per-account limiters acquired inside their endpoints (<c>UseRateLimiter</c>
/// runs before <c>UseAuthentication</c>, so a policy partitioner has no principal to key on — see
/// <c>PasswordChangeRateLimiter</c>).
/// </para>
/// <para>
/// DELIBERATELY NOT A DIMENSION HERE: the partition key. That is the client address for the per-IP
/// limiters and the ACCOUNT ID for the per-account ones, and either would give this counter one series
/// per client — unbounded cardinality, and in the second case an account id sitting unencrypted in a
/// metrics backend. "Which limiter is firing" is the operational question; "who is being throttled" is
/// a forensic one, and <c>AuditLog</c> already answers it in the place built for it.
/// </para>
/// </remarks>
public static class ThrottlePolicies
{
    /// <summary>The 5/min per-IP window on register, login and refresh.</summary>
    public const string Auth = "auth";

    /// <summary>The 20/min per-IP window on logout.</summary>
    public const string Logout = "logout";

    /// <summary>The 100/min per-IP limiter that applies to every non-exempt route.</summary>
    public const string Global = "global";

    /// <summary>Per-account, on <c>POST /resumes/import</c>.</summary>
    public const string ResumeImport = "resume_import";

    /// <summary>Per-account, shared by <c>/resumes/import/extract</c> and <c>/resumes/import/propose</c>.</summary>
    public const string DocumentExtraction = "document_extraction";

    /// <summary>Per-account, on <c>POST /auth/change-password</c>.</summary>
    public const string PasswordChange = "password_change";

    public static IReadOnlyList<string> All { get; } =
        [Auth, Logout, Global, ResumeImport, DocumentExtraction, PasswordChange];
}
