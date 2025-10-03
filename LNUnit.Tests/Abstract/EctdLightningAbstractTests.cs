using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using Dasync.Collections;
using Docker.DotNet;
using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Grpc.Core;
using Invoicesrpc;
// using LNBolt;
using Lnrpc;
using LNUnit.Extentions;
using LNUnit.LND;
using LNUnit.Setup;
using LNUnit.Tests.Fixture;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using Routerrpc;
using Serilog;
using ServiceStack;
using ServiceStack.Text;
using Walletrpc;
using AddressType = Lnrpc.AddressType;
using Assert = NUnit.Framework.Assert;
using ListUnspentRequest = Lnrpc.ListUnspentRequest;
using Network = NBitcoin.Network;
using Transaction = Walletrpc.Transaction;

namespace LNUnit.Tests.Abstract;


public abstract class EctdLightningAbstractTests : IDisposable
{
    public EctdLightningAbstractTests(string dbType,
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

        await _client.CreateDockerImageFromPath("../../../../Docker/lnd", ["custom_lnd", "custom_lnd:latest"]);
        await _client.CreateDockerImageFromPath("./../../../../Docker/bitcoin/30.0", ["bitcoin:latest", "bitcoin:30.0"]);
        await SetupNetwork(_lndImage, _tag, _lndRoot, _pullImage, bitcoinImage: "bitcoin", bitcoinTag: "30.0", pullBitcoinImage: false);
    }



    public string DbContainerName { get; set; } = "postgres";
    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();

    private ServiceProvider _serviceProvider;

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
        string lndRoot = "/root/.lnd", bool pullLndImage = false, string bitcoinImage = "polarlightning/bitcoind", string bitcoinTag = "29.0",
         bool pullBitcoinImage = false)
    {
        await _client.RemoveContainer("miner");
        await _client.RemoveContainer("alice");
        await _client.RemoveContainer("bob");
        await _client.RemoveContainer("carol");
        await _client.RemoveContainer("etcd");

        Builder.AddBitcoinCoreNode(image: bitcoinImage, tag: bitcoinTag, pullImage: pullBitcoinImage);

        if (pullLndImage)
        {
            await _client.PullImageAndWaitForCompleted(lndImage, lndTag);
        }

        if (pullBitcoinImage)
        {
            await _client.PullImageAndWaitForCompleted(bitcoinImage, bitcoinTag);
        }


        Builder.AddPolarLNDNode("alice",
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
            ], imageName: lndImage, tagName: lndTag, pullImage: false, acceptKeysend: true, mapTotmp: false,
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["alice"] : null, lndkSupport: false, nativeSql: _dbType != "boltdb", storeFinalHtlcResolutions: true,
            etcdConfiguration: new LNUnitBuilderExtensions.LndClusterConfig()
            {
                Id = "alice",
                ElectionPrefix = "lnd-cluster"
            });

        Builder.AddPolarLNDNode("bob",
            [
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemotePushOnStart = 1_000_000, // 1MSat
                    RemoteName = "alice"
                }
            ], imageName: lndImage, tagName: lndTag, pullImage: false, acceptKeysend: true, mapTotmp: false,
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["bob"] : null, lndkSupport: false, nativeSql: _dbType != "boltdb");

        Builder.AddPolarLNDNode("carol",
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
            ], imageName: lndImage, tagName: lndTag, pullImage: false, acceptKeysend: true, mapTotmp: false,
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["carol"] : null, lndkSupport: false, nativeSql: _dbType != "boltdb");

        await Builder.Build(lndRoot: lndRoot, etcdEnabled: true);

        await WaitNodesReady();
        await WaitGraphReady();

        Assert.That(await CheckEctdRunning(), Is.True);
    }

    private async Task WaitNodesReady()
    {
        var a = await Builder.WaitUntilAliasIsServerReady("alice");
        var b = await Builder.WaitUntilAliasIsServerReady("bob");
        var c = await Builder.WaitUntilAliasIsServerReady("carol");
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

    [Test(ExpectedResult = true)]
    public async Task<bool> CheckEctdRunning()
    {
        var inspect = await _client.Containers.InspectContainerAsync("etcd");
        Assert.IsTrue(inspect.State.Running, "Etcd running is not running");
        var etcdClient = new EtcdClient($"http://{inspect.NetworkSettings.IPAddress}:2379");
        var status = await etcdClient.StatusAsync(new StatusRequest());
        Assert.That(status.Version, Is.EqualTo("3.5.9"));
        return true;
    }

    [Test]
    [Timeout(2000)]
    [Category("Version")]
    [NonParallelizable]
    public async Task CheckLNDVersion()
    {
        var n = await Builder.WaitUntilAliasIsServerReady("alice");
        var info = n.LightningClient.GetInfo(new GetInfoRequest());
        info.Version.Print();
    }


    private readonly MemoryCache _aliasCache = new(new MemoryCacheOptions { SizeLimit = 10000 });
    protected readonly string _dbType;
    protected readonly string _lndImage;
    protected readonly string _tag;
    protected readonly string _lndRoot;
    protected readonly bool _pullImage;

    private async Task<string> ToAlias(LNDNodeConnection c, string remotePubkey)
    {
        _aliasCache.TryGetValue(remotePubkey, out string alias);
        if (alias.IsNullOrEmpty())
            try
            {
                var nodeInfo = await c.LightningClient.GetNodeInfoAsync(new NodeInfoRequest { PubKey = remotePubkey });
                _aliasCache.Set(remotePubkey, nodeInfo.Node.Alias,
                    new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                        Size = 1
                    });
                return nodeInfo.Node.Alias;
            }
            catch (Exception e)
            {
                return remotePubkey;
            }

        return alias;
    }


}