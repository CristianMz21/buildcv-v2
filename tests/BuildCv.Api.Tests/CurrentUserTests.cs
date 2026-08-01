using System.Security.Claims;
using BuildCv.Api.Security;
using BuildCv.Application.Common.Services;
using BuildCv.Domain.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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

    // The registration itself, not the class. Infrastructure registers UnknownCurrentUser through
    // TryAddSingleton and Program.cs overrides it by registering afterwards — which means the guarantee
    // rests entirely on the ORDER of two lines in two different files. Move AddInfrastructure below the
    // override and every CreatedBy, UpdatedBy and DeletedBy column silently reverts to NULL with a fully
    // green suite. Nothing else in the codebase would notice; this does.
    [Fact]
    public void ICurrentUser_ResolvedFromTheHost_IsTheHttpContextBackedImplementation()
    {
        using var factory = new ApiTestFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICurrentUser>().Should().BeOfType<HttpContextCurrentUser>(
            "AddInfrastructure's UnknownCurrentUser fallback must lose to the Api's override");
    }

    private static IHttpContextAccessor Accessor(HttpContext httpContext) =>
        new HttpContextAccessor { HttpContext = httpContext };
}
