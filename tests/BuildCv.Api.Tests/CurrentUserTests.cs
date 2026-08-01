using System.Security.Claims;
using BuildCv.Api.Security;
using BuildCv.Domain.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace BuildCv.Api.Tests;

// The audit columns are shadow state, so a broken ICurrentUser writes NULL into CreatedBy, UpdatedBy and
// DeletedBy and absolutely nothing else changes. That is how the Api went this long registering
// UnknownCurrentUser and never overriding it — an audit trail that only records that somebody did
// something is not one, and its absence is invisible from the inside.
public class CurrentUserTests
{
    [Fact]
    public void AccountId_ForAnAuthenticatedPrincipal_IsTheSubjectClaim()
    {
        var accountId = AccountId.New();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", accountId.Value.ToString())], "Test"))
        };

        var currentUser = new HttpContextCurrentUser(Accessor(httpContext));

        currentUser.AccountId.Should().Be(accountId);
    }

    // Anonymous writes are legitimate — registration is one — and must record "no principal" rather than
    // throw on the way to a database write.
    [Fact]
    public void AccountId_ForAnAnonymousRequest_IsNull()
    {
        new HttpContextCurrentUser(Accessor(new DefaultHttpContext())).AccountId.Should().BeNull();
    }

    [Fact]
    public void AccountId_ForAMalformedSubjectClaim_IsNull()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "not-a-guid")], "Test"))
        };

        new HttpContextCurrentUser(Accessor(httpContext)).AccountId.Should().BeNull();
    }

    // Background work and the auto-migrate scope run with no request at all.
    [Fact]
    public void AccountId_OutsideAnyRequest_IsNull()
    {
        new HttpContextCurrentUser(new HttpContextAccessor()).AccountId.Should().BeNull();
    }

    private static IHttpContextAccessor Accessor(HttpContext httpContext) =>
        new HttpContextAccessor { HttpContext = httpContext };
}
