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

/// <summary>
/// A strongly-typed identifier was handed <see cref="System.Guid.Empty"/>.
/// </summary>
/// <remarks>
/// <para>
/// The six id types threw a bare <c>ArgumentException</c> until this existed, and the two consequences
/// were both wrong. It matched no <c>IExceptionHandler</c> branch, so every by-id route answered
/// <b>500</b> to a well-formed request — measured on <c>/v1/scoring</c>, <c>/v1/readability</c>,
/// <c>/v1/resumes</c>, <c>/v1/jobs</c> and <c>/v1/organizations</c>, and on the <c>resumeId</c> field of
/// <c>POST /v1/scoring/score</c>, which is the same defect arriving in a request BODY. And
/// <c>ArgumentException.Message</c> appends <c>(Parameter 'value')</c>, so a <b>C# parameter name</b>
/// reached the response detail and the error log — the defect <c>ResumeDraftValidator</c> already fixed
/// once for factory messages, whose comment argues that a parameter name "is not something to put on a
/// review screen".
/// </para>
/// <para>
/// It is a <see cref="DomainException"/> because "an id names something" is an invariant, which puts it
/// in the tier this repository already routes: <c>DomainExceptionHandler</c> answers <b>400</b>, and the
/// Application handlers that build ids internally catch it into a <c>Result</c> through their FIRST
/// catch arm rather than their <c>ArgumentException</c> one.
/// </para>
/// <para>
/// <b>400 rather than 404</b>, and the reason is that the body case exists. A route constraint would fix
/// every <c>{x:guid}</c> route in one line and leave <c>POST /v1/scoring/score</c> answering 500 — and
/// its refusal is a routing miss, which this API answers with an empty body and no content type, adding
/// an unshaped response. There is also nothing for a 404 to conceal: no row can carry an empty id, so
/// "not found" would hide only from the caller that they sent an id no resource can ever have.
/// </para>
/// </remarks>
public sealed class EmptyIdentifierException : DomainException
{
    public EmptyIdentifierException(string message) : base(message) { }
}
