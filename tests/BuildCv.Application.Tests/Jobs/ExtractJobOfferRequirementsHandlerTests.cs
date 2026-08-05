using BuildCv.Application.Jobs;
using FluentAssertions;

namespace BuildCv.Application.Tests.Jobs;

public class ExtractJobOfferRequirementsHandlerTests
{
    private readonly ExtractJobOfferRequirementsHandler _handler = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Extract_BlankText_IsAFailure(string text)
    {
        var result = await _handler.Handle(new ExtractJobOfferRequirementsQuery(text));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Value is required.");
    }

    [Fact]
    public async Task Extract_TextNamingSkills_ProposesThem()
    {
        var result = await _handler.Handle(new ExtractJobOfferRequirementsQuery("We use C# and Docker."));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Select(p => p.Skill).Should().Equal("C#", "Docker");
    }

    // Text that binds fine but names nothing recognised is a legitimate EMPTY proposal, not an error --
    // the candidate then types their own requirements.
    [Fact]
    public async Task Extract_TextWithNoKnownSkill_IsAnEmptySuccess()
    {
        var result = await _handler.Handle(new ExtractJobOfferRequirementsQuery("A collaborative team player."));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }
}
