using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Common.ValueObjects;

public class PersonNameTests
{
    [Fact]
    public void PersonName_with_valid_value_can_be_created()
    {
        var name = PersonName.Create("Cristian Arellano");

        name.Value.Should().Be("Cristian Arellano");
    }

    [Fact]
    public void PersonName_is_trimmed()
    {
        var name = PersonName.Create("  Cristian Arellano  ");

        name.Value.Should().Be("Cristian Arellano");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void PersonName_with_empty_value_throws_exception(string value)
    {
        var act = () => PersonName.Create(value);

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void PersonName_exceeding_max_length_throws_invalid_person_name()
    {
        var longName = new string('A', 201);

        var act = () => PersonName.Create(longName);

        act.Should().Throw<InvalidPersonNameException>();
    }

    [Fact]
    public void PersonName_equality_works()
    {
        var name1 = PersonName.Create("Cristian Arellano");
        var name2 = PersonName.Create("Cristian Arellano");

        name1.Should().Be(name2);
    }

    [Fact]
    public void PersonName_implicit_conversion_to_string()
    {
        var name = PersonName.Create("Cristian Arellano");

        string value = name;

        value.Should().Be("Cristian Arellano");
    }

    [Fact]
    public void TryCreate_with_valid_name_returns_true()
    {
        var result = PersonName.TryCreate("Cristian Arellano", out var name);

        result.Should().BeTrue();
        name.Should().NotBeNull();
    }

    [Fact]
    public void TryCreate_with_invalid_name_returns_false()
    {
        var result = PersonName.TryCreate("", out var name);

        result.Should().BeFalse();
        name.Should().BeNull();
    }
}
