using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Common;

public class TechnologyTests
{
    [Fact]
    public void Technology_normalizes_whitespace()
    {
        var tech = Technology.Create("  C#  ");

        tech.Name.Should().Be("C#");
    }

    [Fact]
    public void Technology_preserves_case_for_display()
    {
        var tech = Technology.Create("C#");

        tech.Name.Should().Be("C#");
        tech.ToString().Should().Be("C#");
    }

    [Fact]
    public void Technology_rejects_too_long_name()
    {
        var act = () => Technology.Create(new string('a', 101));

        act.Should().Throw<InvalidTechnologyException>();
    }

    [Fact]
    public void Technology_rejects_control_characters()
    {
        var act = () => Technology.Create("bad\u0001name");

        act.Should().Throw<InvalidTechnologyException>();
    }

    [Fact]
    public void Technology_rejects_empty()
    {
        var act = () => Technology.Create("   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryCreate_with_invalid_returns_false()
    {
        var result = Technology.TryCreate("", out var tech);

        result.Should().BeFalse();
        tech.Should().BeNull();
    }

    [Fact]
    public void Technology_equality_works()
    {
        var t1 = Technology.Create("C#");
        var t2 = Technology.Create("C#");

        t1.Should().Be(t2);
    }
}
