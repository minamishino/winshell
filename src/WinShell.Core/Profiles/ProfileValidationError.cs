namespace WinShell.Core.Profiles;

public sealed record ProfileValidationError(string PropertyName, string Message);
