using BuildCv.Api.Security;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace BuildCv.Api.Tests;

// The diagnostic exists because enabling forwarded-header trust produces no observable signal: the peer
// address becomes a different value and nothing says whether it is the right one. These tests pin the
// two halves that make it usable — it says something when asked, and it says NOTHING when not asked —
// plus the property that makes it safe to ask, which is that its input is attacker-controlled and it
// treats it that way.
public sealed class ForwardedHeaderDiagnosticsTests
{
    private const string ForwardedFor = "X-Forwarded-For";

    // Not a valid hex digit, so it cannot appear in any address and its presence in a log line can only
    // mean the raw header reached one.
    private const string InjectionSentinel = "ZZINJECTEDZZ";

    [Fact]
    public void Sanitize_AnAbsentHeader_IsReportedAsAbsent() =>
        ForwardedHeaderDiagnostics.Sanitize(StringValues.Empty)
            .Should().Be(ForwardedHeaderDiagnostics.Absent);

    [Fact]
    public void Sanitize_AnEmptyValue_IsReportedAsAbsent() =>
        ForwardedHeaderDiagnostics.Sanitize(new StringValues(string.Empty))
            .Should().Be(ForwardedHeaderDiagnostics.Absent);

    // The whole point of the line is counting hops, and StringValues.ToString() joins with a comma —
    // which is also the chain's separator. Two headers of one hop each would render identically to one
    // header of two, so a repeat is refused rather than resolved into a number nobody sent.
    [Fact]
    public void Sanitize_ARepeatedHeader_IsRefusedRatherThanJoined()
    {
        var repeated = new StringValues(["1.2.3.4", "5.6.7.8"]);

        ForwardedHeaderDiagnostics.Sanitize(repeated)
            .Should().Be(ForwardedHeaderDiagnostics.Unsafe);

        // The control on that claim: joined, it would have been indistinguishable from a real two-hop
        // chain, which the very next assertion shows is accepted.
        repeated.ToString().Should().Be("1.2.3.4,5.6.7.8");
        ForwardedHeaderDiagnostics.Sanitize(new StringValues("1.2.3.4,5.6.7.8"))
            .Should().Be("1.2.3.4,5.6.7.8");
    }

    [Fact]
    public void Sanitize_AValueLongerThanTheLimit_IsRefused()
    {
        var overLong = new string('1', ForwardedHeaderDiagnostics.MaxLength + 1);

        ForwardedHeaderDiagnostics.Sanitize(new StringValues(overLong))
            .Should().Be(ForwardedHeaderDiagnostics.Unsafe);

        // Exactly at the limit is kept, so the refusal above is the length rule firing and not an
        // off-by-one that would refuse every real chain.
        var atLimit = new string('1', ForwardedHeaderDiagnostics.MaxLength);
        ForwardedHeaderDiagnostics.Sanitize(new StringValues(atLimit)).Should().Be(atLimit);
    }

