namespace BuildCv.Api.Security;

public static class AuthorizationPolicies
{
    public const string Candidate = "Candidate";
    public const string Recruiter = "Recruiter";
    public const string Admin = "Admin";
}

public static class AuthenticationSchemes
{
    /// <summary>
    /// Second JWT scheme, used by <c>POST /auth/logout</c> and nowhere else. Identical to the
    /// default scheme except that it does not check <c>exp</c>.
    /// </summary>
    /// <remarks>
    /// The access-token cookie is the only thing that says who is logging out — the refresh cookie
    /// is path-scoped to <c>/auth/refresh</c> and never reaches this route. An idle user's token is
    /// expired by the time they press the button, so validating lifetime here would reduce logout
    /// to "clear cookies and revoke nothing", which is the vulnerability this endpoint exists to
    /// close. Signature, issuer and audience are still validated, so the caller still has to
    /// present a token this API actually issued. The accepted cost: a stolen expired access token
    /// becomes a "log the victim out everywhere" capability — annoying, not escalating, and
    /// strictly less than what a stolen live token already grants.
    /// </remarks>
    public const string ExpiredAccessTokenAllowed = "ExpiredAccessTokenAllowed";
}

public static class RateLimitPolicies
{
    public const string Auth = "auth";

    /// <summary>
    /// Separate window for <c>POST /auth/logout</c>. It is anonymous and state-changing, so it
    /// needs a ceiling, but it tests no secret and so does not belong in the brute-force window —
    /// and putting a UI button on the 5/min login budget would starve everyone behind a NAT.
    /// </summary>
    public const string Logout = "logout";
}

public static class CorsPolicies
{
    public const string Strict = "Strict";
}
