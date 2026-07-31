using BuildCv.Domain.Identity;

namespace BuildCv.Application.Common.Services;

// Who the request is acting as, for the audit columns Infrastructure stamps on every write.
//
// Nullable on purpose. Registration, login and the design-time migration tooling all write rows with
// no authenticated principal, and a port that could not express "nobody" would push every one of
// those callers into inventing a sentinel account id.
public interface ICurrentUser
{
    AccountId? AccountId { get; }
}
