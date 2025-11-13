using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using Looprpc;
using Microsoft.Extensions.Logging;
using ServiceStack;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

namespace LNUnit.LND;

public class LoopConnection : IDisposable
{
    private readonly ILogger<LoopConnection>? _logger;

    /// <summary>
    ///     Constructor auto-start
    /// </summary>
    /// <param name="settings">LND Configuration Settings</param>
    /// <param name="logger"></param>
    public LoopConnection(LoopSettings settings, ILogger<LoopConnection>? logger = null)
    {
        _logger = logger;
        Settings = settings;
        StartWithBase64(settings.TlsCertBase64 ?? throw new InvalidOperationException(),
            settings.MacaroonBase64 ?? throw new InvalidOperationException(),
            settings.GrpcEndpoint);
    }


    public LoopSettings Settings { get; internal set; }

    public string Host { get; internal set; }
    private GrpcChannel GRpcChannel { get; set; }
    public SwapClient.SwapClientClient SwapClient { get; set; }

    public void Dispose()
    {
        GRpcChannel.Dispose();
    }


    public void StartWithBase64(string tlsCertBase64, string macaroonBase64, string host)
    {
        Host = host;
        GRpcChannel = CreateGrpcConnection(host, tlsCertBase64, macaroonBase64);
        SwapClient = new SwapClient.SwapClientClient(GRpcChannel);
        _logger?.LogDebug("Setup Loop gRPC with {Host}", host);
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
}