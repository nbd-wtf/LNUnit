using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using Docker.DotNet;
using Google.Protobuf;
using Grpc.Core;
using Invoicesrpc;
using Lnrpc;
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
// using Dasync.Collections;
// using LNBolt;
using AddressType = Lnrpc.AddressType;
using Assert = NUnit.Framework.Assert;
using ListUnspentRequest = Lnrpc.ListUnspentRequest;
using Network = NBitcoin.Network;
using Transaction = Walletrpc.Transaction;

namespace LNUnit.Tests.Abstract;

public abstract class AbcLightningAbstractTests : IDisposable
{
    private readonly MemoryCache _aliasCache = new(new MemoryCacheOptions { SizeLimit = 10000 });
    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    protected readonly string _dbType;
    protected readonly string _lndImage;
    protected readonly string _lndRoot;
    protected readonly bool _pullImage;
    protected readonly string _tag;

    private ServiceProvider _serviceProvider;

    public AbcLightningAbstractTests(string dbType,
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

    public LNUnitBuilder? Builder { get; private set; }

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
        await _client.CreateDockerImageFromPath("./../../../../Docker/bitcoin/30.0",
            ["bitcoin:latest", "bitcoin:30.0"]);
        await SetupNetwork(_lndImage, _tag, _lndRoot, _pullImage, "bitcoin", "30.0");
    }


    public async Task SetupNetwork(string lndImage = "lightninglabs/lnd", string lndTag = "daily-testing-only",
        string lndRoot = "/root/.lnd", bool pullLndImage = false, string bitcoinImage = "polarlightning/bitcoind",
        string bitcoinTag = "29.0",
        bool pullBitcoinImage = false)
    {
        await _client.RemoveContainer("miner");
        await _client.RemoveContainer("alice");
        await _client.RemoveContainer("bob");
        await _client.RemoveContainer("carol");

        Builder.AddBitcoinCoreNode(image: bitcoinImage, tag: bitcoinTag, pullImage: pullBitcoinImage);

        if (pullLndImage) await _client.PullImageAndWaitForCompleted(lndImage, lndTag);

        if (pullBitcoinImage) await _client.PullImageAndWaitForCompleted(bitcoinImage, bitcoinTag);


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
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["alice"] : null,
            lndkSupport: false, nativeSql: _dbType != "boltdb", storeFinalHtlcResolutions: true);

