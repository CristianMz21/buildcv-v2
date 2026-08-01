using BuildCv.Application.Common.Services;
using BuildCv.Domain.Identity;

namespace BuildCv.Infrastructure.Persistence;

// The default: nobody. Registered with TryAdd semantics so the Api can supply an
// HttpContext-backed implementation without this one having to be removed first, and so the
// migration tooling and the test host get a working registration for free.
//
// Audit columns are therefore nullable everywhere. A row written by an anonymous request or by a
// background job honestly records "no principal" instead of a placeholder Guid that a later reader
// would have to know to distrust.
public sealed class UnknownCurrentUser : ICurrentUser
{
    public AccountId? AccountId => null;
}
