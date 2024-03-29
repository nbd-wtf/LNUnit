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

namespace LNUnit.LND;

public class LNDNodeConnection : IDisposable
{
    private readonly ILogger<LNDNodeConnection>? _logger;
    private Random r = new();

    /// <summary>
    ///     Constructor auto-start
    /// </summary>
    /// <param name="settings">LND Configuration Settings</param>
    public LNDNodeConnection(LNDSettings settings, ILogger<LNDNodeConnection>? logger = null)
    {
        _logger = logger;
        Settings = settings;
        StartWithBase64(settings.TLSCertBase64, settings.MacaroonBase64, settings.GrpcEndpoint);
    }


    public LNDSettings Settings { get; internal set; }

    public string Host { get; internal set; }
    public GrpcChannel gRPCChannel { get; internal set; }
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
    public bool IsRPCReady
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
        gRPCChannel?.Dispose();
    }

    public void StartWithBase64(string tlsCertBase64, string macaroonBase64, string host)
    {
        Host = host;
        gRPCChannel = CreateGrpcConnection(host, tlsCertBase64, macaroonBase64);
        LightningClient = new Lightning.LightningClient(gRPCChannel);
        RouterClient = new Router.RouterClient(gRPCChannel);
        SignClient = new Signer.SignerClient(gRPCChannel);
        StateClient = new State.StateClient(gRPCChannel);
        ChainNotifierClient = new ChainNotifier.ChainNotifierClient(gRPCChannel);
        DevClient = new Dev.DevClient(gRPCChannel);
        InvoiceClient = new Invoices.InvoicesClient(gRPCChannel);
        PeersClient = new Peers.PeersClient(gRPCChannel);
        WalletKitClient = new WalletKit.WalletKitClient(gRPCChannel);
        _logger?.LogDebug("Setup gRPC with {Host}", host);

        var nodeInfo = LightningClient.GetInfo(new GetInfoRequest());
        LocalNodePubKey = nodeInfo.IdentityPubkey;
        LocalAlias = nodeInfo.Alias;
        _logger?.LogDebug("Connected gRPC to {Alias} {PubKey} @ {Host}", LocalAlias, LocalNodePubKey, host);

        ClearnetConnectString = nodeInfo.Uris.FirstOrDefault(x => !x.Contains("onion"));
        OnionConnectString = nodeInfo.Uris.FirstOrDefault(x => x.Contains("onion"));
    }

    public GrpcChannel CreateGrpcConnection(string grpcEndpoint, string TLSCertBase64, string MacaroonBase64)
    {
        // Due to updated ECDSA generated tls.cert we need to let gprc know that
        // we need to use that cipher suite otherwise there will be a handshake
        // error when we communicate with the lnd rpc server.

        Environment.SetEnvironmentVariable("GRPC_SSL_CIPHER_SUITES", "HIGH+ECDSA");
        var httpClientHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (httpRequestMessage, cert, cetChain, policyErrors) => true
        };
        var x509Cert = new X509Certificate2(Convert.FromBase64String(TLSCertBase64));

        httpClientHandler.ClientCertificates.Add(x509Cert);
        string macaroon;

        macaroon = Convert.FromBase64String(MacaroonBase64).ToHex();


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
        catch (RpcException e)
        {
        }
        catch (Exception e)
        {
        }
        return WalletState.NonExisting;
    }

    public Task Stop()
    {
        gRPCChannel.Dispose();
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
        Payment paymentResponse = null;
        await foreach (var res in streamingCallResponse.ResponseStream.ReadAllAsync()) paymentResponse = res;
        return paymentResponse;
    }

    public LNDNodeConnection Clone()
    {
        return new LNDNodeConnection(Settings, _logger);
    }
}