    [Theory]
    [InlineData("1.2.3.4 evil=\"true\"")]      // quote and equals
    [InlineData("1.2.3.4; DROP")]              // semicolon
    [InlineData("1.2.3.4\tinjected")]          // tab
    [InlineData("{\"level\":\"Error\"}")]      // a forged structured line
    [InlineData("192.168.0.1 zzz")]            // a letter outside the hex range
    [InlineData("%2e%2e%2f")]                  // percent-encoding
    public void Sanitize_AValueHoldingAnythingButAnAddressList_IsReplacedWhole(string hostile) =>
        ForwardedHeaderDiagnostics.Sanitize(new StringValues(hostile))
            .Should().Be(ForwardedHeaderDiagnostics.Unsafe);

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("203.0.113.7, 100.100.0.103")]
    [InlineData("203.0.113.7,100.100.0.103,10.0.0.1")]
    [InlineData("[2001:db8::1]:4711")]
    [InlineData("2001:db8::1, 203.0.113.7")]
    public void Sanitize_ARealChain_IsKeptVerbatim(string chain) =>
        ForwardedHeaderDiagnostics.Sanitize(new StringValues(chain)).Should().Be(chain);

    // THE NEGATIVE CONTROL for the test below it. An address is personal data and a log line carries
    // none of this repository's encryption, so "off unless asked" is a property and not a default that
    // happens to hold: at the shipped Information level the middleware must write nothing at all.
    [Fact]
    public async Task AtTheDefaultLevel_TheDiagnosticWritesNothing()
    {
        var recorder = new RecordingLoggerProvider();
        using var factory = new ApiTestFactory(configureServices: RecordingOnly(recorder, LogLevel.Information));
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.TryAddWithoutValidation(ForwardedFor, "203.0.113.7");
        (await client.GetAsync(new Uri("/health/live", UriKind.Relative))).EnsureSuccessStatusCode();

        DiagnosticLines(recorder).Should().BeEmpty();
    }

    [Fact]
    public async Task AtDebug_TheLineNamesThePeerAndTheChainThatWasNotConsumed()
    {
        var recorder = new RecordingLoggerProvider();
        using var factory = new ApiTestFactory(configureServices: RecordingLogging.Capturing(recorder));
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.TryAddWithoutValidation(ForwardedFor, "203.0.113.7, 100.100.0.103");
        (await client.GetAsync(new Uri("/health/live", UriKind.Relative))).EnsureSuccessStatusCode();

        var line = DiagnosticLines(recorder).Should().ContainSingle().Subject;

        // Trust is off in this host, so the chain arrives unconsumed and in full — which is precisely
        // the diagnosis an operator needs: the header was sent, and it was ignored.
        line.Message.Should().Contain("203.0.113.7, 100.100.0.103");
        line.Level.Should().Be(LogLevel.Debug);
    }

    // The line's input is client-supplied by definition, and it is the only log line here designed to
    // carry outside text. Asserted the way ObservabilityLeakTests asserts: over EVERY string the log
    // call carried — message, structured state and enclosing scopes — because a value that never
    // reaches the rendered message can still reach a structured sink.
    [Fact]
    public async Task AHostileHeader_ReachesNoPartOfALogLine()
    {
        var recorder = new RecordingLoggerProvider();
        using var factory = new ApiTestFactory(configureServices: RecordingLogging.Capturing(recorder));
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.TryAddWithoutValidation(ForwardedFor, $"1.2.3.4 {InjectionSentinel}");
        (await client.GetAsync(new Uri("/health/live", UriKind.Relative))).EnsureSuccessStatusCode();

        // The diagnostic ran and refused the value, so the absence below is a refusal rather than a
        // request that never reached the middleware — the failure mode where a control proves nothing.
        var line = DiagnosticLines(recorder).Should().ContainSingle().Subject;
        line.Message.Should().Contain(ForwardedHeaderDiagnostics.Unsafe);

        recorder.AllText.Should().NotContain(text => text.Contains(InjectionSentinel, StringComparison.Ordinal));
    }

    private static IReadOnlyList<RecordedLog> DiagnosticLines(RecordingLoggerProvider recorder) =>
        [.. recorder.Records.Where(record =>
            record.Category == typeof(ForwardedHeaderDiagnostics).FullName)];

    // Deliberately NOT RecordingLogging.Capturing: that clears the filter rules to open everything to
    // Trace, which is the opposite of what the default-level test is asking about. This registers the
    // recorder and pins the minimum explicitly, so the assertion does not depend on appsettings.json
    // continuing to say Information.
    private static Action<IServiceCollection> RecordingOnly(
        RecordingLoggerProvider recorder,
        LogLevel minimum) =>
        services =>
        {
            services.AddLogging(logging => logging.Services.AddSingleton<ILoggerProvider>(recorder));
            services.Configure<LoggerFilterOptions>(options =>
            {
                options.Rules.Clear();
                options.MinLevel = minimum;
            });
        };
}
