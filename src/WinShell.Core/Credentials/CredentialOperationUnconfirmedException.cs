namespace WinShell.Core.Credentials;

public sealed class CredentialOperationUnconfirmedException : Exception
{
    public CredentialOperationUnconfirmedException() : base("Windows 凭据操作结果尚未确认。") { }
    public CredentialOperationUnconfirmedException(Exception innerException)
        : base("Windows 凭据操作结果尚未确认。", innerException) { }
}
