using BuildCv.Application.Resumes;
using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Api.Common;

public static class ResultExtensions
{
    /// <summary>
    /// Renders per-field draft failures as the ProblemDetails shape ASP.NET's own model validation
    /// emits — status 400, <c>application/problem+json</c>, and an <c>errors</c> object keyed by field
    /// path.
    /// </summary>
    /// <remarks>
    /// Deliberately the SAME shape as <c>Results.ValidationProblem</c> rather than a BuildCv-specific
    /// envelope: a client that already understands ASP.NET validation errors needs no new convention,
    /// and every other error this API answers is ProblemDetails-shaped too.
    /// <para>
    /// Grouped by path because one input can fail twice — a certificate whose start date is both blank
    /// and needed by its end date, say — and the dictionary value is an array for exactly that reason.
    /// Ordinal comparison keeps <c>experiences[1]</c> and <c>Experiences[1]</c> distinct, which they
    /// are: the key is the JSON path the client sent, not a C# member name.
    /// </para>
    /// </remarks>
    public static IResult ToValidationProblem(this IReadOnlyList<FieldError> fieldErrors) =>
        Results.ValidationProblem(fieldErrors
            .GroupBy(error => error.Path, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Message).ToArray(),
                StringComparer.Ordinal));

    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult>? onSuccess = null)
    {
        if (result.IsSuccess)
            return onSuccess is null ? Results.Ok(result.Value) : onSuccess(result.Value!);

        var message = result.Error!;
        var statusCode = message switch
        {
            "Forbidden." => StatusCodes.Status403Forbidden,
            var m when m.EndsWith("not found.", StringComparison.Ordinal) => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Problem(detail: message, statusCode: statusCode);
    }
}
