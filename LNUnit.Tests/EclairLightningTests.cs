using System.Collections.Immutable;
using System.Diagnostics;
using Dasync.Collections;
using Docker.DotNet;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Grpc.Core;
using Lnrpc;
using LNUnit.Extentions;
using LNUnit.LND;
using LNUnit.Setup;
using LNUnit.Tests.Fixture;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Routerrpc;
using Serilog;
using ServiceStack;
using ServiceStack.Text;
using Assert = NUnit.Framework.Assert;
 
namespace LNUnit.Tests.Fixture;

[TestFixture("postgres", "lightninglabs/lnd", "daily-testing-only", "/root/.lnd", true)]
public  class EclairLightningTests : IDisposable
{
    public EclairLightningTests(string dbType,
        string lndImage = "custom_lnd",
        string tag = "latest",
        string lndRoot = "/root/.lnd",
        bool pullImage = false
    )
    {
        _dbType = dbType;
        _lndImage = lndImage;
        _tag = tag;
        _lndRoot = lndRoot;
        _pullImage = pullImage;
    }

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
        
        await _client.CreateDockerImageFromPath("./../../../../Docker/bitcoin/27.0", ["bitcoin:latest", "bitcoin:27.0"]);
        await SetupNetwork(_lndImage, _tag, _lndRoot, _pullImage);
    }



    public string DbContainerName { get; set; } = "postgres";
    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();

    private ServiceProvider _serviceProvider;
    private readonly string _dbType;
    private readonly string _lndImage;
    private readonly string _tag;
    private readonly string _lndRoot;
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


    public async Task SetupNetwork(string lndImage = "lightninglabs/lnd", string lndTag = "daily-testing-only",
        string lndRoot = "/root/.lnd", bool pullLndImage = false, string bitcoinImage = "bitcoin", string bitcoinTag = "27.0",
         bool pullBitcoinImage = false)
    {
        await _client.RemoveContainer("miner");
        await _client.RemoveContainer("alice");
        await _client.RemoveContainer("bob");
        await _client.RemoveContainer("carol");

        Builder.AddBitcoinCoreNode(image: bitcoinImage, tag: bitcoinTag, pullImage: pullBitcoinImage);

        if (pullLndImage) await _client.PullImageAndWaitForCompleted(lndImage, lndTag);


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
            ],
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["alice"] : null);

        Builder.AddPolarEclairNode("bob",
            [
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemotePushOnStart = 1_000_000, // 1MSat
                    RemoteName = "alice"
                }
            ],  mapTotmp: true,
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["bob"] : null);

        Builder.AddPolarEclairNode("carol",
            [
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemotePushOnStart = 1_000_000, // 1MSat
                    RemoteName = "bob"
                },
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemotePushOnStart = 1_000_000, // 1MSat
                    RemoteName = "bob"
                },
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemotePushOnStart = 1_000_000, // 1MSat
                    RemoteName = "bob"
                },
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemotePushOnStart = 1_000_000, // 1MSat
                    RemoteName = "bob"
                }
            ],  mapTotmp: true,
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["carol"] : null);

        await Builder.Build();

        WaitNodesReady();
        //await WaitGraphReady();
    }

    private void WaitNodesReady()
    {
        // var a = Builder.WaitUntilLndAliasIsServerReady("alice");
        // var b = Builder.WaitUntilLndAliasIsServerReady("bob");
        // var c = Builder.WaitUntilLndAliasIsServerReady("carol");
        //Task.WaitAll(a, b, c);
    }

    private async Task WaitGraphReady(string fromAlias = "alice")
    {
        var graphReady = false;
        while (!graphReady)
        {
            var graph = await Builder.GetGraphFromAlias(fromAlias);
            if (graph.Nodes.Count < 3)
            {
                "Graph not ready...".Print();
                await Task.Delay(250); //let the graph sync 
            }
            else
            {
                graphReady = true;
            }
        }
    }


    public async Task<bool> IsRunning()
    {
        try
        {
            var inspect = await _client.Containers.InspectContainerAsync(DbContainerName);
            return inspect.State.Running;
        }
        catch
        {
            // ignored
        }

        return false;
    }


    [Test]
    public async Task EclairRunning()
    {
        await Task.Delay(10000);
    }
   
}