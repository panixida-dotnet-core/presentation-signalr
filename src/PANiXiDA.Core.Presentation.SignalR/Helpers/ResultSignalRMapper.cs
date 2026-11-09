using Microsoft.AspNetCore.SignalR;

using PANiXiDA.Core.ResultPattern;

namespace PANiXiDA.Core.Presentation.SignalR.Helpers;

/// <summary>Maps application result failures to standard SignalR exceptions.</summary>
public static class ResultSignalRMapper
{
    /// <summary>Throws a HubException for a failed result and returns normally on success.</summary>
    /// <param name="result">The application result.</param>
    /// <exception cref="ArgumentNullException">The result is null.</exception>
    /// <exception cref="HubException">The result failed. The message is based on its first error; unexpected details are hidden.</exception>
    public static void ThrowIfFailure(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsFailure)
        {
            throw new HubException(GetErrorMessage(result.FirstError));
        }
    }

    /// <summary>Returns the successful value or throws a HubException for a failed result.</summary>
    /// <typeparam name="TValue">The successful value type.</typeparam>
    /// <param name="result">The application result.</param>
    /// <returns>The successful value, including zero, false, or an allowed null.</returns>
    /// <exception cref="ArgumentNullException">The result is null.</exception>
    /// <exception cref="HubException">The result failed. The message is based on its first error; unexpected details are hidden.</exception>
    public static TValue GetValueOrThrow<TValue>(this Result<TValue> result)
    {
        result.ThrowIfFailure();
        return result.Value;
    }

    private static string GetErrorMessage(Error error)
    {
        return error.Type switch
        {
            ErrorType.Validation or ErrorType.NotFound or ErrorType.Conflict
                or ErrorType.Unauthorized or ErrorType.Forbidden or ErrorType.Failure => error.Message,
            _ => "An unexpected error occurred."
        };
    }
}
