using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace BuildCv.Api.Tests;

// THE REQUEST THIS CHANGE EXISTS FOR, over the real pipeline: a CV that says "June 2015 - February 2019"
// goes up, comes back as a draft carrying 2015-06 and 2019-02, is imported unedited, and is read back
// and scored with those dates intact.
//
// It is worth having at this layer and not only in the parser tests because the precision crosses four
// boundaries here — parser to draft, draft to JSON, JSON to validator, aggregate to column and back —
// and the failure at any of them looks identical from inside one layer: a date that widened to a day
// nobody wrote, or a field that came back blank the way it used to.
public sealed class DatePrecisionTests
{
    [Fact]
    public async Task AMonthAndYearCv_ProposesDatesAtMonthPrecisionInsteadOfFlaggingThemAsUnextracted()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await ProposeAsync(client, token, TextCv);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var experience = body.RootElement.GetProperty("draft").GetProperty("experiences")[0];
        experience.GetProperty("start").GetString().Should().Be("2015-06");
        experience.GetProperty("end").GetString().Should().Be("2019-02");

        // The other half of the claim, and the half a candidate feels: the field is no longer flagged
        // for them to complete by hand. Both are asserted, because a draft carrying the value while
        // still shouting NotExtracted would send them to the same keyboard.
        ConfidenceOf(body, "experiences[0].start").Should().NotBe("NotExtracted");
        ConfidenceOf(body, "experiences[0].end").Should().NotBe("NotExtracted");
    }

    [Fact]
    public async Task AMonthAndYearCv_ImportedUnedited_ReadsBackWithThoseDates()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await ImportAsync(client, token, Draft(start: "2015-06", end: "2019-02"));

        using var resume = JsonDocument.Parse(
            await (await GetAsync(client, token, $"/v1/resumes/{resumeId}")).Content.ReadAsStringAsync());

        var period = resume.RootElement.GetProperty("experiences")[0].GetProperty("period");
        period.GetProperty("start").GetString().Should().Be("2015-06",
            "a month is what the CV stated, so a month is what comes back — not 2015-06-01");
        period.GetProperty("end").GetString().Should().Be("2019-02");
    }

    // THE PAYOFF, MEASURED OVER THE WIRE. Under the convention the range runs 2015-06-01 to 2019-02-28,
    // which is 1368 days against ExperienceScore's five-year denominator. The assertion is that exact
    // fraction rather than "greater than zero", because the number is what the change is worth: the same
    // CV used to import with two blank dates and score this section at nothing at all.
    [Fact]
    public async Task AMonthAndYearCv_ScoresItsExperienceInsteadOfContributingNothing()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, candidateToken) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);
        var (_, recruiterToken) = await client.RegisterAndLoginAsync(TestHelpers.RecruiterEmail, role: "Recruiter");

        var resumeId = await ImportAsync(client, candidateToken, Draft(start: "2015-06", end: "2019-02"));
        var jobId = await ScoringEndpointTests.CreateJobAsync(client, recruiterToken);
        await ScoringEndpointTests.PublishAsync(client, recruiterToken, jobId);

        var response = await ScoringEndpointTests.ScoreAsync(client, candidateToken, resumeId, jobId);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var analysis = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        analysis.RootElement.GetProperty("breakdown").GetProperty("experienceScore").GetDouble()
            .Should().BeApproximately(1368.0 / (365.0 * 5), 1e-9);
    }

    // THE NON-REGRESSION A CLIENT WILL NOTICE FIRST. A date the candidate typed in full must survive the
    // round trip in full: the wire format did not become "month precision for everybody".
    [Fact]
    public async Task ADateTypedInFull_IsNeverWidenedByTheRoundTrip()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var resumeId = await ImportAsync(client, token, Draft(start: "2015-06-15", end: "2019-02-20"));

        using var resume = JsonDocument.Parse(
            await (await GetAsync(client, token, $"/v1/resumes/{resumeId}")).Content.ReadAsStringAsync());

        var period = resume.RootElement.GetProperty("experiences")[0].GetProperty("period");
        period.GetProperty("start").GetString().Should().Be("2015-06-15");
        period.GetProperty("end").GetString().Should().Be("2019-02-20");
    }

    // A date is still a date. The importer accepts three widths and nothing else, and a malformed one is
    // still a field error keyed to the field rather than a framework 400 naming nothing.
    [Fact]
    public async Task ADateThatIsNoneOfTheThreeWidths_IsStillAFieldErrorAtItsOwnPath()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var (_, token) = await client.RegisterAndLoginAsync(TestHelpers.CandidateEmail);

        var response = await PostImportAsync(client, token, Draft(start: "June 2015", end: "2019-02"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        problem.RootElement.GetProperty("errors").GetProperty("experiences[0].start")[0].GetString()
            .Should().Be("Invalid date. Expected yyyy-MM-dd, yyyy-MM or yyyy.");
    }

    // ---------------------------------------------------------------- fixtures

    private const string TextCv =
        """
        Priya Nair
        priya.nair@example.com

        EXPERIENCE
        Staff Engineer
        Shopify
        June 2015 - February 2019
        """;

    private static string Draft(string start, string end) =>
        $$"""
        {
          "contact": {
            "fullName": "Priya Nair",
            "email": "priya.nair@example.com"
          },
          "experiences": [
            {
              "type": "Professional",
              "organization": "Shopify",
              "position": "Staff Engineer",
              "start": {{JsonSerializer.Serialize(start)}},
              "end": {{JsonSerializer.Serialize(end)}}
            }
          ]
        }
        """;

    private static string? ConfidenceOf(JsonDocument body, string path) =>
        body.RootElement.GetProperty("confidence").GetProperty("fields").EnumerateArray()
            .Where(field => field.GetProperty("path").GetString() == path)
            .Select(field => field.GetProperty("confidence").GetString())
            .FirstOrDefault();

    private static async Task<Guid> ImportAsync(HttpClient client, string token, string draftJson)
    {
        var response = await PostImportAsync(client, token, draftJson);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return created.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> PostImportAsync(
        HttpClient client, string token, string draftJson)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import")
        {
            Content = new StringContent(draftJson, Encoding.UTF8, "application/json"),
        }.WithBearer(token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> ProposeAsync(HttpClient client, string token, string text)
    {
        var filePart = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        filePart.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/resumes/import/propose")
        {
            Content = new MultipartFormDataContent { { filePart, "file", "cv.txt" } },
        }.WithBearer(token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route).WithBearer(token);
        return await client.SendAsync(request);
    }
}
