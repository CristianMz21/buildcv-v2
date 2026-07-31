using System.Security.Cryptography;
using System.Text;
using BuildCv.Domain.Identity;

namespace BuildCv.Api.Security;

public static class AuditLog
{
    public static void Log(ILogger logger, string eventName, AccountId? accountId, HttpContext context, string? email = null)
    {
        // Emails are hashed (first 8 hex chars of SHA-256) for correlation without storing PII.
        var emailHash = email is null
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email)))[..8];

        logger.LogInformation(
            "Auth event {Event} account {AccountId} ip {Ip} emailHash {EmailHash} at {Timestamp}",
            eventName,
            accountId?.Value,
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            emailHash,
            DateTimeOffset.UtcNow);
    }
}
