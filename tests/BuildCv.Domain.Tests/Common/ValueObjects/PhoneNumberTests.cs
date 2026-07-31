using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Common.ValueObjects;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("+573001234567")]
    [InlineData("+14155552671")]
    [InlineData("+447911123456")]
    public void PhoneNumber_with_valid_format_can_be_created(string value)
    {
        var phoneNumber = PhoneNumber.Create(value);

        phoneNumber.Value.Should().Be(value);
    }

    [Fact]
    public void PhoneNumber_strips_dashes_and_spaces()
    {
        var phoneNumber = PhoneNumber.Create("+57 300-123-4567");

        phoneNumber.Value.Should().Be("+573001234567");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void PhoneNumber_with_empty_value_throws_exception(string value)
    {
        var act = () => PhoneNumber.Create(value);

        act.Should().Throw<Exception>();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("123")]
    [InlineData("abc")]
    public void PhoneNumber_with_invalid_format_throws_invalid_phone_number(string value)
    {
        var act = () => PhoneNumber.Create(value);

        act.Should().Throw<InvalidPhoneNumberException>();
    }

    [Fact]
    public void PhoneNumber_equality_works()
    {
        var phone1 = PhoneNumber.Create("+573001234567");
        var phone2 = PhoneNumber.Create("+573001234567");

        phone1.Should().Be(phone2);
    }

    [Fact]
    public void PhoneNumber_implicit_conversion_to_string()
    {
        var phoneNumber = PhoneNumber.Create("+573001234567");

        string value = phoneNumber;

        value.Should().Be("+573001234567");
    }

    [Fact]
    public void TryCreate_with_valid_phone_returns_true()
    {
        var result = PhoneNumber.TryCreate("+573001234567", out var phoneNumber);

        result.Should().BeTrue();
        phoneNumber.Should().NotBeNull();
    }

    [Fact]
    public void TryCreate_with_invalid_phone_returns_false()
    {
        var result = PhoneNumber.TryCreate("invalid", out var phoneNumber);

        result.Should().BeFalse();
        phoneNumber.Should().BeNull();
    }
}
