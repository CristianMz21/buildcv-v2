namespace BuildCv.Domain.Common.ValueObjects;

public sealed record Result<T>
{
    public T? Value { get; }
    public string? Error { get; }
    public bool IsSuccess => Error is null;

    private Result(T value)
    {
        Value = value;
        Error = null;
    }

    private Result(string error)
    {
        Value = default;
        Error = error;
    }

    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(string error) => new(error);

    public Result<TOut> Map<TOut>(Func<T, TOut> map) =>
        IsSuccess ? Result<TOut>.Success(map(Value!)) : Result<TOut>.Failure(Error!);

    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> bind) =>
        IsSuccess ? bind(Value!) : Result<TOut>.Failure(Error!);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<string, TOut> onFailure) =>
        IsSuccess ? onSuccess(Value!) : onFailure(Error!);
}

public sealed record Result
{
    public string? Error { get; }
    public bool IsSuccess => Error is null;

    private Result() => Error = null;

    private Result(string error) => Error = error;

    public static Result Success() => new();
    public static Result Failure(string error) => new(error);
}
