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

public sealed class InvalidLanguageException : DomainException
{
    public InvalidLanguageException(string message) : base(message) { }
}

public sealed class InvalidResumeNameException : DomainException
{
    public InvalidResumeNameException(string message) : base(message) { }
}

public sealed class InvalidAccountException : DomainException
{
    public InvalidAccountException(string message) : base(message) { }
}

/// <summary>
/// A proposed password that does not meet <see cref="Identity.PasswordPolicy"/>.
/// </summary>
/// <remarks>
/// Its own type rather than <see cref="InvalidAccountException"/> because the two say different
/// things to a caller: one means "this account is malformed", the other means "choose another
/// password and try again". Every message it carries names the limit and never the value — it
/// reaches a 400 body and the application log, and the value is a credential.
/// </remarks>
public sealed class WeakPasswordException : DomainException
{
    public WeakPasswordException(string message) : base(message) { }
}

public sealed class InvalidMembershipException : DomainException
{
    public InvalidMembershipException(string message) : base(message) { }
}

public sealed class InvalidSlugException : DomainException
{
    public InvalidSlugException(string message) : base(message) { }
}

public sealed class InvalidPartialDateException : DomainException
{
    public InvalidPartialDateException(string message) : base(message) { }
}

public sealed class InvalidDateRangeException : DomainException
{
    public InvalidDateRangeException(string message) : base(message) { }
}

public sealed class InvalidJobPostingException : DomainException
{
    public InvalidJobPostingException(string message) : base(message) { }
}

public sealed class InvalidRecommendationException : DomainException
{
    public InvalidRecommendationException(string message) : base(message) { }
}

public sealed class DuplicateSkillException : DomainException
{
    public DuplicateSkillException(string message) : base(message) { }
}

public sealed class DuplicateEntryException : DomainException
{
    public DuplicateEntryException(string message) : base(message) { }
}

public sealed class EntryNotFoundException : DomainException
{
    public EntryNotFoundException(string message) : base(message) { }
}
