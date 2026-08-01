using BuildCv.Application.Common.Services;
using BuildCv.Domain.Identity;

namespace BuildCv.Api.Security;

// Who is writing. Resolves the acting principal from the current request so the audit interceptor can
// stamp CreatedBy, UpdatedBy and DeletedBy with a real account id.
//
// Infrastructure registers UnknownCurrentUser as a fallback and the Api replaces it here. Until it did,
// every audit column on every row was NULL and nothing failed — an audit trail that records only that
// somebody did something is not an audit trail, and its absence is invisible from the inside.
//
// GetAccountIdOrNull rather than GetAccountId: an anonymous request writing a row is legitimate —
// registration is exactly that — and it should honestly record "no principal" instead of throwing on the
// way to a database write.
public sealed class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public AccountId? AccountId => httpContextAccessor.HttpContext?.User.GetAccountIdOrNull();
}
