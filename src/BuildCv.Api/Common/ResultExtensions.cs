using BuildCv.Application.Common;
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
    /// Grouped by path rather than built with <c>ToDictionary</c> directly, and NOT because a duplicate
    /// path occurs today — every helper in <c>ResumeDraftValidator</c> records at most one error per
    /// field and returns, so the groups are all single-element. It is grouped because
    /// <c>ToDictionary</c> THROWS on a duplicate key: the day a rule reports twice at one path, the
    /// difference between these two lines is a correct 400 and a 500. The ProblemDetails value is an
    /// array either way, so nothing about the response shape depends on which is used.
    /// </para>
    /// <para>
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