        Builder.AddPolarLNDNode("bob",
            [
                new LNUnitNetworkDefinition.Channel
                {
                    ChannelSize = 10_000_000, //10MSat
                    RemotePushOnStart = 1_000_000, // 1MSat
                    RemoteName = "alice"
                }
            ], imageName: lndImage, tagName: lndTag, pullImage: false, acceptKeysend: true, mapTotmp: false,
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["bob"] : null, lndkSupport: false,
            nativeSql: _dbType != "boltdb");

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
            postgresDSN: _dbType == "postgres" ? PostgresFixture.LNDConnectionStrings["carol"] : null,
            lndkSupport: false, nativeSql: _dbType != "boltdb");

        await Builder.Build(lndRoot: lndRoot);

        await WaitNodesReady();
        await WaitGraphReady();
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

    // [Test]
    // [NonParallelizable]
    // [Category("Interceptor")]
    // public async Task VirtualNodeInvoicePaymentFlowDemo()
    // {
    //     var virtualNodeKey = new Key(); //Random node key
    //     SecureKeyManager.Initialize(virtualNodeKey.ToBytes());
    //     ConfigManager.Instance.Network = NLightning.Common.Types.Network.REG_TEST;
    //     //Node we will intercept at and use as hint
    //     var alice = await Builder!.WaitUntilAliasIsServerReady("alice");
    //     //Node we will pay from
    //     var bob = await Builder!.WaitUntilAliasIsServerReady("bob");
    //     //Build the invoice
    //     var preimageHexString = RandomNumberGenerator.GetHexString(64);
    //     var hashHexString = SHA256.HashData(Convert.FromHexString(preimageHexString)).ToHex();
    //     var paymentSecretHexString = RandomNumberGenerator.GetHexString(64);
    //     var paymentHash = uint256.Parse(hashHexString);
    //     var paymentSecret = uint256.Parse(paymentSecretHexString); ;
    //     var invoice = new NLightning.Bolts.BOLT11.Invoice(10_000, "Hello NLightning, here is 10 sats", paymentHash, paymentSecret);
    //     var shortChannelId = new ShortChannelId(6102, 1, 1);
    //     var ri = new RoutingInfoCollection
    //     {
    //         new RoutingInfo(   new PubKey(alice.LocalNodePubKeyBytes),  shortChannelId,   0,  0, 42)
    //     };
    //     var f = new Features();
    //     f.SetFeature(NLightning.Bolts.BOLT9.Feature.VAR_ONION_OPTIN, false, true);
    //     f.SetFeature(NLightning.Bolts.BOLT9.Feature.PAYMENT_SECRET, false, true);
    //     invoice.Features = f;
    //     invoice.RoutingInfos = ri;
    //     var paymentRequest = invoice.Encode();
    //
    //     //Setup interceptor to get virtual nodes stuff 
    //     var nodeClone = alice.Clone();
    //     Routerrpc.CircuitKey? htlcToCheckIfSettled = null;
    //
    //     var i = new LNDSimpleHtlcInterceptorHandler(nodeClone, async x =>
    //     {
    //         var onionBlob = x.OnionBlob.ToByteArray();
    //         var decoder = new OnionBlob(onionBlob);
    //         var sharedSecret = LNTools.DeriveSharedSecret(decoder.EphemeralPublicKey, virtualNodeKey.ToBytes());
    //         var hopPeel = decoder.Peel(sharedSecret, null, x.PaymentHash.ToByteArray());
    //         Assert.That(paymentSecretHexString.ToLower(), Is.EqualTo(hopPeel.hopPayload.PaymentData.PaymentSecret.ToHex()));
    //         //Logic for interception
    //         $"Intercepted Payment {Convert.ToHexString(x.PaymentHash.ToByteArray())} on channel {x.IncomingCircuitKey.ChanId} for virtual node {virtualNodeKey.PubKey.ToHex()}"
    //             .Print();
    //         htlcToCheckIfSettled = x.IncomingCircuitKey;
    //         return new ForwardHtlcInterceptResponse
    //         {
    //             Action = ResolveHoldForwardAction.Settle,
    //             Preimage = ByteString.CopyFrom(Convert.FromHexString(preimageHexString)), //we made invoice use preimage we know to settle
    //             IncomingCircuitKey = x.IncomingCircuitKey
    //         };
    //     });
    //     Builder.InterceptorHandlers.Add("alias", i);
    //
    //     //Pay the thing
    //     var p = bob.RouterClient.SendPaymentV2(new SendPaymentRequest()
    //     {
    //         PaymentRequest = paymentRequest,
    //         NoInflightUpdates = true,
    //         TimeoutSeconds = 10
    //     });
    //     await p.ResponseStream.MoveNext(CancellationToken.None);
    //     var paymentStatus = p.ResponseStream.Current;
    //     //Did it work?
    //     Assert.That(paymentStatus.Status, Is.EqualTo(Payment.Types.PaymentStatus.Succeeded));
    //     await Task.Delay(2000); //if we don't delay LND shows this as unsettled, it returns true early, with HTLC tracking should verify in prod setup. Otherwise this stuff is phantom 
    //
    //     //Check HTLCs
    //
    //     try
    //     {
    //         var result = await alice.LightningClient.LookupHtlcResolutionAsync(new LookupHtlcResolutionRequest()
    //         {
    //             ChanId = htlcToCheckIfSettled!.ChanId,
    //             HtlcIndex = htlcToCheckIfSettled!.HtlcId
    //         });
    //         result.PrintDump();
    //         Assert.That(result.Settled, Is.True);
    //     }
    //     catch (RpcException e) when (e.Status.StatusCode == StatusCode.NotFound)
    //     {
    //         //isn't resolved yet
    //     }
    //
    //
    //
    //
    //     var listChannels = await alice.LightningClient.ListChannelsAsync(new ListChannelsRequest()
    //     {
    //         Peer = ByteString.CopyFrom(bob.LocalNodePubKeyBytes)
    //     });
    //     Assert.That(listChannels.Channels.Sum(c => c.TotalSatoshisReceived), Is.EqualTo(10));
    //
    //     //Cleanup
    //     Builder.CancelAllInterceptors();
    //
    // }

    // private PSBT CreatePsbtOutputTemplate(int numberOfOutputs, string destinationAddress, long amountPerOutputSats, Network network)
    // {
    //     if (numberOfOutputs <= 0 || amountPerOutputSats <= 0)
    //         throw new ArgumentException("Number of outputs and amount per output must be greater than zero.");
    //
    //     BitcoinAddress address = BitcoinAddress.Create(destinationAddress, network);
    //
    //     // Create a transaction
    //     var tx = NBitcoin.Transaction.Create(network);
    //
    //     // // Add dummy inputs
    //     // tx.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, 0)));
    //
    //     // Add outputs
    //     for (int i = 0; i < numberOfOutputs; i++)
    //     {
    //         tx.Outputs.Add(new TxOut(Money.Satoshis(amountPerOutputSats), address));
    //     }
    //
    //     // Create PSBT from the transaction
    //     return PSBT.FromTransaction(tx, network);
    // }

    private async Task<string> CreatePsbtOutputTemplateBitcoinRPC(int numberOfOutputs, string destinationAddress,
        long amountPerOutputSats, Network network)
    {
        return await Builder.BitcoinRpcClient.CreateMultiOutputPsbt(destinationAddress, amountPerOutputSats,
            numberOfOutputs);
    }

    [TestCase(2, "bcrt1pau9xpav22lr592a2fgldkqy65e42uz5gdgy8et6kgvkrtep5gkeq07uyla", 1000, "regtest")]
    [Category("PSBT")]
    public async Task PSBTFlow(int numberOfOutputs, string depositAddress, int amountPerOutputSats, string network,
        ulong feeRate = 1)
    {
        var networkChain = Network.GetNetwork(network);
        var psbtOutputTemplate =
            await CreatePsbtOutputTemplateBitcoinRPC(numberOfOutputs, depositAddress, amountPerOutputSats,
                networkChain);
        //This verifies is fundable, and leases outputs.
        var fundReq = new FundPsbtRequest
        {
            Psbt = ByteString.FromBase64(psbtOutputTemplate)
        };

        fundReq.SatPerVbyte = feeRate;
        var node = Builder.LNDNodePool.GetLNDNodeConnection();

        var fundedPsbt = node.WalletKitClient.FundPsbt(fundReq);

        //extract txId
        var psbt = PSBT.Parse(fundedPsbt.FundedPsbt.ToBase64(), networkChain);
        var txIdHexString = psbt.GetGlobalTransaction().GetHash().ToString();
        Console.WriteLine($"LoopStaticInDeposit: Generated funded TX: {txIdHexString}");

        //Signs and returns raw tx to transmit
        var finalizedPsbt = node.WalletKitClient.FinalizePsbt(new FinalizePsbtRequest
        {
            FundedPsbt = fundedPsbt.FundedPsbt
        });
        Console.WriteLine($"LoopStaticInDeposit: Finalized TX: {txIdHexString}");

        //Publish
        var publicRes = node.WalletKitClient.PublishTransaction(new Transaction
        {
            Label = "Loop Static In Split",
            TxHex = finalizedPsbt.RawFinalTx
        });
    }

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
    [Category("HoldInvoice")]
    [NonParallelizable]
    [Timeout(30000)]
    public async Task HoldInvoice_Settlement()
    {
        var alice = await Builder.GetNodeFromAlias("alice");
        var bob = await Builder.GetNodeFromAlias("bob");
        var preimage = new byte[32];
        preimage[0] = 19;
        var hasher = SHA256Managed.Create();

        var hash = hasher.ComputeHash(preimage);
        var i = bob.InvoiceClient.AddHoldInvoice(new AddHoldInvoiceRequest
        {
            Hash = ByteString.CopyFrom(hash),
            Memo = "Test HOLD invoice",
            ValueMsat = 10_000, //10 sat,
            Expiry = 20
        });
        var paymentResponse = Builder.MakeLightningPaymentFromAlias("alice", new SendPaymentRequest
        {
            PaymentRequest = i.PaymentRequest,
            NoInflightUpdates = true,
            TimeoutSeconds = 20
        });
        Task.Run(() =>
        {
            var run = true;
            while (run)
                try
                {
                    var settle = bob.InvoiceClient.SettleInvoice(new SettleInvoiceMsg
                    {
                        Preimage = ByteString.CopyFrom(preimage)
                    });
                    if (settle != null)
                        run = false;
                    Task.Yield().GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    Debug.Print(e.ToString());
                    // do nothing
                }
        });
        Assert.That(paymentResponse.GetAwaiter().GetResult() != null);
    }

    // [Test]
    // [Category("Payment")]
    // [NonParallelizable]
    // public async Task MultiplePaymentsSameHash()
    // {
    //     var alice = await Builder.GetNodeFromAlias("alice");
    //     var carol = await Builder.GetNodeFromAlias("carol");
    //     var bob = await Builder.GetNodeFromAlias("bob");
    //     //    await Builder.DelayAllHTLCsOnAlias("alice", 2_000); //6s
    //     //    await Builder.DelayAllHTLCsOnAlias("bob", 2_000); //6s
    //     //    await Builder.DelayAllHTLCsOnAlias("carol", 2_000); //6s
    //     var preimage = new byte[32];
    //     preimage[0] = 12;
    //     var invoice1 = alice.LightningClient.AddInvoice(new Invoice()
    //     {
    //         RPreimage = ByteString.CopyFrom(preimage),
    //         Value = 1,
    //         Memo = "test same preimage",
    //         Expiry = 300
    //     });
    //     var invoice2 = bob.LightningClient.AddInvoice(new Invoice()
    //     {
    //         RPreimage = ByteString.CopyFrom(preimage),
    //         Value = 1,
    //         Memo = "test same preimage",
    //         Expiry = 300
    //     });
    //     var t1 = await Builder.MakeLightningPaymentFromAlias("carol", new SendPaymentRequest()
    //     {
    //         PaymentRequest = invoice1.PaymentRequest,
    //         TimeoutSeconds = 60,
    //         FeeLimitSat = 10000000
    //     });
    //     var t2 = await Builder.MakeLightningPaymentFromAlias("carol", new SendPaymentRequest()
    //     {
    //         PaymentRequest = invoice2.PaymentRequest,
    //         TimeoutSeconds = 60,
    //         FeeLimitSat = 10000000
    //     });
    //     //Task.WaitAll(t1, t2);
    //     t1.PrintDump();
    //     t2.PrintDump();
    //
    //
    // }

    [Test]
    [Category("Balancing")]
    [NonParallelizable]
    [Timeout(25000)]
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
        var n = await Builder.WaitUntilAliasIsServerReady("alice");
        var info = n.LightningClient.GetInfo(new GetInfoRequest());
        info.Version.Print();
    }

    [Test]
    [Category("OnChannelEvent")]
    [NonParallelizable]
    [Ignore("when needed")]
    public async Task OnChannelEventBasics()
    {
        var a = await Builder.GetNodeFromAlias("alice");
        var b = await Builder.GetNodeFromAlias("bob");
        using var e = new LNDChannelEventsHandler(a, OnChannelEvent);
        using var e2 = new LNDGraphEventsHandler(a, OnGraphEvent);
        await Task.Delay(5000);
        await Builder.RestartByAlias("bob");
        await Task.Delay(30000);
        e.InterceptCount.PrintDump();
    }

    private void OnGraphEvent(GraphTopologyUpdate obj)
    {
        "OnGraphEvent".Print();
        obj.PrintDump();
    }

    private void OnChannelEvent(ChannelEventUpdate obj)
    {
        "OnChannelEvent".Print();
        obj.PrintDump();
    }


    [Test]
    [Category("ChannelAcceptor")]
    [Timeout(15000)]
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

        var alice = await Builder.GetNodeFromAlias("alice");
        var bob = await Builder.GetNodeFromAlias("bob");
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
        //Move things along so it is confirmed
        await Builder.NewBlock();
        while (!alice.LightningClient.GetInfo(new GetInfoRequest()).SyncedToChain) //wait until synced so can open
            await Task.Delay(1000);
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


    /// <summary>
    ///     Keysend from Alice and Carol to Bob, see how many can clear in 10s.
    /// </summary>
    /// <param name="threads"></param>
    [Test]
    [Category("Payment")]
    [NonParallelizable]
    [Timeout(60000)]
    [TestCase(1)]
    [TestCase(32)]
    public async Task Keysend_To_Bob_PaymentsPerSecondMax_Threaded(int threads)
    {
        Builder.CancelAllInterceptors();
        var aliceSettings = await Builder.GetLNDSettingsFromContainer("alice", _lndRoot);
        var bobSettings = await Builder.GetLNDSettingsFromContainer("bob", _lndRoot);
        var carolSettings = await Builder.GetLNDSettingsFromContainer("carol", _lndRoot);
        var alice = new LNDNodeConnection(aliceSettings);
        var bob = new LNDNodeConnection(bobSettings);
        var carol = new LNDNodeConnection(carolSettings);

        await Task.Delay(1000); //TODO: why? we are not checking channels are up

        var sw = Stopwatch.StartNew();
        var success_count = 0;
        var fail_count = 0;
        var cts = new CancellationTokenSource();

        var mc_base = await alice.RouterClient.QueryMissionControlAsync(new QueryMissionControlRequest());
        while (sw.ElapsedMilliseconds <= 10_000)
            Task.WaitAll(
                Parallel.ForAsync(0, threads, cts.Token, async (i, token) =>
                {
                    var payment = await alice.KeysendPayment(bob.LocalNodePubKey, 1, 100000000, "Hello World", 6,
                        new Dictionary<ulong, byte[]> { { 99999, new byte[] { 11, 11, 11 } } });
                    if (payment.Status == Payment.Types.PaymentStatus.Succeeded)
                        Interlocked.Increment(ref success_count);
                    else
                        Interlocked.Increment(ref fail_count);
                }),
                Parallel.ForAsync(0, threads, cts.Token, async (i, token) =>
                {
                    var payment = await carol.KeysendPayment(bob.LocalNodePubKey, 1, 100000000, "Hello World", 6,
                        new Dictionary<ulong, byte[]> { { 99999, new byte[] { 11, 11, 11 } } });
                    if (payment.Status == Payment.Types.PaymentStatus.Succeeded)
                        Interlocked.Increment(ref success_count);
                    else
                        Interlocked.Increment(ref fail_count);
                    Interlocked.Increment(ref success_count);
                }));
        sw.Stop();
        var attempted_pps = (fail_count + success_count) / (sw.ElapsedMilliseconds / 1000.0);
        $"Attempted Payments: {fail_count + success_count}".Print();
        $"Successful: {success_count}".Print();
        $"Failed    : {fail_count}".Print();
        var successful_pps = success_count / (sw.ElapsedMilliseconds / 1000.0);
        $"Successful Payments per second: {successful_pps}".Print();
        var size = await Builder.GetFileSize("alice", "/root/.lnd/data/graph/regtest/channel.db");
        size.PrintDump();
        size = await Builder.GetFileSize("bob", "/root/.lnd/data/graph/regtest/channel.db");
        size.PrintDump();
        size = await Builder.GetFileSize("carol", "/root/.lnd/data/graph/regtest/channel.db");
        size.PrintDump();
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
        Assert.That(data.IsRpcReady);
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
        var data = await Builder.UpdateGlobalFeePolicyOnAlias("alice", new LNUnitNetworkDefinition.Channel());
        data.PrintDump();
        Assert.That(data.FailedUpdates.Count == 0);
    }

    [Test]
    [Timeout(10000)]
    public async Task SendMany_Onchain()
    {
        var alice = await Builder.GetNodeFromAlias("alice");
        var addresses = new List<string>();
        var sendManyRequest = new SendManyRequest
        {
            SatPerVbyte = 10,
            Label = "Test send to multiple",
            SpendUnconfirmed = true
        };
        //make destinations
        for (var i = 0; i < 10; i++)
        {
            var address = alice.LightningClient.NewAddress(new NewAddressRequest { Type = AddressType.TaprootPubkey })
                .Address;
            addresses.Add(address);
            sendManyRequest.AddrToAmount.Add(address, 10000);
        }

        alice.LightningClient.SendMany(sendManyRequest);

        await Builder.NewBlock(10); //fast forward in time

        await Builder.WaitUntilSyncedToChain("alice");
        //verify last address got funds
        var unspend = alice.LightningClient.ListUnspent(new ListUnspentRequest { MinConfs = 1, MaxConfs = 20 });
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

        Assert.That(confirmedAddresses.Count, Is.EqualTo(addresses.Count), "Confirmed deposits doesn't match request.");
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
    [Category("Invoice")]
    [NonParallelizable]
    //   [Timeout(5000)]
    public async Task CheckForSuccessButFailureReasonIsNotNone_LND_18_Bug()
    {
        var invoice = await Builder.GeneratePaymentRequestFromAlias("alice", new Invoice
        {
            Memo = "weird",
            Expiry = 1000,
            ValueMsat = 1003
        });
        //Apply HTLC hold to prevent payment from settling 
        await Builder.DelayAllHTLCsOnAlias("bob", 30_000);
        await Builder.DelayAllHTLCsOnAlias("carol", 15_000);
        Payment? payment = null;
        var failed = false;

        payment = await Builder.MakeLightningPaymentFromAlias("carol", new SendPaymentRequest
        {
            PaymentRequest = invoice.PaymentRequest,
            FeeLimitSat = 100000000,
            TimeoutSeconds = 15
        });


        Builder.CancelInterceptorOnAlias("bob");
        Builder.CancelInterceptorOnAlias("carol");
        Assert.That(payment!.Status == Payment.Types.PaymentStatus.Succeeded);
        Assert.That(payment!.FailureReason == PaymentFailureReason.FailureReasonNone);
        var invoiceLookup = await Builder.LookupInvoice("alice", invoice.RHash);
        Assert.That(invoiceLookup != null);
        Assert.That(invoiceLookup.RHash == invoice.RHash);
        Assert.That(invoiceLookup.State == Invoice.Types.InvoiceState.Settled);
        var paymentLookup = await Builder.LookupPayment("carol", invoiceLookup.RHash);
        Assert.That(paymentLookup.Status == Payment.Types.PaymentStatus.Succeeded);
        //This will pass in v0.18.0 and is changing behavior
        Assert.That(paymentLookup.FailureReason != PaymentFailureReason.FailureReasonNone);
    }


    [Test]
    [Category("Payment")]
    [Category("Interceptor")]
    [NonParallelizable]
    [Timeout(30000)]
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
        List<LndHtlcMonitor> monitors = new();
        foreach (var n in Builder.LNDNodePool.ReadyNodes.ToImmutableList())
            //This disposes so should use Clone of connection
            monitors.Add(new LndHtlcMonitor(n.Clone(), htlc => { htlcEvents++; }));


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
    [Timeout(30000)]
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
        var asyncEnumerablePayments = payments.Payments.Where(x => finalized.Contains(x.Status) && x.Htlcs.Any())
            .ToAsyncEnumerable();
        await foreach (var p in asyncEnumerablePayments)
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

    [Test]
    [Category("Payment")]
    [Category("Invoice")]
    [Category("Sync")]
    [NonParallelizable]
    [Timeout(30000)]
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
        await bob.LightningClient.DeleteAllPaymentsAsync(new DeleteAllPaymentsRequest
        {
            AllPayments = true
        });

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
        await bob.LightningClient.DeleteAllPaymentsAsync(new DeleteAllPaymentsRequest
        {
            AllPayments = true
        });

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