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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Routerrpc;
using Serilog;
using ServiceStack;
using ServiceStack.Text;
using Assert = NUnit.Framework.Assert;

namespace LNUnit.Test.Fixtures;

[TestFixture("postgres", "custom_lnd", "latest", "/home/lnd/.lnd", false)]
[TestFixture("boltdb", "custom_lnd", "latest", "/home/lnd/.lnd", false)]
[TestFixture("postgres", "lightninglabs/lnd", "daily-testing-only", "/root/.lnd", true)]
[TestFixture("boltdb", "lightninglabs/lnd", "daily-testing-only", "/root/.lnd", true)]
public class AbcLightningFixture : IDisposable
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

        await _client.CreateDockerImageFromPath("../../../../Docker/lnd", ["custom_lnd", "custom_lnd:latest"]);
        await SetupNetwork(_lndImage, _tag, _lndRoot, _pullImage);
    }

    public AbcLightningFixture(string dbType,
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

    public string DbContainerName { get; set; } = "postgres";
    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();

    private ServiceProvider _serviceProvider;

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        // Remove containers
        _client.RemoveContainer("miner").Wait();
        _client.RemoveContainer("alice").Wait();
        _client.RemoveContainer("bob").Wait();
        _client.RemoveContainer("carol").Wait();

        Builder?.Destroy();
        Builder?.Dispose();
        _client.Dispose();
    }

    public LNUnitBuilder? Builder { get; private set; }


    public async Task SetupNetwork(string image = "lightninglabs/lnd", string tag = "daily-testing-only",
        string lndRoot = "/root/.lnd", bool pullImage = false)
    {
        await _client.RemoveContainer("miner");
        await _client.RemoveContainer("alice");
        await _client.RemoveContainer("bob");
        await _client.RemoveContainer("carol");

        Builder.AddBitcoinCoreNode();

        if (pullImage) await _client.PullImageAndWaitForCompleted(image, tag);


        Builder.AddPolarLNDNode("alice",
            [
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemoteName = "bob"
                }
            ], imageName: image, tagName: tag, pullImage: false, acceptKeysend: true,
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["alice"] : null);

        Builder.AddPolarLNDNode("bob",
            [
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemotePushOnStart = 1_000_000, // 1MSat
                    RemoteName = "alice"
                }
            ], imageName: image, tagName: tag, pullImage: false, acceptKeysend: true,
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["bob"] : null);

        Builder.AddPolarLNDNode("carol",
            [
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemotePushOnStart = 1_000_000, // 1MSat
                    RemoteName = "bob"
                }
            ], imageName: image, tagName: tag, pullImage: false, acceptKeysend: true,
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["carol"] : null);

        await Builder.Build(lndRoot: lndRoot);

        WaitNodesReady();
        await WaitGraphReady();
    }

    private void WaitNodesReady()
    {
        var a = Builder.WaitUntilAliasIsServerReady("alice");
        var b = Builder.WaitUntilAliasIsServerReady("bob");
        var c = Builder.WaitUntilAliasIsServerReady("carol");
        Task.WaitAll(a, b, c);
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


    ///////
    /// ///
    [Test]
    [Category("Payment")]
    [NonParallelizable]
    [Timeout(5000)]

    public async Task FailureNoRouteBecauseFeesAreTooHigh()
    {
        var invoice = await Builder.GeneratePaymentRequestFromAlias("carol", new Invoice
        {
            Memo = "This path is a trap, better have max_fees set",
            ValueMsat = 10000 //1 satoshi, looks cheap, but is gonna be expensive with the fees from bob -> carol a hidden cost
        });
        var payment = await Builder.MakeLightningPaymentFromAlias("alice", new SendPaymentRequest
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
    [Timeout(5000)]
    public async Task Successful()
    {
        var invoice = await Builder.GeneratePaymentRequestFromAlias("bob", new Invoice
        {
            Memo = "This path is a trap, better have max_fees set",
            ValueMsat = 1000 //1 satoshi, fees will be 0 because it is direct peer
        });
        var payment = await Builder.MakeLightningPaymentFromAlias("alice", new SendPaymentRequest
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
    [Timeout(5000)]
    public async Task PoolRebalance()
    {
        var stats = await Builder.LNDNodePool.RebalanceNodePool();
        stats.PrintDump();
        //Moves around because I should be resetting tests.
        Assert.That(stats.TotalRebalanceCount >= 2);
        Assert.That(stats.TotalAmount >= 7993060);
        stats = await Builder.LNDNodePool.RebalanceNodePool();
        Assert.That(stats.TotalRebalanceCount == 0);
        stats.PrintDump();
    }


    [Test]
    [Timeout(2000)]
    [Category("Version")]
    [NonParallelizable]
    public async Task CheckLNDVersion()
    {
        var n = Builder.LNDNodePool.ReadyNodes.First();
        var info = n.LightningClient.GetInfo(new GetInfoRequest());
        info.Version.Print();
    }


    [Test]
    [Category("ChannelAcceptor")]
    [Timeout(5000)]
    [NonParallelizable]
    public async Task ChannelAcceptorDeny()
    {
        var acceptorTasks = new List<LNDChannelAcceptor>();
        foreach (var lnd in Builder.LNDNodePool.ReadyNodes.ToImmutableList())
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

        var alice = Builder.GetNodeFromAlias("alice");
        var bob = Builder.GetNodeFromAlias("bob");
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
        await Builder.NewBlock(10);

        //Close
        var close = alice.LightningClient.CloseChannel(new
            CloseChannelRequest
        {
            ChannelPoint = channelPoint,
            SatPerVbyte = 10
        });
        //Move things along so it is confirmed
        await Builder.NewBlock(10);
        acceptorTasks.ForEach(x => x.Dispose());
    }


    [Test]
    [Category("Fees")]
    [NonParallelizable]
    [Timeout(60000)]
    public async Task UpdateChannelPolicyPerNode()
    {
        var acceptorTasks = new List<LNDChannelAcceptor>();
        foreach (var lnd in Builder.LNDNodePool.ReadyNodes.ToImmutableList())
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
    [Timeout(5000)]
    public async Task SuccessfulKeysend()
    {
        Builder.CancelAllInterceptors();
        var aliceSettings = await Builder.GetLNDSettingsFromContainer("alice", _lndRoot);
        var bobSettings = await Builder.GetLNDSettingsFromContainer("bob", _lndRoot);
        var alice = new LNDNodeConnection(aliceSettings);
        var bob = new LNDNodeConnection(bobSettings);
        // WaitNodesReady();
        // await WaitGraphReady("bob");
        // var channels = bob.LightningClient.ListChannels(new ListChannelsRequest() { ActiveOnly = true });
        // while (!channels.Channels.Any(x => x.RemotePubkey.EqualsIgnoreCase(alice.LocalNodePubKey) && x.Active))
        // {
        //     Debug.Print("Channel not up yet.");
        //     await Task.Delay(250);
        //     channels = bob.LightningClient.ListChannels(new ListChannelsRequest() { ActiveOnly = true });
        // }
        await Task.Delay(1000); //TODO: why? we are not checking channels are up
        var payment = await alice.KeysendPayment(bob.LocalNodePubKey, 10, 100000000, "Hello World", 10,
            new Dictionary<ulong, byte[]> { { 99999, new byte[] { 11, 11, 11 } } });

        Assert.That(payment.Status, Is.EqualTo(Payment.Types.PaymentStatus.Succeeded));
    }

    [Test]
    [Category("LNUnit")]
    [NonParallelizable]
    [Timeout(5000)]
    public async Task ExportGraph()
    {
        var data = await Builder.GetGraphFromAlias("alice");
        data.PrintDump();
        Assert.That(data.Nodes.Count == 3);
    }

    [Test]
    [Category("LNUnit")]
    [NonParallelizable]
    [Timeout(1000)]
    public async Task GetChannelsFromAlias()
    {
        var alice = await Builder.GetChannelsFromAlias("alice");
        var bob = await Builder.GetChannelsFromAlias("bob");
        var carol = await Builder.GetChannelsFromAlias("carol");
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
    [Timeout(5000)]

    public async Task GetChannelPointFromAliases()
    {
        var data = Builder.GetChannelPointFromAliases("alice", "bob");
        data.PrintDump();

        Assert.That(data.First().FundingTxidCase != ChannelPoint.FundingTxidOneofCase.None);
    }

    [Test]
    [Category("LNDNodePool")]
    [NonParallelizable]
    [Timeout(1000)]
    public async Task GetNodeConnectionFromPool()
    {
        var data = Builder.LNDNodePool.GetLNDNodeConnection();
        Assert.That(data.IsRPCReady);
        Assert.That(data.IsServerReady);
        var found = Builder.LNDNodePool.GetLNDNodeConnection(data.LocalNodePubKey);
        Assert.That(data.LocalNodePubKey == found.LocalNodePubKey);
        var notFound = Builder.LNDNodePool.GetLNDNodeConnection("not valid");
        Assert.That(notFound == null);
    }

    [Test]
    [Category("Fees")]
    [NonParallelizable]
    [Timeout(5000)]
    public async Task UpdateChannelPolicy()
    {
        var data = Builder.UpdateGlobalFeePolicyOnAlias("alice", new LNUnitNetworkDefinition.Channel());
        data.PrintDump();
        Assert.That(data.FailedUpdates.Count == 0);
    }

    [Test]
    [Timeout(10000)]
    public async Task SendMany_Onchain()
    {
        var alice = Builder.GetNodeFromAlias("alice");
        var addresses = new List<string>();
        var sendManyRequest = new SendManyRequest()
        {
            SatPerVbyte = 10,
            Label = "Test send to multiple",
            SpendUnconfirmed = true,
        };
        //make destinations
        for (int i = 0; i < 10; i++)
        {
            var address = alice.LightningClient.NewAddress(new NewAddressRequest() { Type = AddressType.TaprootPubkey }).Address;
            addresses.Add(address);
            sendManyRequest.AddrToAmount.Add(address, 10000);
        }

        alice.LightningClient.SendMany(sendManyRequest);

        Builder.NewBlock(10); //fast forward in time

        //verify last address got funds
        var unspend = alice.LightningClient.ListUnspent(new ListUnspentRequest() { });
        var confirmedAddresses = new List<string>();
        foreach (var u in unspend.Utxos)
        {
            var exists = addresses.FirstOrDefault(x => x.EqualsIgnoreCase(u.Address));
            if (exists != null)
            {
                Assert.That(u.AmountSat, Is.EqualTo(10_000));
                confirmedAddresses.Add(exists);
            }
        }
        Assert.That(addresses.Count, Is.EqualTo(confirmedAddresses.Count), "Confirmed deposits doesn't match request.");
    }

    [Test]
    [Category("Payment")]
    [Category("Invoice")]
    [NonParallelizable]
    [Timeout(5000)]

    public async Task FailureInvoiceTimeout()
    {
        var invoice = await Builder.GeneratePaymentRequestFromAlias("alice", new Invoice
        {
            Memo = "Things are too slow, never gonna work",
            Expiry = 1,
            ValueMsat = 1003 //1 satoshi, fees will be 0 because it is direct peer,
        });
        //Apply HTLC hold to prevent payment from settling
        await Builder.DelayAllHTLCsOnAlias("alice", 2_000); //6s
        await Builder.DelayAllHTLCsOnAlias("bob", 2_000); //6s
        await Builder.DelayAllHTLCsOnAlias("carol", 2_000); //6s

        await Task.Delay(1000);
        var failed = false;
        try
        {
            var payment = await Builder.MakeLightningPaymentFromAlias("carol", new SendPaymentRequest
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
        Builder.CancelInterceptorOnAlias("alice");
        Builder.CancelInterceptorOnAlias("bob");
        Builder.CancelInterceptorOnAlias("carol");
        var invoiceLookup = await Builder.LookupInvoice("alice", invoice.RHash);
        Assert.That(invoiceLookup != null);
        Assert.That(invoiceLookup.RHash == invoice.RHash);
        Assert.That(invoiceLookup.State == Invoice.Types.InvoiceState.Canceled);
    }

    [Test]
    [Category("Payment")]
    [Category("Interceptor")]
    [NonParallelizable]
    [Timeout(15000)]
    public async Task FailureReasonNoRoute()
    {
        var invoice = await Builder.GeneratePaymentRequestFromAlias("carol", new Invoice
        {
            Memo = "This path is a trap, better have max_fees set",
            ValueMsat = 10004,
            Expiry = 60 * 60 * 24
        });
        invoice.PrintDump();
        //Apply HTLC hold to prevent payment from settling
        await Builder.DelayAllHTLCsOnAlias("bob", 600_000); //600s

        var paymentTask = Builder.MakeLightningPaymentFromAlias("alice", new SendPaymentRequest
        {
            PaymentRequest = invoice.PaymentRequest,
            FeeLimitMsat = 10000000000000,
            TimeoutSeconds = 100,
            NoInflightUpdates = true
        });

        "Carol shutdown".Print();
        await Builder.ShutdownByAlias("carol", 0, true);
        await Task.Delay(1000);
        paymentTask.Status.PrintDump();
        Builder.CancelInterceptorOnAlias("bob");
        paymentTask.Status.PrintDump();
        var payment = await paymentTask;
        await Builder.RestartByAlias("carol", 0, true, lndRoot: _lndRoot);
        await Builder.WaitUntilAliasIsServerReady("carol");
        payment.PrintDump();
        paymentTask.Dispose();
        Assert.That(payment != null && payment.Status == Payment.Types.PaymentStatus.Failed);
        Assert.That(payment != null && payment.FailureReason == PaymentFailureReason.FailureReasonNoRoute);
    }


    [Test]
    [Category("Payment")]
    [Category("Interceptor")]
    [NonParallelizable]
    [Timeout(30000)]
    public async Task InterceptorTest()
    {
        List<AddInvoiceResponse> invoices = new();
        for (var i = 0; i < 10; i++)
        {
            var invoice = await Builder.GeneratePaymentRequestFromAlias("alice", new Invoice
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
        foreach (var n in Builder.LNDNodePool.ReadyNodes.ToImmutableList())
            //This disposes so should use Clone of connection
            monitors.Add(new LNDHTLCMonitor(n.Clone(), htlc => { htlcEvents++; }));


        await Builder.DelayAllHTLCsOnAlias("alice", 1);
        await Builder.DelayAllHTLCsOnAlias("bob", 1);
        await Builder.DelayAllHTLCsOnAlias("carol", 1);
        await Task.Delay(1000);
        Assert.That(await Builder.IsInterceptorActiveForAlias("alice"));
        Assert.That((await Builder.GetInterceptor("bob")).InterceptCount == 0);
        Assert.That(await Builder.IsInterceptorActiveForAlias("bob"));
        Assert.That(await Builder.IsInterceptorActiveForAlias("carol"));
        await Task.Delay(5000);
        var ii = 0;
        foreach (var invoice in invoices)
        {
            ii++;
            ii.PrintDump();
            var payment = await Builder.MakeLightningPaymentFromAlias("carol", new SendPaymentRequest
            {
                PaymentRequest = invoice.PaymentRequest,
                FeeLimitMsat = 1000000000000000, //plenty of money
                TimeoutSeconds = 20000,
                NoInflightUpdates = true
            });
            payment.PrintDump();
            Assert.That(payment.Status == Payment.Types.PaymentStatus.Succeeded);
        }

        Assert.That(await Builder.IsInterceptorActiveForAlias("alice"), "alice not intercepting");
        Assert.That((await Builder.GetInterceptor("bob")).InterceptCount >= 10, "bob not intercepting");
        Assert.That(await Builder.IsInterceptorActiveForAlias("bob"), "Bob not intercepting");
        Assert.That(await Builder.IsInterceptorActiveForAlias("carol"), "carol not intercepting");
        Assert.That(htlcEvents > 10); //just what it spit out didn't do math for that
    }

    [Test]
    [Category("Payment")]
    [Category("Interceptor")]
    [NonParallelizable]
    [Timeout(15000)]
    public async Task GetPaymentFailureData()
    {
        //Setup


        var invoice = await Builder.GeneratePaymentRequestFromAlias("carol", new Invoice
        {
            Memo = "This path is a trap, better have max_fees set",
            ValueMsat = 10004, //1 satoshi, fees will be 0 because it is direct peer,
            Expiry = 60 * 60 * 24
        });
        invoice.PrintDump();
        //Apply HTLC hold to prevent payment from settling
        await Builder.DelayAllHTLCsOnAlias("bob", 600_000); //600s

        var paymentTask = Builder.MakeLightningPaymentFromAlias("alice", new SendPaymentRequest
        {
            PaymentRequest = invoice.PaymentRequest,
            FeeLimitMsat = 10000000000000, //max of 1 satoshi, won't be issue as direct peer so fee is 0
            TimeoutSeconds = 100,
            NoInflightUpdates = true
        });

        "Carol shutdown".Print();
        await Builder.ShutdownByAlias("carol", 0, true);
        await Task.Delay(1000);
        paymentTask.Status.PrintDump();
        Builder.CancelInterceptorOnAlias("bob");
        paymentTask.Status.PrintDump();
        var payment = await paymentTask;
        await Builder.RestartByAlias("carol", 0, true, lndRoot: _lndRoot);
        await Builder.WaitUntilAliasIsServerReady("carol");
        payment.PrintDump();
        paymentTask.Dispose();
        Assert.That(payment != null && payment.Status == Payment.Types.PaymentStatus.Failed);
        // Assert.That(payment.Htlcs.Last().Failure.Code == Failure.Types.FailureCode.UnknownNextPeer);

        //Check
        var res = new List<PaymentStats>();

        var consolidated = new Dictionary<string, (int Success, int Fail)>();
        var client = (await Builder.GetLNDSettingsFromContainer("alice", _lndRoot)).GetClient();
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
            { Payment.Types.PaymentStatus.Failed };
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

    private readonly MemoryCache _aliasCache = new(new MemoryCacheOptions { SizeLimit = 10000 });
    private readonly string _dbType;
    private readonly string _lndImage;
    private readonly string _tag;
    private readonly string _lndRoot;
    private readonly bool _pullImage;

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

    [Test]
    [Category("Payment")]
    [Category("Invoice")]
    [Category("Sync")]
    [NonParallelizable]
    [Timeout(15000)]
    public async Task ListInvoiceAndPaymentPaging()
    {
        var invoices = await Builder.GeneratePaymentsRequestFromAlias("alice", 10, new Invoice
        {
            Memo = "Things are too slow, never gonna work",
            Expiry = 100,
            ValueMsat = 1003 //1 satoshi, fees will be 0 because it is direct peer,
        });

        var alice = await Builder.WaitUntilAliasIsServerReady("alice");
        var bob = await Builder.WaitUntilAliasIsServerReady("bob");

        //purge data
        await bob.LightningClient.DeleteAllPaymentsAsync(new DeleteAllPaymentsRequest());

        foreach (var invoice in invoices)
        {
            var payment = await Builder.MakeLightningPaymentFromAlias("bob", new SendPaymentRequest
            {
                PaymentRequest = invoice.PaymentRequest,
                FeeLimitSat = 100000000,
                TimeoutSeconds = 50
            });
            Assert.That(payment.Status == Payment.Types.PaymentStatus.Succeeded);
        }


        await Task.Delay(2000);

        var lp = await bob.LightningClient.ListPaymentsAsync(new ListPaymentsRequest
        {
            CreationDateStart = (ulong)DateTime.UtcNow.Subtract(TimeSpan.FromDays(1)).ToUnixTime(),
            CreationDateEnd = (ulong)DateTime.UtcNow.AddDays(1).ToUnixTime(),
            IncludeIncomplete = false,
            Reversed = true,
            MaxPayments = 10
        });
        Assert.That(lp.Payments.Count == 10);
        await Task.Delay(200);

        var li = await alice.LightningClient.ListInvoicesAsync(new ListInvoiceRequest
        {
            CreationDateStart = (ulong)DateTime.UtcNow.Subtract(TimeSpan.FromDays(1)).ToUnixTime(),
            CreationDateEnd = (ulong)DateTime.UtcNow.AddDays(1).ToUnixTime(),
            NumMaxInvoices = 10,
            Reversed = true,
            PendingOnly = false
        });
        Assert.That(li.Invoices.Count == 10);
        Assert.That(li.Invoices.First().State == Invoice.Types.InvoiceState.Settled);
    }

    [Test]
    [Category("Payment")]
    [Category("Invoice")]
    [Category("Sync")]
    [NonParallelizable]
    [Timeout(15000)]
    public async Task ListInvoiceAndPaymentNoDatePage()
    {
        var invoice = await Builder.GeneratePaymentRequestFromAlias("alice", new Invoice
        {
            Memo = "Things are too slow, never gonna work",
            Expiry = 100,
            ValueMsat = 1003 //1 satoshi, fees will be 0 because it is direct peer,
        });

        var alice = await Builder.WaitUntilAliasIsServerReady("alice");
        var bob = await Builder.WaitUntilAliasIsServerReady("bob");

        //purge data
        await bob.LightningClient.DeleteAllPaymentsAsync(new DeleteAllPaymentsRequest());

        var payment = await Builder.MakeLightningPaymentFromAlias("bob", new SendPaymentRequest
        {
            PaymentRequest = invoice.PaymentRequest,
            FeeLimitSat = 100000000,
            TimeoutSeconds = 5
        });
        Assert.That(payment.Status == Payment.Types.PaymentStatus.Succeeded);

        await Task.Delay(1000);

        var lp = await bob.LightningClient.ListPaymentsAsync(new ListPaymentsRequest
        {
            IncludeIncomplete = false,
            MaxPayments = 10
        });
        Assert.That(lp.Payments.Count == 1);
        await Task.Delay(2000);

        var li = await alice.LightningClient.ListInvoicesAsync(new ListInvoiceRequest
        {
            NumMaxInvoices = 10,
            Reversed = true,
            PendingOnly = false
        });
        Assert.That(li.Invoices.Count > 0);
        Assert.That(li.Invoices.First().State == Invoice.Types.InvoiceState.Settled);
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