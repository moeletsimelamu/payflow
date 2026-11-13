using Microsoft.AspNetCore.Http;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Infrastructure.Http;

public static class ResultHttpExtensions
{
    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : ErrorResults.Problem(result.Error);
    }

    public static IResult ToHttp<T>(this Result<T> result) => result.ToHttp(value => Results.Ok(value));

    public static IResult ToHttp(this Result result, Func<IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess() : ErrorResults.Problem(result.Error);
    }
}
