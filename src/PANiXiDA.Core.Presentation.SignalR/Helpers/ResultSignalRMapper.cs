using PANiXiDA.Core.Presentation.SignalR.Results;
using PANiXiDA.Core.ResultPattern;

namespace PANiXiDA.Core.Presentation.SignalR.Helpers;

/// <summary>Maps application results to serializable SignalR operation responses.</summary>
public static class ResultSignalRMapper
{
    /// <summary>Maps a result without a value to a structured hub response.</summary>
    /// <param name="result">The application result.</param>
    /// <returns>A response containing the success flag and safe error details.</returns>
    /// <exception cref="ArgumentNullException">The result is null.</exception>
    public static SignalRResult ToSignalRResult(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new SignalRResult(result.IsSuccess, MapErrors(result.Errors));
    }

    /// <summary>Maps a result with a value to a structured hub response without reading a failed result's Value.</summary>
    /// <typeparam name="TValue">The successful value type.</typeparam>
    /// <param name="result">The application result.</param>
    /// <returns>A response containing the success flag, value or default, and safe error details.</returns>
    /// <exception cref="ArgumentNullException">The result is null.</exception>
    public static SignalRResult<TValue> ToSignalRResult<TValue>(this Result<TValue> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new SignalRResult<TValue>(result.IsSuccess, result.ValueOrDefault, MapErrors(result.Errors));
    }

    private static IReadOnlyList<SignalRError> MapErrors(IReadOnlyList<Error> errors)
    {
        return Array.AsReadOnly(errors.Select(MapError).ToArray());
    }

    private static SignalRError MapError(Error error)
    {
        switch (error.Type)
        {
            case ErrorType.Validation:
                var field = error.Metadata.TryGetValue(Error.FieldMetadataKey, out var value)
                    && value is string name && !string.IsNullOrWhiteSpace(name) ? name : "general";
                return new SignalRError(nameof(ErrorType.Validation), error.Message, field);
            case ErrorType.NotFound:
            case ErrorType.Conflict:
            case ErrorType.Unauthorized:
            case ErrorType.Forbidden:
            case ErrorType.Failure:
                return new SignalRError(error.Type.ToString(), error.Message, null);
            default:
                return new SignalRError(nameof(ErrorType.Unexpected), "An unexpected error occurred.", null);
        }
    }
}
