using CedarRecon.Domain.Errors;

namespace CedarRecon.Domain.Common;

/// <summary>
/// Result monad for domain operations that can fail without throwing.
/// Use this instead of exceptions in domain/application layers.
/// </summary>
public readonly record struct Result<T>
{
    public T? Value { get; }
    public DomainError? Error { get; }
    public bool IsSuccess => Error is null;
    public bool IsFailure => Error is not null;

    private Result(T value)
    {
        Value = value;
        Error = null;
    }

    private Result(DomainError error)
    {
        Value = default;
        Error = error;
    }

    public static Result<T> Ok(T value) => new(value);
    public static Result<T> Fail(DomainError error) => new(error);

    public Result<TOut> Map<TOut>(Func<T, TOut> map) =>
        IsSuccess ? Result<TOut>.Ok(map(Value!)) : Result<TOut>.Fail(Error!);

    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> bind) =>
        IsSuccess ? bind(Value!) : Result<TOut>.Fail(Error!);

    public T GetValueOrThrow() =>
        IsSuccess ? Value! : throw new InvalidOperationException($"Result is in error state: {Error!.Message}");
}
