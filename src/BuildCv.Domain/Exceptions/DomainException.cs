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

public sealed class InvalidOrganizationNameException : DomainException
{
    public InvalidOrganizationNameException(string message) : base(message) { }
}

public sealed class InvalidTechnologyException : DomainException
{
    public InvalidTechnologyException(string message) : base(message) { }
}

public sealed class InvalidAccountException : DomainException
{
    public InvalidAccountException(string message) : base(message) { }
}

public sealed class InvalidOrganizationException : DomainException
{
    public InvalidOrganizationException(string message) : base(message) { }
}

public sealed class InvalidMembershipException : DomainException
{
    public InvalidMembershipException(string message) : base(message) { }
}

public sealed class InvalidSlugException : DomainException
{
    public InvalidSlugException(string message) : base(message) { }
}
