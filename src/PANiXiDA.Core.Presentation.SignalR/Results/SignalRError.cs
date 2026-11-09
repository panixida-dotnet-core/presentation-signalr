namespace PANiXiDA.Core.Presentation.SignalR.Results;

/// <summary>Represents a safe, structured error returned by a hub operation.</summary>
/// <param name="Type">The error category name from ResultPattern, such as Validation or Conflict.</param>
/// <param name="Message">The client-visible error message.</param>
/// <param name="Field">The validation field, or null for other error categories.</param>
public sealed record SignalRError(string Type, string Message, string? Field);
