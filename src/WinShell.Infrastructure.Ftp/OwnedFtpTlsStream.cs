using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using FluentFTP.Client.BaseClient;
using FluentFTP.Streams;
namespace WinShell.Infrastructure.Ftp;

// Fluent's public custom-TLS boundary hands us the actual socket. No protocol parsing lives here.
internal sealed class OwnedFtpTlsStream : IFtpStream
{
    private readonly object _gate = new();
    private Socket? _socket;
    private SslStream? _stream;
    private TimeSpan _deadline;
    private Task? _close;
    private bool _authenticated;
    internal Exception? CloseFailure
    {
        get; private set;
    }
    public OwnedFtpTlsStream()
    {
    } // Required by the mature CustomStream factory.

    public void Init(BaseFtpClient client, string targetHost, Socket socket,
        CustomRemoteCertificateValidationCallback validateCertificate, bool isControl,
        IFtpStream controlConnStream, IFtpStreamConfig config)
    {
        var owner = (BoundedFtpClient)client;
        _socket = socket;
        _deadline = owner.Deadline;
        owner.RegisterTlsStream(this); // Register before authentication, including every failed DATA open.
        _stream = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false,
            (sender, certificate, chain, errors) =>
                validateCertificate(sender, certificate!, chain!, errors == SslPolicyErrors.None ? "" : errors.ToString()) && errors == SslPolicyErrors.None);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            ClientCertificates = client.Config.ClientCertificates,
            EnabledSslProtocols = client.Config.SslProtocols,
            CertificateRevocationCheckMode = client.Config.ValidateCertificateRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck
        };
        owner.ConfigureTlsAuthentication(options); // Includes fixture-only CustomRootTrust, never a replacement callback.
        if (options.RemoteCertificateValidationCallback is not null)
            throw new InvalidOperationException("FTPS 不允许替换严格证书验证回调。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(owner.AuthenticationToken);
        deadline.CancelAfter(_deadline);
        using var abort = deadline.Token.Register(AbortSocket);
        // IFtpStream.Init is synchronous. The entire product operation runs on its owned worker;
        // cancellation/deadline aborts this real socket, then this call joins the authentication task.
        try
        {
            _stream.AuthenticateAsClientAsync(options, deadline.Token).GetAwaiter().GetResult();
            _authenticated = true;
        }
        catch (Exception error) when (deadline.IsCancellationRequested)
        {
            if (owner.AuthenticationToken.IsCancellationRequested)
                throw new OperationCanceledException("FTPS 握手已取消。", error, owner.AuthenticationToken);
            throw new TimeoutException("FTPS 握手超时。", error);
        }
    }
    public Stream GetBaseStream() => _stream ?? throw new ObjectDisposedException(nameof(OwnedFtpTlsStream));
    public bool CanRead() => _stream?.CanRead == true;
    public bool CanWrite() => _stream?.CanWrite == true;
    public SslProtocols GetSslProtocol() => _stream?.SslProtocol ?? SslProtocols.None;
    public string GetCipherSuite() => _stream?.NegotiatedCipherSuite.ToString() ?? "";
    public void Dispose()
    {
        Task close;
        lock (_gate)
            close = _close ??= CloseAsync();
        close.GetAwaiter().GetResult(); // Required synchronous public contract, bounded by actual socket interruption.
    }
    private async Task CloseAsync()
    {
        using var deadline = new CancellationTokenSource(_deadline);
        using var abort = deadline.Token.Register(AbortSocket);
        try
        {
            if (_authenticated && _stream is not null)
                await _stream.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            CloseFailure = deadline.IsCancellationRequested ? new TimeoutException("FTPS 关闭超时；已中断底层连接并等待关闭完成。", error) : error;
        }
        finally
        {
            AbortSocket();
            try
            {
                if (_stream is not null)
                    await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) { CloseFailure ??= error; }
        }
    }
    private void AbortSocket() => Interlocked.Exchange(ref _socket, null)?.Dispose();
}
