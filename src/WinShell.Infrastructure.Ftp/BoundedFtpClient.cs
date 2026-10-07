using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentFTP;
using FluentFTP.Helpers;
using WinShell.Core.Credentials;
using WinShell.Core.Connections;
using WinShell.Core.Profiles;
namespace WinShell.Infrastructure.Ftp;

// Each operation owns this client and every stream it creates, including streams whose TLS open fails.
internal sealed class BoundedFtpClient : AsyncFtpClient
{
    private readonly ConnectionProfile _profile;
    private readonly ICredentialStore _credentials;
    private readonly ConnectionAdmissionBudget _budget;
    private IDisposable? _dataHandshake;
    private readonly List<FtpSocketStream> _dataStreams = [];
    internal TimeSpan Deadline
    {
        get;
    }
    internal bool StoreAttempted
    {
        get; private set;
    }
    internal bool RenameAttempted
    {
        get; private set;
    }
    internal BoundedFtpClient(ConnectionProfile profile, ICredentialStore credentials, TimeSpan timeout, X509ChainPolicy? chainPolicy, ConnectionAdmissionBudget budget)
        : base(profile.Host, profile.UserName, "", profile.Port)
    {
        _profile = profile;
        _credentials = credentials;
        Deadline = timeout;
        _budget = budget;
        Config.EncryptionMode = profile.Protocol == ConnectionProtocol.Ftp ? FtpEncryptionMode.None : profile.FtpsMode == FtpsMode.Implicit ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit;
        Config.DataConnectionEncryption = true;
        Config.EncryptAuthenticationOnly = false;
        Config.ValidateAnyCertificate = false;
        Config.ValidateCertificateRevocation = true;
        Config.Noop = false;
        Config.SslSessionLength = 0;
        Config.SelfConnectMode = FtpSelfConnectMode.Never;
        Config.DataConnectionType = FtpDataConnectionType.AutoPassive;

        Config.ConnectTimeout = Config.ReadTimeout = Config.WriteTimeout = checked((int)timeout.TotalMilliseconds);
        Config.DataConnectionConnectTimeout = Config.DataConnectionReadTimeout = Config.DataConnectionWriteTimeout = checked((int)timeout.TotalMilliseconds);
        Config.DisconnectWithQuit = false;
        Config.StaleDataCheck = false;
        Encoding = new UTF8Encoding(false, true);
        m_stream = new BoundedControlStream(this);
        m_stream.ValidateCertificate += StrictCertificate;
        if (chainPolicy is not null)
            ConfigureAuthentication += (_, options) => options.Options.CertificateChainPolicy = chainPolicy;
    }
    private static void StrictCertificate(FtpSocketStream stream, FtpSslValidationEventArgs args) => args.Accept = args.PolicyErrors == SslPolicyErrors.None;
    protected override async Task ConnectAsync(FtpSocketStream stream, string host, int port, FtpIpVersion ipVersions, CancellationToken token)
    {
        // The mature DATA constructor installs Accept=true. Our last handler makes the final decision before TLS.
        stream.ValidateCertificate += StrictCertificate;
        if (stream is FtpDataStream data)
            data.ControlConnection = null;
        _dataStreams.Add(stream);
        _dataHandshake = await _budget.EnterHandshakeAsync(token).ConfigureAwait(false);
        await base.ConnectAsync(stream, host, port, ipVersions, token).ConfigureAwait(false);
    }
    protected override async Task Authenticate(CancellationToken token)
    {
        if (_profile.Protocol == ConnectionProtocol.Ftps && !IsEncrypted)
            throw new IOException("FTPS 控制通道未加密，拒绝读取凭据。");
        using var secret = _profile.AuthenticationMethod == AuthenticationMethod.Anonymous ? null : await _credentials.ReadAsync(_profile.Id, token).ConfigureAwait(false);
        var password = _profile.AuthenticationMethod == AuthenticationMethod.Anonymous ? "anonymous@" : secret is null ? throw new IOException("此配置没有保存密码。") : new UTF8Encoding(false, true).GetString(secret.Bytes.Span);
        FtpPathPolicy.CommandValue(password, 2560, allowEmpty: true);
        Credentials = new NetworkCredential(_profile.AuthenticationMethod == AuthenticationMethod.Anonymous ? "anonymous" : _profile.UserName, password);
        try
        {
            await base.Authenticate(token).ConfigureAwait(false);
        }
        finally { Credentials = new NetworkCredential(); }
    }
    internal async Task<FtpDataStream> OpenOwnedDataAsync(string command, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Deadline);
        await SetDataTypeAsync(FtpDataType.Binary, deadline.Token).ConfigureAwait(false);
        var stream = await OpenDataStreamAsync(command, 0, deadline.Token).ConfigureAwait(false);
        _dataHandshake?.Dispose();
        _dataHandshake = null;
        return stream;
    }
    internal async Task ConfirmCompletionAsync(FtpDataStream stream, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Deadline);
        await stream.CloseAsync(deadline.Token).ConfigureAwait(false);
        var reply = await GetReply(deadline.Token).ConfigureAwait(false);
        if (reply.Code is not ("226" or "250"))
            throw new IOException("FTP 数据完成应答无效：" + reply.Code);
    }
    internal async Task CloseOwnedAsync()
    {
        List<Exception> errors = [];
        foreach (var stream in _dataStreams)
            try
            {
                await stream.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) { errors.Add(error); }
        try
        {
            await m_stream.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) { errors.Add(error); }
        try
        {
            Dispose();
        }
        catch (Exception error) { errors.Add(error); }
        _dataHandshake?.Dispose();
        _dataHandshake = null;
        if (errors.Count != 0)
            throw new AggregateException("FTP 连接关闭失败。", errors);
    }
    private sealed class BoundedControlStream(BoundedFtpClient client) : FtpSocketStream(client)
    {
        private int _read;
        private void Observe(ReadOnlySpan<byte> command)
        {
            // Observe only opcode bytes; do not decode or retain credentials or command text.
            if (command.StartsWith("STOR "u8))
                client.StoreAttempted = true;
            if (command.StartsWith("RNTO "u8))
                client.RenameAttempted = true;
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Observe(buffer.AsSpan(offset, count));
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Observe(buffer);
            base.Write(buffer);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Observe(buffer.AsSpan(offset, count));
            return base.WriteAsync(buffer, offset, count, token);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Observe(buffer.Span);
            return base.WriteAsync(buffer, token);
        }
        private int Reserve(int count)
        {
            if (count == 0)
                return 0;
            if (_read >= 65536)
                throw new IOException("FTP 控制应答超过 64 KiB 限额。");
            return Math.Min(count, 65536 - _read);
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, Reserve(count));
            _read += read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = base.Read(buffer[..Reserve(buffer.Length)]);
            _read += read;
            return read;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            var read = await base.ReadAsync(buffer, offset, Reserve(count), token).ConfigureAwait(false);
            _read += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        {
            var read = await base.ReadAsync(buffer[..Reserve(buffer.Length)], token).ConfigureAwait(false);
            _read += read;
            return read;
        }
    }
}
