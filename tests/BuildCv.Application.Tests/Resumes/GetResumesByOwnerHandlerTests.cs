namespace BuildCv.Application.Tests.Resumes;

using BuildCv.Application.Common.Pagination;
using BuildCv.Application.Resumes;
using BuildCv.Application.Tests.Fakes;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;
using FluentAssertions;

public sealed class GetResumesByOwnerHandlerTests
{
    private readonly FakeResumeRepository _resumes = new();
    private readonly FakeAccountRepository _accounts = new();
    private readonly GetResumesByOwnerHandler _handler;

    public GetResumesByOwnerHandlerTests() => _handler = new GetResumesByOwnerHandler(_resumes, _accounts);

    [Fact]
    public async Task Handle_WithNoPagingArguments_ReturnsTheOwnersResumesNewestFirst()
    {
        var owner = AccountId.New();
        var first = await Seed(owner);
        var second = await Seed(owner);
        await Seed(AccountId.New());

        var result = await _handler.Handle(new GetResumesByOwnerQuery(owner, owner));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Select(resume => resume.Id).Should().Equal(second.Id, first.Id);
        result.Value.NextCursor.Should().BeNull();
    }

    // Three resumes at two per page, walked the way a client walks it. Another owner's resume sits in
    // the store the whole time and must never appear, on any page.
    [Fact]
    public async Task Handle_WalkedByCursor_ReturnsEachOfTheOwnersResumesExactlyOnce()
    {
        var owner = AccountId.New();
        var first = await Seed(owner);
        var second = await Seed(owner);
        var third = await Seed(owner);
        var somebodyElses = await Seed(AccountId.New());

        var firstPage = await Page(owner, limit: 2);
        firstPage.Items.Select(resume => resume.Id).Should().Equal(third.Id, second.Id);
        firstPage.NextCursor.Should().NotBeNull();

        var secondPage = await Page(owner, limit: 2, cursor: firstPage.NextCursor);
        secondPage.Items.Select(resume => resume.Id).Should().Equal(first.Id);
        secondPage.NextCursor.Should().BeNull("three resumes at two per page end on the second");

        firstPage.Items.Concat(secondPage.Items).Should().NotContain(resume => resume.Id == somebodyElses.Id);
    }

    [Fact]
    public async Task Handle_WithAnAbsurdLimit_ClampsItInsteadOfFailing()
    {
        var owner = AccountId.New();
        await Seed(owner);

        (await Page(owner, limit: 0)).Items.Should().ContainSingle();
        (await Page(owner, limit: 100_000)).Items.Should().ContainSingle();
    }

    // The failure that must never be a full scan and must never be an exception: a cursor the caller
    // made up comes back as a Result failure, which the Api turns into a 400. Returning the first page
    // instead would silently restart the client's walk and look like the data had changed underneath it.
    [Theory]
    [InlineData("nonsense")]
    [InlineData("AAAAAAAAAAA")]
    [InlineData("%00%00")]
    public async Task Handle_WithACursorItCannotDecode_FailsWithoutTouchingTheRepository(string cursor)
    {
        var owner = AccountId.New();
        await Seed(owner);

        var result = await _handler.Handle(new GetResumesByOwnerQuery(owner, owner, 10, cursor));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PageRequest.InvalidCursorError);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ForSomebodyElsesResumes_IsForbidden()
    {
        var owner = AccountId.New();
        await Seed(owner);

        var result = await _handler.Handle(new GetResumesByOwnerQuery(AccountId.New(), owner));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Forbidden.");
    }

    // Authorization is decided before the cursor is looked at, so a stranger cannot use the difference
    // between "Forbidden." and "Invalid cursor." to learn anything about the request it just sent.
    [Fact]
    public async Task Handle_ForSomebodyElsesResumesWithAMalformedCursor_StillAnswersForbidden()
    {
        var owner = AccountId.New();
        await Seed(owner);

        var result = await _handler.Handle(new GetResumesByOwnerQuery(AccountId.New(), owner, 10, "nonsense"));

        result.Error.Should().Be("Forbidden.");
    }

    private async Task<Page<Resume>> Page(AccountId owner, int? limit = null, string? cursor = null)
    {
        var result = await _handler.Handle(new GetResumesByOwnerQuery(owner, owner, limit, cursor));
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value!;
    }

    private async Task<Resume> Seed(AccountId owner)
    {
        var resume = Resume.Create(owner, new ContactInformation(
            PersonName.Create("Jane Doe"), Email.Create($"{Guid.NewGuid():N}@example.com")));
        await _resumes.AddAsync(resume);
        return resume;
    }
}
