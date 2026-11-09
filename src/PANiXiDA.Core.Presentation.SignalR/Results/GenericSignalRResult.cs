namespace PANiXiDA.Core.Presentation.SignalR.Results;

/// <summary>Represents the serializable outcome of a hub operation with a value.</summary>
/// <typeparam name="TValue">The successful value type.</typeparam>
/// <param name="IsSuccess">Whether the operation succeeded.</param>
/// <param name="Value">The successful value, or default on failure. Read only when IsSuccess is true.</param>
/// <param name="Errors">The operation errors, or an empty collection on success.</param>
public sealed record SignalRResult<TValue>(bool IsSuccess, TValue? Value, IReadOnlyList<SignalRError> Errors);
