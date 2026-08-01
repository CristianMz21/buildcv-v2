namespace BuildCv.Api.Security;

public static class AuthorizationPolicies
{
    public const string Candidate = "Candidate";
    public const string Recruiter = "Recruiter";
    public const string Admin = "Admin";
}

public static class RateLimitPolicies
{
    public const string Auth = "auth";
}

public static class CorsPolicies
{
    public const string Strict = "Strict";
}
