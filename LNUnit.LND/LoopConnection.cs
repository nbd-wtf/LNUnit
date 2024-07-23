using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using Looprpc;
using Microsoft.Extensions.Logging;
using ServiceStack;

namespace LNUnit.LND;

public class LoopConnection : IDisposable
{
    private readonly ILogger<LoopConnection>? _logger;
    private Random r = new();

    /// <summary>
    ///     Constructor auto-start
    /// </summary>
    /// <param name="settings">LND Configuration Settings</param>
    public LoopConnection(LoopSettings settings, ILogger<LoopConnection>? logger = null)
    {
        _logger = logger;
        Settings = settings;
        StartWithBase64(settings.TLSCertBase64, settings.MacaroonBase64, settings.GrpcEndpoint);
    }


    public LoopSettings Settings { get; internal set; }

    public string Host { get; internal set; }
    public GrpcChannel gRPCChannel { get; internal set; }
    public Looprpc.SwapClient.SwapClientClient SwapClient { get; set; }


    public void StartWithBase64(string tlsCertBase64, string macaroonBase64, string host)
    {
        Host = host;
        gRPCChannel = CreateGrpcConnection(host, tlsCertBase64, macaroonBase64);
        SwapClient = new SwapClient.SwapClientClient(gRPCChannel);
        _logger?.LogDebug("Setup Loop gRPC with {Host}", host);
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

    public void Dispose()
    {
        gRPCChannel.Dispose();
    }
}