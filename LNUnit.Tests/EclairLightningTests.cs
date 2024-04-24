using BTCPayServer.Lightning.Eclair.Models;
using Docker.DotNet;
using LNUnit.Setup;
using LNUnit.Tests.Fixture;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using ServiceStack.Text;

namespace LNUnit.Tests;

[TestFixture("sqlite", "polarlightning/eclair", "0.10.0", true)]
public class EclairLightningTests : IDisposable
{
    [SetUp]
    public async Task PerTestSetUp()
    {
        Builder.CancelAllInterceptors();
        Builder.NewBlock(); //tick toc new block
        await Builder.NewBlock();
    }

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory()) // Set the current directory as the base path
            .AddJsonFile("appsettings.json", false, true)
            .AddJsonFile("appsettings.Development.json", false, true)
            .Build();
        var services = new ServiceCollection();
        var loggerConfiguration = new LoggerConfiguration().Enrich.FromLogContext();
        loggerConfiguration.ReadFrom.Configuration(configuration);
        Log.Logger = loggerConfiguration.CreateLogger();
        services.AddLogging();
        services.AddSerilog(Log.Logger, true);
        _serviceProvider = services.BuildServiceProvider();
        Builder = ActivatorUtilities.CreateInstance<LNUnitBuilder>(_serviceProvider);

        if (_dbType == "postgres")
        {
            PostgresFixture.AddDb("alice");
            PostgresFixture.AddDb("bob");
            PostgresFixture.AddDb("carol");
        }

        await _client.CreateDockerImageFromPath("./../../../../Docker/bitcoin/27.0",
            ["bitcoin:latest", "bitcoin:27.0"]);
        await SetupNetwork(_lndImage, _tag, _pullImage);
    }

    public EclairLightningTests(string dbType,
        string lndImage = "custom_lnd",
        string tag = "latest",
        bool pullImage = false
    )
    {
        _dbType = dbType;
        _lndImage = lndImage;
        _tag = tag;
        _pullImage = pullImage;
    }


    public string DbContainerName { get; set; } = "postgres";
    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();

    private ServiceProvider _serviceProvider;
    private readonly string _dbType;
    private readonly string _lndImage;
    private readonly string _tag;
    private readonly bool _pullImage;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        // Remove containers
        _client.RemoveContainer("miner").GetAwaiter().GetResult();
        _client.RemoveContainer("alice").GetAwaiter().GetResult();
        _client.RemoveContainer("bob").GetAwaiter().GetResult();
        _client.RemoveContainer("carol").GetAwaiter().GetResult();

        Builder?.Destroy();
        Builder?.Dispose();
        _client.Dispose();
    }

    public LNUnitBuilder? Builder { get; private set; }


    public async Task SetupNetwork(string eclairImage = "polarlightning/eclair", string eclairTag = "0.10.0",
        bool pullEclairImage = false, string bitcoinImage = "polarlightning/bitcoind",
        string bitcoinTag = "26.0",
        bool pullBitcoinImage = false)
    {
        await _client.RemoveContainer("miner");
        await _client.RemoveContainer("alice");
        await _client.RemoveContainer("bob");
        await _client.RemoveContainer("carol");

        Builder.AddBitcoinCoreNode(image: bitcoinImage, tag: bitcoinTag, pullImage: pullBitcoinImage);

        if (pullEclairImage) await _client.PullImageAndWaitForCompleted(eclairImage, eclairTag);


        Builder.AddPolarEclairNode("alice",
            [
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemoteName = "bob"
                },
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemoteName = "bob"
                },
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemoteName = "bob"
                },
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemoteName = "bob"
                }
            ], pullImage: true, tagName: eclairTag, imageName: eclairImage,
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["alice"] : null);

        Builder.AddPolarEclairNode("bob",
            [
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemotePushOnStart = 1_000_000, // 1MSat
                    RemoteName = "alice"
                }
            ], mapTotmp: true, pullImage: true,
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["bob"] : null);

        // Builder.AddPolarEclairNode("carol",
        //     [
        //         new LNUnitNetworkDefinition.Channel
        //         {
        //             ChannelSize = 10_000_000, //10MSat
        //             RemotePushOnStart = 1_000_000, // 1MSat
        //             RemoteName = "bob"
        //         },
        //         new LNUnitNetworkDefinition.Channel
        //         {
        //             ChannelSize = 10_000_000, //10MSat
        //             RemotePushOnStart = 1_000_000, // 1MSat
        //             RemoteName = "bob"
        //         },
        //         new LNUnitNetworkDefinition.Channel
        //         {
        //             ChannelSize = 10_000_000, //10MSat
        //             RemotePushOnStart = 1_000_000, // 1MSat
        //             RemoteName = "bob"
        //         },
        //         new LNUnitNetworkDefinition.Channel
        //         {
        //             ChannelSize = 10_000_000, //10MSat
        //             RemotePushOnStart = 1_000_000, // 1MSat
        //             RemoteName = "bob"
        //         }
        //     ], mapTotmp: true,
        //     postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["carol"] : null);

        await Builder.Build();

        await WaitNodesReady();
        //await WaitGraphReady();
    }

    private async Task WaitNodesReady()
    {
        var alice = Builder.EclairNodePool.ReadyNodes.First(x => x.LocalAlias == "alice");

        var ready = false;
        while (!ready)
        {
            var n = await alice.NodeClient.AllNodes();
            if (n.Count >= Builder.EclairNodePool.ReadyNodes.Count)
            {
                ready = true;
                continue;
            }

            "Nodes not in graph, waiting 250ms".Print();
            await Task.Delay(250);
        }
        // var a = Builder.WaitUntilLndAliasIsServerReady("alice");
        // var b = Builder.WaitUntilLndAliasIsServerReady("bob");
        // var c = Builder.WaitUntilLndAliasIsServerReady("carol");
        //Task.WaitAll(a, b, c);
    }


    [Test]
    [Timeout(10000)]
    public async Task EclairRunning()
    {
        var alice = Builder.EclairNodePool.ReadyNodes.First(x => x.LocalAlias == "alice");
        var bob = Builder.EclairNodePool.ReadyNodes.First(x => x.LocalAlias == "bob");
        var invoice = await alice.NodeClient.CreateInvoice("test", 10_000);
        var payId = await bob.NodeClient.PayInvoice(new PayInvoiceRequest()
        {
            Invoice = invoice.Serialized,
            MaxAttempts = 1, MaxFeePct = 1,
        });
        payId.PrintDump();
        await Task.Delay(1000);

        var sendStatus = await bob.NodeClient.GetSentInfo(invoice.PaymentHash);
        sendStatus.PrintDump();
    }
}