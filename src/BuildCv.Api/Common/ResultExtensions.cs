using BuildCv.Domain.Common.ValueObjects;

namespace BuildCv.Api.Common;

public static class ResultExtensions
{
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
