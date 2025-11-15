using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Chainrpc;
using Devrpc;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Invoicesrpc;
using Lnrpc;
using Microsoft.Extensions.Logging;
using Peersrpc;
using Routerrpc;
using ServiceStack;
using Signrpc;
using Walletrpc;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

namespace LNUnit.LND;

public class LNDNodeConnection : IDisposable
{
    private readonly ILogger<LNDNodeConnection>? _logger;

    /// <summary>
    ///     Constructor auto-start
    /// </summary>
    /// <param name="settings">LND Configuration Settings</param>
    /// <param name="logger"></param>
    public LNDNodeConnection(LNDSettings settings, ILogger<LNDNodeConnection>? logger = null)
    {
        _logger = logger;
        Settings = settings;
        StartWithBase64(settings.TlsCertBase64 ?? throw new InvalidOperationException(),
            settings.MacaroonBase64 ?? throw new InvalidOperationException(),
            settings.GrpcEndpoint);
    }


    public LNDSettings Settings { get; internal set; }

    public string Host { get; internal set; }
    private GrpcChannel GRpcChannel { get; set; }
    public Lightning.LightningClient LightningClient { get; internal set; }
    public Router.RouterClient RouterClient { get; internal set; }
    public Signer.SignerClient SignClient { get; internal set; }
    public State.StateClient StateClient { get; internal set; }

    public ChainNotifier.ChainNotifierClient ChainNotifierClient { get; internal set; }
    public Dev.DevClient DevClient { get; internal set; }
    public Invoices.InvoicesClient InvoiceClient { get; internal set; }
    public Peers.PeersClient PeersClient { get; internal set; }
    public WalletKit.WalletKitClient WalletKitClient { get; internal set; }


    public string LocalNodePubKey { get; internal set; }

    public byte[] LocalNodePubKeyBytes => Convert.FromHexString(LocalNodePubKey);

    public string LocalAlias { get; internal set; }
    public string ClearnetConnectString { get; internal set; }
    public string OnionConnectString { get; internal set; }

    /// <summary>
    ///     Is at least at RPC Ready (will also report true if in ServerActive state)
    /// </summary>
    public bool IsRpcReady
    {
        get
        {
            var stateSafe = GetStateSafe();
            return stateSafe is WalletState.RpcActive or WalletState.ServerActive;
        }
    }

    /// <summary>
    ///     Server is ready for all functions
    /// </summary>
    public bool IsServerReady => GetStateSafe() == WalletState.ServerActive;

    public void Dispose()
    {
        GRpcChannel.Dispose();
    }

    public void StartWithBase64(string tlsCertBase64, string macaroonBase64, string host)
    {
        Host = host;
        GRpcChannel = CreateGrpcConnection(host, tlsCertBase64, macaroonBase64);
        LightningClient = new Lightning.LightningClient(GRpcChannel);
        RouterClient = new Router.RouterClient(GRpcChannel);
        SignClient = new Signer.SignerClient(GRpcChannel);
        StateClient = new State.StateClient(GRpcChannel);
        ChainNotifierClient = new ChainNotifier.ChainNotifierClient(GRpcChannel);
        DevClient = new Dev.DevClient(GRpcChannel);
        InvoiceClient = new Invoices.InvoicesClient(GRpcChannel);
        PeersClient = new Peers.PeersClient(GRpcChannel);
        WalletKitClient = new WalletKit.WalletKitClient(GRpcChannel);
        _logger?.LogDebug("Setup gRPC with {Host}", host);

        var nodeInfo = LightningClient.GetInfo(new GetInfoRequest());
        LocalNodePubKey = nodeInfo.IdentityPubkey;
        LocalAlias = nodeInfo.Alias;
        _logger?.LogDebug("Connected gRPC to {Alias} {PubKey} @ {Host}", LocalAlias, LocalNodePubKey, host);

        ClearnetConnectString = nodeInfo.Uris.FirstOrDefault(x => !x.Contains("onion")) ?? string.Empty;
        OnionConnectString = nodeInfo.Uris.FirstOrDefault(x => x.Contains("onion")) ?? string.Empty;
    }

    public GrpcChannel CreateGrpcConnection(string grpcEndpoint, string tlsCertBase64, string macaroonBase64)
    {
        // Due to updated ECDSA generated tls.cert we need to let gprc know that
        // we need to use that cipher suite otherwise there will be a handshake
        // error when we communicate with the lnd rpc server.

        Environment.SetEnvironmentVariable("GRPC_SSL_CIPHER_SUITES", "HIGH+ECDSA");
        var httpClientHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        var certBytes = Convert.FromBase64String(tlsCertBase64);
        var x509Cert = X509CertificateLoader.LoadCertificate(certBytes);

        httpClientHandler.ClientCertificates.Add(x509Cert);
        string macaroon;

        macaroon = Convert.FromBase64String(macaroonBase64).ToHex();


        var credentials = CallCredentials.FromInterceptor((_, metadata) =>
        {
            metadata.Add(new Metadata.Entry("macaroon", macaroon));
            return Task.CompletedTask;
        });

        var grpcChannel = GrpcChannel.ForAddress(
            grpcEndpoint,
            new GrpcChannelOptions
            {
                DisposeHttpClient = true,
                HttpHandler = httpClientHandler,
                Credentials = ChannelCredentials.Create(new SslCredentials(), credentials),
                MaxReceiveMessageSize = 128000000,
                MaxSendMessageSize = 128000000
            });
        return grpcChannel;
    }

    public WalletState GetStateSafe(double timeOutSeconds = 3)
    {
        try
        {
            var res = StateClient.GetState(new GetStateRequest(), null, DateTime.UtcNow.AddSeconds(timeOutSeconds));
            return res.State;
        }
        catch (RpcException)
        {
            // ignored
        }
        catch (Exception)
        {
            // ignored
        }

        return WalletState.NonExisting;
    }

    public Task Stop()
    {
        GRpcChannel.Dispose();
        return Task.CompletedTask;
    }

    public async Task<Payment?> KeysendPayment(string dest, long amtSat, long feeLimitSat = 10, string? message = null,
        int timeoutSeconds = 60, Dictionary<ulong, byte[]>? keySendPairs = null)
    {
        var randomBytes = RandomNumberGenerator.GetBytes(32); // new byte[32];
        //r.NextBytes(randomBytes);
        var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(randomBytes);
        var payment = new SendPaymentRequest
        {
            Dest = ByteString.CopyFrom(Convert.FromHexString(dest)),
            Amt = amtSat,
            FeeLimitSat = feeLimitSat,
            PaymentHash = ByteString.CopyFrom(hash),
            TimeoutSeconds = timeoutSeconds
        };
        payment.DestCustomRecords.Add(5482373484, ByteString.CopyFrom(randomBytes)); //keysend
        if (keySendPairs != null)
            foreach (var kvp in keySendPairs)
                payment.DestCustomRecords.Add(kvp.Key, ByteString.CopyFrom(kvp.Value));
        if (message != null)
            payment.DestCustomRecords.Add(34349334,
                ByteString.CopyFrom(Encoding.Default.GetBytes(message))); //message type
        var streamingCallResponse = RouterClient.SendPaymentV2(payment);
        Payment? paymentResponse = null;
        await foreach (var res in streamingCallResponse.ResponseStream.ReadAllAsync().ConfigureAwait(false))
            paymentResponse = res;
        return paymentResponse;
    }

    public LNDNodeConnection Clone()
    {
        return new LNDNodeConnection(Settings, _logger);
    }
}