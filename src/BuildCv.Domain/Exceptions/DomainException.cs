namespace BuildCv.Domain.Exceptions;

public class DomainException : Exception
{
    protected DomainException(string message) : base(message) { }
}

public sealed class InvalidEmailException : DomainException
{
    public InvalidEmailException(string message) : base(message) { }
}

public sealed class InvalidPhoneNumberException : DomainException
{
    public InvalidPhoneNumberException(string message) : base(message) { }
}

public sealed class InvalidUrlException : DomainException
{
    public InvalidUrlException(string message) : base(message) { }
}

public sealed class InvalidPersonNameException : DomainException
{
    public InvalidPersonNameException(string message) : base(message) { }
}
