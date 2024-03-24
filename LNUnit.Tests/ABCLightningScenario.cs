using System.Buffers.Text;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Unicode;
using Dasync.Collections;
using Docker.DotNet;
using Docker.DotNet.Models;
using Google.Protobuf;
using Grpc.Core;
using Lnrpc;
using LNUnit.Extentions;
using LNUnit.LND;
using LNUnit.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Routerrpc;
using Serilog;
using ServiceStack;
using ServiceStack.Text;

namespace LNUnit.Tests;

public partial class ABCLightningScenario
{
    private readonly MemoryCache _aliasCache = new(new MemoryCacheOptions { SizeLimit = 10000 });
    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly Random _random = new();
    private WebApplication _host;

    private bool _isGitlab;
    private LNUnitBuilder _LNUnitBuilder;

    [SetUp]
    public async Task PerTestSetUp()
    {
        _LNUnitBuilder.CancelAllInterceptors();
        await _LNUnitBuilder.NewBlock();
        await WaitForNodesReady();
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var builder = WebApplication.CreateBuilder(new string[] { });

        //  builder.Services.AddSingleton(builder.Configuration.GetAWSOptions());

        var loggerConfiguration = new LoggerConfiguration().Enrich.FromLogContext();
        loggerConfiguration.ReadFrom.Configuration(builder.Configuration);

        Log.Logger = loggerConfiguration.CreateLogger();
        builder.Host.UseSerilog(dispose: true); //Log.Logger is implicitly used


        builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });

        builder.Services.Configure<GzipCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardLimit = 1;
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });
        builder.Services.Configure<HttpsRedirectionOptions>(options => { options.HttpsPort = 443; });
        builder.Services.Configure<MemoryCacheOptions>(o =>
        {
            o.ExpirationScanFrequency = new TimeSpan(0, 10, 0); // we can have stale records up to 10m
            o.SizeLimit = 100_000; //Should cover whole never work for a while, it is in record, not size
        });
        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });

        // Add services to the container.
        builder.Services.AddMemoryCache();
        builder.Services.AddControllers();
        builder.Services.AddOptions();


        // builder.Services.Configure<BTCSettings>(o =>
        // {
        //     var port = 8332;
        //     switch (builder.Configuration["LNMetricsSettings:Network"])
        //     {
        //         case "mainnet":
        //             port = 8332;
        //             break;
        //         case "signet":
        //             port = 38332;
        //             break;
        //         case "testnet":
        //             port = 28332;
        //             break;
        //         case "regtest":
        //         case "simnet":
        //             port = 18332;
        //             break;
        //     }
        // });


        builder.Services.Configure<List<LNDSettings>>(o =>
        {
            //    o.AddRange(lndSettings);
        });

        builder.Services.Configure<LNDNodePoolConfig>(c =>
        {
            // c.AddNode(new LNDNodeConnection(lndSettings.First()));
            //
            // foreach (var z in lndSettings.Skip(1))
            // {
            //     c.AddConnectionSettings(z);
            // }
            // c.UpdateReadyStatesPeriod(1);
        });

        builder.Services.AddTransient<LNDNodePool>();
        _host = builder.Build();
        var host_running = _host.RunAsync();
        //    await Task.Delay(100);
        //   var f = _host.Services.GetService<LNUnitControllerDbContext>();
        //   _nodePool =  _host.Services.GetService<LNDNodePool>();
        //       f.Database.Migrate();

        var tag = "polar_lnd_0_16_3:latest";
        await _client.CreateDockerImageFromPath("./../../../../Docker/lnd", new List<string> { tag });
        await _client.CreateDockerImageFromPath("./../../../../Docker/bitcoin/27.0rc1", new List<string> { "bitcoin:27.0rc1" });
        await _client.Networks.PruneNetworksAsync(new NetworksDeleteUnusedParameters());
        await RemoveContainer("miner");
        await RemoveContainer("alice");
        await RemoveContainer("bob");
        await RemoveContainer("carol");

        var id = await _client.GetGitlabRunnerNetworkId();
        _LNUnitBuilder = ActivatorUtilities.CreateInstance<LNUnitBuilder>(_host.Services);

        _isGitlab = !id.IsEmpty();
        if (_isGitlab)
        {
            _LNUnitBuilder.Configuration.BaseName = "bridge";
            _LNUnitBuilder.Configuration.DockerNetworkId = "bridge";
        }

        // if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        // {
        //     _LNUnitBuilder.Configuration.BaseName = "bridge";
        //     _LNUnitBuilder.Configuration.DockerNetworkId = "bridge";
        // }

        _LNUnitBuilder.AddBitcoinCoreNode("miner", "bitcoin", "27.0rc1", pullImage: false);
        _LNUnitBuilder.AddPolarLNDNode("alice", new List<LNUnitNetworkDefinition.Channel>
        {
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemotePushOnStart = 5_000_000, // 5MSat
                MinHtlcMsat = 0,
                MaxHtlcMsat = 1_000_000_000_000_000_000,
                TimeLockDelta = 120,
                MaintainLocalBalanceRatio = 0.9,
                RemoteName = "bob"
            }
        }, imageName: "polar_lnd_0_16_3", tagName: "latest", pullImage: false);
        _LNUnitBuilder.AddPolarLNDNode("bob", new List<LNUnitNetworkDefinition.Channel>
        {
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemotePushOnStart = 5_000_000, // 5MSat
                RemoteName = "alice",
                BaseFeeMsat = 1_000, // Absurd 1 sats base fee
                FeeRatePpm = 1_000_000 // and add a 100% fee to that transfer
            },
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemotePushOnStart = 5_000_000, // 5MSat
                RemoteName = "carol",
                BaseFeeMsat = 1_000, // Absurd 1 sats base fee
                FeeRatePpm = 1_000_000 // and add a 100% fee to that transfer
                //   MaxHtlcMsat = 1_000_000_000 //1Msat max amount can flow
            },

            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemotePushOnStart = 1_000_000, // 1MSat
                MaxHtlcMsat = 1000_000, // 1000 sats
                RemoteName = "carol"
            }
        }, imageName: "polar_lnd_0_16_3", tagName: "latest", pullImage: false);
        _LNUnitBuilder.AddPolarLNDNode("carol", new List<LNUnitNetworkDefinition.Channel>
        {
            new()
            {
                ChannelSize = 10_000_000, //10MSat
                RemotePushOnStart = 1_000_000, // 1MSat
                RemoteName = "bob"
            }
        }, imageName: "polar_lnd_0_16_3", tagName: "latest", pullImage: false);
        await _LNUnitBuilder
            .Build(!_isGitlab); // || System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) );

        await _LNUnitBuilder.NewBlock();
        await WaitForNodesReady();
        var graphReady = false;
        while (!graphReady)
        {
            var graph = await _LNUnitBuilder.GetGraphFromAlias("alice");
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


    private async Task WaitForNodesReady()
    {
        var a = _LNUnitBuilder.WaitUntilAliasIsServerReady("alice");
        var b = _LNUnitBuilder.WaitUntilAliasIsServerReady("bob");
        var c = _LNUnitBuilder.WaitUntilAliasIsServerReady("carol");

        Task.WaitAll(a, b, c);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        await _LNUnitBuilder.Destroy(!_isGitlab);
        _LNUnitBuilder.Dispose();
        _client.Dispose();
        await _host.DisposeAsync();
    }

    private async Task RemoveContainer(string name, bool removeLinks = false)
    {
        try
        {
            await _client.Containers.StopContainerAsync(name,
                new ContainerStopParameters { WaitBeforeKillSeconds = 0 });
        }
        catch (Exception e)
        {
            // ignored
        }

        try
        {
            await _client.Containers.RemoveContainerAsync(name,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = true, RemoveLinks = removeLinks });
        }
        catch (Exception e)
        {
            // ignored
        }
    }

    [Test]
    [Category("Payment")]
    [NonParallelizable]
    public async Task FailureNoRouteBecauseFeesAreTooHigh()
    {
        var invoice = await _LNUnitBuilder.GeneratePaymentRequestFromAlias("carol", new Invoice
        {
            Memo = "This path is a trap, better have max_fees set",
            ValueMsat = 10000 //1 satoshi, looks cheap, but is gonna be expensive with the fees from bob -> carol a hidden cost
        });
        var payment = await _LNUnitBuilder.MakeLightningPaymentFromAlias("alice", new SendPaymentRequest
        {
            PaymentRequest = invoice.PaymentRequest,
            FeeLimitMsat = 0, //max of 1 satoshi, we ain't gonna be fee attacked
            TimeoutSeconds = 1
        });
        Assert.That(payment.Status == Payment.Types.PaymentStatus.Failed &&
                    payment.FailureReason == PaymentFailureReason.FailureReasonNoRoute);
    }

    [Test]
    [Category("Payment")]
    [NonParallelizable]
    public async Task Successful()
    {
        var invoice = await _LNUnitBuilder.GeneratePaymentRequestFromAlias("bob", new Invoice
        {
            Memo = "This path is a trap, better have max_fees set",
            ValueMsat = 1000 //1 satoshi, fees will be 0 because it is direct peer
        });
        var payment = await _LNUnitBuilder.MakeLightningPaymentFromAlias("alice", new SendPaymentRequest
        {
            PaymentRequest = invoice.PaymentRequest,
            FeeLimitSat = 100000, //max of 1 satoshi, won't be issue as direct peer so fee is 0
            TimeoutSeconds = 10
        });
        Assert.That(payment.Status == Payment.Types.PaymentStatus.Succeeded);
    }

    [Test]
    [Category("Balancing")]
    [NonParallelizable]
    public async Task PoolRebalance()
    {
        var stats = await _LNUnitBuilder.LNDNodePool.RebalanceNodePool();
        stats.PrintDump();
        //Moves around because I should be resetting tests.
        Assert.That(stats.TotalRebalanceCount >= 2);
        Assert.That(stats.TotalAmount >= 7993060);
        stats = await _LNUnitBuilder.LNDNodePool.RebalanceNodePool();
        Assert.That(stats.TotalRebalanceCount == 0);
        stats.PrintDump();
    }

    // [Test]
    // [Category("Messaging")]
    // [NonParallelizable]
    // public async Task SendPeerMessages()
    // {
    //    var nodes = _LNUnitBuilder.LNDNodePool.ReadyNodes.ToImmutableList();
    //    var handlers = new Dictionary<LNDNodeConnection,LNDCustomMessageHandler>();
    //    foreach (var node in nodes)
    //    {
    //        var h = new LNDCustomMessageHandler(node);
    //        h.OnMessage += (sender, message) =>
    //        {
    //             $"Peer: {Convert.ToHexString( message.Peer.ToByteArray())} Message: {System.Text.Encoding.UTF8.GetString(message.Data.ToByteArray())}".Print();
    //        };
    //        handlers.Add(node,h);
    //    }
    //
    //    await Task.Delay(100);
    //    for (int i = 0; i < 10; i++)
    //    {
    //        await handlers.Last().Value.SendCustomMessageRequest(new CustomMessage()
    //        {
    //            Data = ByteString.CopyFrom(new byte[65533]), 
    //            Peer = ByteString.CopyFrom(Convert.FromHexString(handlers.First().Key.LocalNodePubKey)),
    //            Type = 513
    //        });
    //      
    //    }
    //   
    //    await Task.Delay(1000);
    // }
    //
    [Test]
    [Category("Version")]
    [NonParallelizable]
    public async Task CheckLNDVersion()
    {
        var n = _LNUnitBuilder.LNDNodePool.ReadyNodes.First();
        var info = n.LightningClient.GetInfo(new GetInfoRequest());
        Assert.That(info.Version == "0.17.4-beta commit=v0.17.4-beta");
        info.Version.Print();
    }


    [Test]
    [Category("ChannelAcceptor")]
    [NonParallelizable]
    public async Task ChannelAcceptorDeny()
    {
        var acceptorTasks = new List<LNDChannelAcceptor>();
        foreach (var lnd in _LNUnitBuilder.LNDNodePool.ReadyNodes.ToImmutableList())
        {
            var channelAcceptor =
                new LNDChannelAcceptor(lnd, async req =>
                {
                    var requestingNodePubkey = req.NodePubkey;
                    var isEnoughSats = req.FundingAmt > 5_000_000; //min 5Msat
                    var privateChannel = ((int)req.ChannelFlags & 1) != 1; //figure out flag for unannounced channel
                    var onBlacklist = false; //await CheckBlacklist(req);

                    //Yes / no?
                    if (isEnoughSats && !privateChannel && !onBlacklist && !req.WantsZeroConf)
                        return new ChannelAcceptResponse
                        {
                            Accept = true,
                            ZeroConf = req.WantsZeroConf,
                            MinAcceptDepth = 6,
                            PendingChanId = req.PendingChanId
                        };
                    return new ChannelAcceptResponse
                    {
                        Accept = false,
                        Error =
                            "Sorry, node does not match ZBD peering requirements. Please reach out to support for more information.",
                        PendingChanId = req.PendingChanId
                    };
                });

            acceptorTasks.Add(channelAcceptor);
        }

        var alice = _LNUnitBuilder.GetNodeFromAlias("alice");
        var bob = _LNUnitBuilder.GetNodeFromAlias("bob");
        //Fail Private
        Assert.CatchAsync<RpcException>(async () =>
        {
            var channelPoint = await alice.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
            {
                NodePubkey = ByteString.CopyFrom(Convert.FromHexString(bob.LocalNodePubKey)),
                SatPerVbyte = 10,
                LocalFundingAmount = 10_000_000,
                PushSat = 100,
                Private = true
            });
        });
        //Fail too small
        Assert.CatchAsync<RpcException>(async () =>
        {
            var channelPoint = await alice.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
            {
                NodePubkey = ByteString.CopyFrom(Convert.FromHexString(bob.LocalNodePubKey)),
                SatPerVbyte = 10,
                LocalFundingAmount = 1_000_000,
                PushSat = 100,
                Private = true
            });
        });
        //works public, large enough
        var channelPoint = await alice.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
        {
            NodePubkey = ByteString.CopyFrom(Convert.FromHexString(bob.LocalNodePubKey)),
            SatPerVbyte = 10,
            LocalFundingAmount = 10_000_000,
            PushSat = 100
            //       Private = true,
        });


        //Move things along so it is confirmed
        await _LNUnitBuilder.NewBlock(10);

        //Close
        var close = alice.LightningClient.CloseChannel(new
            CloseChannelRequest
        {
            ChannelPoint = channelPoint,
            SatPerVbyte = 10
        });
        //Move things along so it is confirmed
        await _LNUnitBuilder.NewBlock(10);
        acceptorTasks.ForEach(x => x.Dispose());
    }


    [Test]
    [Category("Fees")]
    [NonParallelizable]
    public async Task UpdateChannelPolicyPerNode()
    {
        var acceptorTasks = new List<LNDChannelAcceptor>();
        foreach (var lnd in _LNUnitBuilder.LNDNodePool.ReadyNodes.ToImmutableList())
        {
            var channelsResponse = await lnd.LightningClient.ListChannelsAsync(new ListChannelsRequest
            {
                ActiveOnly = true
            });
            foreach (var chan in channelsResponse.Channels)
            {
                var cp = chan.ChannelPoint.SplitOnFirst(":");
                var chanDetail = await lnd.LightningClient.GetChanInfoAsync(new ChanInfoRequest
                {
                    ChanId = chan.ChanId
                });
                var timeLockDelta = chanDetail.Node1Pub == lnd.LocalNodePubKey
                    ? chanDetail.Node1Policy.TimeLockDelta
                    : chanDetail.Node2Policy.TimeLockDelta;
                var maxHtlcMsat = chanDetail.Node1Pub == lnd.LocalNodePubKey
                    ? chanDetail.Node1Policy.MaxHtlcMsat
                    : chanDetail.Node2Policy.MaxHtlcMsat;
                var result = await lnd.LightningClient.UpdateChannelPolicyAsync(new PolicyUpdateRequest
                {
                    ChanPoint = new ChannelPoint
                    {
                        FundingTxidStr = cp.First(),
                        OutputIndex = cp.Last().ConvertTo<uint>()
                    },
                    // BaseFeeMsat = 1000,
                    // FeeRatePpm = 5000,
                    // MaxHtlcMsat = 100_000_000_000,
                    // MinHtlcMsat = 1_000,
                    TimeLockDelta = timeLockDelta + 1,
                    MinHtlcMsatSpecified = false
                });
                await Task.Delay(1000);
                chanDetail = await lnd.LightningClient.GetChanInfoAsync(new ChanInfoRequest
                {
                    ChanId = chan.ChanId
                });
                var timeLockDeltaNew = chanDetail.Node1Pub == lnd.LocalNodePubKey
                    ? chanDetail.Node1Policy.TimeLockDelta
                    : chanDetail.Node2Policy.TimeLockDelta;
                var maxHtlcMsatNew = chanDetail.Node1Pub == lnd.LocalNodePubKey
                    ? chanDetail.Node1Policy.MaxHtlcMsat
                    : chanDetail.Node2Policy.MaxHtlcMsat;
                Assert.That(timeLockDeltaNew != timeLockDelta);
                Assert.That(maxHtlcMsatNew == maxHtlcMsat);
            }
        }
    }


    [Test]
    [Category("Payment")]
    [NonParallelizable]
    public async Task SuccessfulKeysend()
    {
        var aliceSettings = await _LNUnitBuilder.GetLNDSettingsFromContainer("alice");
        var bobSettings = await _LNUnitBuilder.GetLNDSettingsFromContainer("bob");
        var alice = new LNDNodeConnection(aliceSettings);
        var bob = new LNDNodeConnection(bobSettings);
        var payment = await alice.KeysendPayment(bob.LocalNodePubKey, 10, 100000000, "Hello World", 10,
            new Dictionary<ulong, byte[]> { { 99999, new byte[] { 11, 11, 11 } } });

        Assert.That(payment.Status == Payment.Types.PaymentStatus.Succeeded);
    }

    [Test]
    [Category("LNUnit")]
    [NonParallelizable]
    public async Task ExportGraph()
    {
        var data = await _LNUnitBuilder.GetGraphFromAlias("alice");
        data.PrintDump();
        Assert.That(data.Nodes.Count == 3);
    }

    [Test]
    [Category("LNUnit")]
    [NonParallelizable]
    public async Task GetChannelsFromAlias()
    {
        var alice = await _LNUnitBuilder.GetChannelsFromAlias("alice");
        var bob = await _LNUnitBuilder.GetChannelsFromAlias("bob");
        var carol = await _LNUnitBuilder.GetChannelsFromAlias("carol");
        Assert.That(alice.Channels.Any());
        "Alice".Print();
        alice.Channels.PrintDump();
        Assert.That(bob.Channels.Any());
        "Bob".Print();
        bob.Channels.PrintDump();
        Assert.That(carol.Channels.Any());
        "Carol".Print();
        carol.Channels.PrintDump();
    }

    [Test]
    [Category("LNUnit")]
    [NonParallelizable]
    public async Task GetChannelPointFromAliases()
    {
        var data = _LNUnitBuilder.GetChannelPointFromAliases("alice", "bob");
        data.PrintDump();

        Assert.That(data.First().FundingTxidCase != ChannelPoint.FundingTxidOneofCase.None);
    }

    [Test]
    [Category("LNDNodePool")]
    [NonParallelizable]
    public async Task GetNodeConnectionFromPool()
    {
        var data = _LNUnitBuilder.LNDNodePool.GetLNDNodeConnection();
        Assert.That(data.IsRPCReady);
        Assert.That(data.IsServerReady);
        var found = _LNUnitBuilder.LNDNodePool.GetLNDNodeConnection(data.LocalNodePubKey);
        Assert.That(data.LocalNodePubKey == found.LocalNodePubKey);
        var notFound = _LNUnitBuilder.LNDNodePool.GetLNDNodeConnection("not valid");
        Assert.That(notFound == null);
    }

    [Test]
    [Category("Fees")]
    [NonParallelizable]
    public async Task UpdateChannelPolicy()
    {
        var data = _LNUnitBuilder.UpdateGlobalFeePolicyOnAlias("alice", new LNUnitNetworkDefinition.Channel());
        data.PrintDump();
        Assert.That(data.FailedUpdates.Count == 0);
    }

    [Test]
    [Category("Payment")]
    [Category("Invoice")]
    [NonParallelizable]
    public async Task FailureInvoiceTimeout()
    {
        var invoice = await _LNUnitBuilder.GeneratePaymentRequestFromAlias("alice", new Invoice
        {
            Memo = "Things are too slow, never gonna work",
            Expiry = 1,
            ValueMsat = 1003 //1 satoshi, fees will be 0 because it is direct peer,
        });
        //Apply HTLC hold to prevent payment from settling
        await _LNUnitBuilder.DelayAllHTLCsOnAlias("alice", 2_000); //6s
        await _LNUnitBuilder.DelayAllHTLCsOnAlias("bob", 2_000); //6s
        await _LNUnitBuilder.DelayAllHTLCsOnAlias("carol", 2_000); //6s

        await Task.Delay(1000);
        var failed = false;
        try
        {
            var payment = await _LNUnitBuilder.MakeLightningPaymentFromAlias("carol", new SendPaymentRequest
            {
                PaymentRequest = invoice.PaymentRequest,
                FeeLimitSat = 100000000, //max of 1 satoshi, won't be issue as direct peer so fee is 0
                TimeoutSeconds = 1
            });
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Unknown && e.Status.Detail.Contains("invoice expired."))
        {
            failed = true;
        }

        Assert.That(failed);
        _LNUnitBuilder.CancelInterceptorOnAlias("alice");
        _LNUnitBuilder.CancelInterceptorOnAlias("bob");
        _LNUnitBuilder.CancelInterceptorOnAlias("carol");
        var invoiceLookup = await _LNUnitBuilder.LookupInvoice("alice", invoice.RHash);
        Assert.That(invoiceLookup != null);
        Assert.That(invoiceLookup.RHash == invoice.RHash);
        Assert.That(invoiceLookup.State == Invoice.Types.InvoiceState.Canceled);
    }

    [Test]
    [Category("Payment")]
    [Category("Interceptor")]
    [NonParallelizable]
    public async Task FailureAtNextHopUnknownNextPeer()
    {
        var invoice = await _LNUnitBuilder.GeneratePaymentRequestFromAlias("carol", new Invoice
        {
            Memo = "This path is a trap, better have max_fees set",
            ValueMsat = 10004,
            Expiry = 60 * 60 * 24
        });
        invoice.PrintDump();
        //Apply HTLC hold to prevent payment from settling
        await _LNUnitBuilder.DelayAllHTLCsOnAlias("bob", 600_000); //600s

        var paymentTask = _LNUnitBuilder.MakeLightningPaymentFromAlias("alice", new SendPaymentRequest
        {
            PaymentRequest = invoice.PaymentRequest,
            FeeLimitMsat = 10000000000000,
            TimeoutSeconds = 100,
            NoInflightUpdates = true
        });

        "Carol shutdown".Print();
        await _LNUnitBuilder.ShutdownByAlias("carol", 0);
        await Task.Delay(1000);
        paymentTask.Status.PrintDump();
        _LNUnitBuilder.CancelInterceptorOnAlias("bob");
        paymentTask.Status.PrintDump();
        var payment = await paymentTask;
        await _LNUnitBuilder.RestartByAlias("carol", 0, true);
        await _LNUnitBuilder.WaitUntilAliasIsServerReady("carol");
        payment.PrintDump();
        paymentTask.Dispose();
        Assert.That(payment != null && payment.Status == Payment.Types.PaymentStatus.Failed);
        Assert.That(payment.Htlcs.Last().Failure.Code == Failure.Types.FailureCode.UnknownNextPeer);
    }


    [Test]
    [Category("Payment")]
    [Category("Interceptor")]
    [NonParallelizable]
    public async Task InterceptorTest()
    {
        List<AddInvoiceResponse> invoices = new();
        for (var i = 0; i < 10; i++)
        {
            var invoice = await _LNUnitBuilder.GeneratePaymentRequestFromAlias("alice", new Invoice
            {
                Memo = "This path is a trap, better have max_fees set",
                ValueMsat = 1000,
                Expiry = 60 * 60 * 24
            });
            invoices.Add(invoice);
        }

        //Apply HTLC hold to prevent payment from settling
        var htlcEvents = 0;
        List<LNDHTLCMonitor> monitors = new();
        foreach (var n in _LNUnitBuilder.LNDNodePool.ReadyNodes.ToImmutableList())
            //This disposes so should use Clone of connection
            monitors.Add(new LNDHTLCMonitor(n.Clone(), htlc => { htlcEvents++; }));


        await _LNUnitBuilder.DelayAllHTLCsOnAlias("alice", 1);
        await _LNUnitBuilder.DelayAllHTLCsOnAlias("bob", 1);
        await _LNUnitBuilder.DelayAllHTLCsOnAlias("carol", 1);
        await Task.Delay(1000);
        Assert.That(await _LNUnitBuilder.IsInterceptorActiveForAlias("alice"));
        Assert.That((await _LNUnitBuilder.GetInterceptor("bob")).InterceptCount == 0);
        Assert.That(await _LNUnitBuilder.IsInterceptorActiveForAlias("bob"));
        Assert.That(await _LNUnitBuilder.IsInterceptorActiveForAlias("carol"));
        await Task.Delay(5000);
        var ii = 0;
        foreach (var invoice in invoices)
        {
            ii++;
            ii.PrintDump();
            var payment = await _LNUnitBuilder.MakeLightningPaymentFromAlias("carol", new SendPaymentRequest
            {
                PaymentRequest = invoice.PaymentRequest,
                FeeLimitMsat = 1000000000000000, //plenty of money
                TimeoutSeconds = 20000,
                NoInflightUpdates = true
            });
            payment.PrintDump();
            Assert.That(payment.Status == Payment.Types.PaymentStatus.Succeeded);
        }

        Assert.That(await _LNUnitBuilder.IsInterceptorActiveForAlias("alice"), "alice not intercepting");
        Assert.That((await _LNUnitBuilder.GetInterceptor("bob")).InterceptCount >= 10, "bob not intercepting");
        Assert.That(await _LNUnitBuilder.IsInterceptorActiveForAlias("bob"), "Bob not intercepting");
        Assert.That(await _LNUnitBuilder.IsInterceptorActiveForAlias("carol"), "carol not intercepting");
        Assert.That(htlcEvents > 10); //just what it spit out didn't do math for that
    }

    [Test]
    [Category("Payment")]
    [Category("Interceptor")]
    [NonParallelizable]
    public async Task GetPaymentFailureData()
    {
        //Setup


        var invoice = await _LNUnitBuilder.GeneratePaymentRequestFromAlias("carol", new Invoice
        {
            Memo = "This path is a trap, better have max_fees set",
            ValueMsat = 10004, //1 satoshi, fees will be 0 because it is direct peer,
            Expiry = 60 * 60 * 24
        });
        invoice.PrintDump();
        //Apply HTLC hold to prevent payment from settling
        await _LNUnitBuilder.DelayAllHTLCsOnAlias("bob", 600_000); //600s

        var paymentTask = _LNUnitBuilder.MakeLightningPaymentFromAlias("alice", new SendPaymentRequest
        {
            PaymentRequest = invoice.PaymentRequest,
            FeeLimitMsat = 10000000000000, //max of 1 satoshi, won't be issue as direct peer so fee is 0
            TimeoutSeconds = 100,
            NoInflightUpdates = true
        });

        "Carol shutdown".Print();
        await _LNUnitBuilder.ShutdownByAlias("carol", 0);
        await Task.Delay(1000);
        paymentTask.Status.PrintDump();
        _LNUnitBuilder.CancelInterceptorOnAlias("bob");
        paymentTask.Status.PrintDump();
        var payment = await paymentTask;
        await _LNUnitBuilder.RestartByAlias("carol", 0, true);
        await _LNUnitBuilder.WaitUntilAliasIsServerReady("carol");
        payment.PrintDump();
        paymentTask.Dispose();
        Assert.That(payment != null && payment.Status == Payment.Types.PaymentStatus.Failed);
        // Assert.That(payment.Htlcs.Last().Failure.Code == Failure.Types.FailureCode.UnknownNextPeer);

        //Check
        var res = new List<PaymentStats>();

        var consolidated = new Dictionary<string, (int Success, int Fail)>();
        var client = (await _LNUnitBuilder.GetLNDSettingsFromContainer("alice")).GetClient();
        var payments = await client.LightningClient.ListPaymentsAsync(new ListPaymentsRequest
        {
            //CountTotalPayments = true,
            CreationDateEnd = (ulong)DateTime.UtcNow.ToUnixTime(),
            CreationDateStart = (ulong)DateTime.UtcNow.AddDays(-1).ToUnixTime(),
            MaxPayments = 1000,
            IndexOffset = 0,
            IncludeIncomplete = true // this included failures per docs
        });
        var finalized = new List<Payment.Types.PaymentStatus>
            {Payment.Types.PaymentStatus.Failed};
        await foreach (var p in payments.Payments.Where(x => finalized.Contains(x.Status) && x.Htlcs.Any()))
        {
            var destinationNode = p.Htlcs.Last().Route.Hops.Last().PubKey;

            if (p.FailureReason == PaymentFailureReason.FailureReasonNone)
                //Success
                await Record(true, p, client);
            else
                await Record(false, p, client);
        }

        async Task Record(bool success, Payment p, LNDNodeConnection c)
        {
            var finalPath = p.Htlcs.Last().Route.Hops;
            var pubKey = finalPath.Last().PubKey;

            if (res.Exists(x => x.Pubkey == pubKey)) //(consolidated.ContainsKey(pubKey))
            {
                var r = res.Single(x => x.Pubkey == pubKey); // consolidated[pubKey];
                r.FailedAttempts = p.Htlcs.Count() - 1;
                r.MinHops = Math.Min(r.MinHops, finalPath.Count());
                r.MaxHops = Math.Max(r.MaxHops, finalPath.Count());
                if (success)
                    r.Success++;
                else
                    r.Fail++;
            }
            else
            {
                res.Add(new PaymentStats
                {
                    Pubkey = pubKey,
                    Alias = await ToAlias(c, pubKey),
                    Fail = success ? 0 : 1,
                    Success = success ? 1 : 0,
                    FailedAttempts = p.Htlcs.Count() - 1,
                    MinHops = finalPath.Count(),
                    MaxHops = finalPath.Count()
                }); // consolidated.Add(pubKey,(success ? 1 : 0, success ? 0 : 1));
            }
        }
    }

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

    public class PaymentStats

    {
        public string Alias { get; set; }
        public string Pubkey { get; set; }

        public long MaxHops { get; set; }

        public long MinHops { get; set; }

        public long FailedAttempts { get; set; }

        public long Success { get; set; }
        public long Fail { get; set; }
    }
}