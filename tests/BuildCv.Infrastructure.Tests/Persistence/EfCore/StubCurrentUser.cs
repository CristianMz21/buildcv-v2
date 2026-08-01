using BuildCv.Application.Common.Services;
using BuildCv.Domain.Identity;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

// A known principal, so the audit columns can be read back and compared against something. The Api's
// HttpContextCurrentUser is the production equivalent; the columns are shadow state, so nothing in the
// Domain would reveal it silently writing NULL.
internal sealed class StubCurrentUser(AccountId accountId) : ICurrentUser
{
    public AccountId? AccountId { get; } = accountId;
}
