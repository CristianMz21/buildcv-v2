namespace BuildCv.Application.Common;

/// <summary>
/// One rejected field of a reviewed draft. <paramref name="Path"/> is the JSON path of the offending
/// value as the client sent it — <c>experiences[2].end</c>, <c>requirements[0].skill</c> — so a review
/// screen can highlight the input the candidate has to fix rather than the request as a whole.
/// </summary>
/// <remarks>
/// This is deliberately NOT expressed through <see cref="BuildCv.Domain.Common.ValueObjects.Result{T}"/>:
/// that type carries one string, the API routes on its text, and a draft has many fields across several
/// collections. "Invalid phone number." tells a review screen nothing about where to point.
/// <para>
/// It lives in <c>Common</c> rather than beside either draft because it is the shared currency of every
/// bulk-import use case in the Application layer — the resume draft and the job-offer draft both collect
/// their failures as a list of these, through <see cref="FieldErrorCollector"/>.
/// </para>
/// </remarks>
public sealed record FieldError(string Path, string Message);
