using BuildCv.Infrastructure.Persistence.EfCore;
using FluentAssertions;

namespace BuildCv.Infrastructure.Tests.Persistence.EfCore;

// Split across two classes because the two halves need different things: proving the probe says NO
// needs a server that is not there, which needs nothing at all, while proving it says YES needs a real
// one. Only the second is an integration test.
public sealed class EfCorePersistenceProbeUnreachableTests
{
    // Port 1 on the loopback address. Nothing listens there, so the connection is REFUSED rather than
    // timing out, which is what keeps this a fast unit test instead of a five-second one. Connect
    // Timeout is set anyway so a host that black-holes the packet still answers inside a second.
    private const string UnreachableConnectionString =
        "Server=127.0.0.1,1;Database=BuildCv;User Id=sa;Password=unused;"
        + "TrustServerCertificate=True;Connect Timeout=1";

    // The half that matters most, because it is the one production hits during an incident. It also
    // pins the shape of the answer: FALSE, not an exception. A probe that threw would reach
    // DatabaseHealthCheck, which does not catch, and the readiness endpoint would answer 500 — a status
    // an orchestrator reads as "broken", not as "not ready yet", and one this API's own exception
    // handler would dress in a ProblemDetails body no probe can parse.
    [Fact]
    public async Task CanReachAsync_AgainstAServerThatIsNotThere_AnswersFalseRatherThanThrowing()
    {
        await using var context = PersistenceTestContext.Create(
            UnreachableConnectionString, TimeProvider.System);
        var probe = new EfCorePersistenceProbe(context);

        var reachable = await probe.CanReachAsync();

        reachable.Should().BeFalse();
    }
}

[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EfCorePersistenceProbeTests
{
    private readonly SqlServerFixture _fixture;

    public EfCorePersistenceProbeTests(SqlServerFixture fixture) => _fixture = fixture;

    // Without this the false above is worthless: a probe hard-coded to return false would pass that
    // test and report every healthy instance as not ready, which takes the whole deployment out of
    // rotation. The pair is the assertion.
    [Fact]
    public async Task CanReachAsync_AgainstTheRealServer_AnswersTrue()
    {
        await using var context = _fixture.NewApplicationContext();
        var probe = new EfCorePersistenceProbe(context);

        var reachable = await probe.CanReachAsync();

        reachable.Should().BeTrue();
    }
}
