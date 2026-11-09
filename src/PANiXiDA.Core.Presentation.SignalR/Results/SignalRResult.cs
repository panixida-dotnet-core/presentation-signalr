namespace PANiXiDA.Core.Presentation.SignalR.Results;

/// <summary>Represents the serializable outcome of a hub operation without a value.</summary>
/// <param name="IsSuccess">Whether the operation succeeded.</param>
/// <param name="Errors">The operation errors, or an empty collection on success.</param>
public sealed record SignalRResult(bool IsSuccess, IReadOnlyList<SignalRError> Errors);
