using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Dasync.Collections;
using Docker.DotNet;
using Docker.DotNet.Models;
using Google.Protobuf;
using Grpc.Core;
using Lnrpc;
using LNUnit.LND;
using NBitcoin;
using NBitcoin.RPC;
using Org.BouncyCastle.Crypto.Parameters;
using Routerrpc;
using ServiceStack;
using SharpCompress.Readers;
using AddressType = Lnrpc.AddressType;
using HostConfig = Docker.DotNet.Models.HostConfig;
using Network = NBitcoin.Network;

namespace LNUnit.Setup;

public class LNUnitBuilder : IDisposable
{
    private readonly DockerClient _dockerClient = new DockerClientConfiguration().CreateClient();
    private readonly ILogger<LNUnitBuilder>? _logger;
    private readonly IServiceProvider? _serviceProvider;
    private bool _loopLNDReady;
    public Dictionary<string, LNDChannelInterceptorHandler> ChannelHandlers = new();

    public Dictionary<string, LNDSimpleHtlcInterceptorHandler> InterceptorHandlers = new();

    public LNUnitBuilder(LNUnitNetworkDefinition c = null, ILogger<LNUnitBuilder>? logger = null,
        IServiceProvider serviceProvider = null)
    {
        Configuration = c ?? new LNUnitNetworkDefinition();
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public int WaitForBitcoinNodeStartupTimeout { get; set; } = 30_000; //ms timeout

    public bool IsBuilt { get; internal set; }
    public bool IsDestoryed { get; internal set; }
    public RPCClient? BitcoinRpcClient { get; internal set; }
    public LNDNodePool? LNDNodePool { get; internal set; }
    public LNUnitNetworkDefinition Configuration { get; set; } = new();

    public void Dispose()
    {
        _dockerClient.Dispose();
        if (LNDNodePool != null)
            LNDNodePool.Dispose();
    }


    public static async Task<LNUnitBuilder> LoadConfigurationFile(string path)
    {
        var data = await File.ReadAllTextAsync(path);
        return new LNUnitBuilder(LoadConfiguration(data));
    }

    public string SaveConfigurationAsJson()
    {
        return Configuration.ToJson(x => x.MaxDepth = 2);
    }

    public static LNUnitNetworkDefinition LoadConfiguration(string json)
    {
        return json.FromJson<LNUnitNetworkDefinition>();
    }

    public async Task Destroy(bool destoryNetwork = false)
    {
        if (!IsBuilt)
            throw new Exception("Build() must called before Destroy()");

        foreach (var n in Configuration.BTCNodes.Where(x => !x.DockerContainerId.IsEmpty()))
        {
            var result = await _dockerClient.Containers.StopContainerAsync(n.DockerContainerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 1 });
            await _dockerClient.Containers.RemoveContainerAsync(n.DockerContainerId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
        }

        foreach (var n in Configuration.LNDNodes.Where(x => !x.DockerContainerId.IsEmpty()))
        {
            var result = await _dockerClient.Containers.StopContainerAsync(n.DockerContainerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 1 });
            await _dockerClient.Containers.RemoveContainerAsync(n.DockerContainerId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
        }

        if (destoryNetwork) await _dockerClient.Networks.DeleteNetworkAsync(Configuration.DockerNetworkId);
        IsDestoryed = true;
    }

    public async Task Build(bool setupNetwork = false, string lndRoot = "/home/lnd/.lnd")
    {
        _logger?.LogInformation("Building LNUnit scenerio.");
        //Validation
        if (IsBuilt)
            throw new Exception("Build() already called.");
        IsDestoryed = false; // reset
        if (Configuration.BTCNodes.Count != 1)
            throw
                new Exception(
                    "Must have 1 BTCNode"); //TODO: Has loop but not really setup for multiple properly yet, so throw for more than 1 unit stable

        //Setup network
        if (setupNetwork)
            Configuration.DockerNetworkId = await _dockerClient.BuildTestingNetwork(Configuration.BaseName);

        //Setup BTC Nodes

        ContainerListResponse bitcoinNode;
        foreach (var n in Configuration.BTCNodes) //TODO: can do multiple at once
        {
            if (n.PullImage) await _dockerClient.PullImageAndWaitForCompleted(n.Image, n.Tag);
            var nodeContainer = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = $"{n.Image}:{n.Tag}",
                HostConfig = new HostConfig
                {
                    NetworkMode = $"{Configuration.DockerNetworkId}"
                },
                Name = n.Name,
                Hostname = n.Name,
                Cmd = n.Cmd
            });
            n.DockerContainerId = nodeContainer.ID;
            var success =
                await _dockerClient.Containers.StartContainerAsync(nodeContainer.ID, new ContainerStartParameters());
            await Task.Delay(500);
            //Setup wallet and basic funds
            var listContainers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters());
            bitcoinNode = listContainers.First(x => x.ID == nodeContainer.ID);
            BitcoinRpcClient = new RPCClient("bitcoin:bitcoin",
                bitcoinNode.NetworkSettings.Networks.First().Value.IPAddress, Bitcoin.Instance.Regtest);
            WaitForBitcoinNodeStartupTimeout = 30000;
            BitcoinRpcClient.HttpClient = new HttpClient
            { Timeout = TimeSpan.FromMilliseconds(WaitForBitcoinNodeStartupTimeout) };

            await BitcoinRpcClient.CreateWalletAsync("default", new CreateWalletOptions { LoadOnStartup = true });
            var utxos = await BitcoinRpcClient.GenerateAsync(200);
        }


        var lndSettings = new List<LNDSettings>();
        CreateContainerResponse? loopServer = null;

        if (Configuration.LoopServer != null)
        {
            var n = Configuration.LoopServer;
            if (n.PullImage) await _dockerClient.PullImageAndWaitForCompleted(n.Image, n.Tag);
            var nodeContainer = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = $"{n.Image}:{n.Tag}",
                HostConfig = new HostConfig
                {
                    NetworkMode = $"{Configuration.DockerNetworkId}",
                    Links = new List<string> { n.BitcoinBackendName }
                },
                Name = n.Name,
                Hostname = n.Name,
                Cmd = n.Cmd
            });
            n.DockerContainerId = nodeContainer.ID;
            var success =
                await _dockerClient.Containers.StartContainerAsync(nodeContainer.ID, new ContainerStartParameters());
            var inspectionResponse = await _dockerClient.Containers.InspectContainerAsync(n.DockerContainerId);
            var ipAddress = inspectionResponse.NetworkSettings.Networks.First().Value.IPAddress;

            var txt = await GetStringFromFS(n.DockerContainerId, $"{lndRoot}/tls.cert");
            var tlsCertBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(txt));
            var data = await GetBytesFromFS(n.DockerContainerId,
                $"{lndRoot}/data/chain/bitcoin/regtest/admin.macaroon");
            var adminMacaroonBase64String = Convert.ToBase64String(data);


            var adminMacaroonTar =
                await GetTarStreamFromFS(n.DockerContainerId,
                    $"{lndRoot}/data/chain/bitcoin/regtest/admin.macaroon");

            var lndConfig = new LNDSettings
            {
                GrpcEndpoint = $"https://{ipAddress}:10009/",
                MacaroonBase64 = adminMacaroonBase64String,
                TLSCertBase64 = tlsCertBase64
            };
            //lndSettings.Add(lndConfig);
            foreach (var dependentContainer in n.DependentContainers)
            {
                if (dependentContainer.PullImage)
                    await _dockerClient.PullImageAndWaitForCompleted(dependentContainer.Image, dependentContainer.Tag);

                loopServer = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
                {
                    Image = $"{dependentContainer.Image}:{dependentContainer.Tag}",
                    HostConfig = new HostConfig
                    {
                        NetworkMode = $"{Configuration.DockerNetworkId}",
                        Links = new List<string> { n.BitcoinBackendName, "loopserver-lnd", "alice" },
                        Binds = dependentContainer.Binds
                    },
                    Name = dependentContainer.Name,
                    Hostname = dependentContainer.Name,
                    Cmd = dependentContainer.Cmd,
                    ExposedPorts = dependentContainer.ExposedPorts
                });
                dependentContainer.DockerContainerId = nodeContainer.ID;
                var dataReadonly = await GetBytesFromFS(n.DockerContainerId,
                    $"{lndRoot}/data/chain/bitcoin/regtest/readonly.macaroon");
                var readonlyBase64String = Convert.ToBase64String(dataReadonly);

                var invoices = await GetBytesFromFS(n.DockerContainerId,
                    $"{lndRoot}/data/chain/bitcoin/regtest/invoices.macaroon");
                var chainnotifier = await GetBytesFromFS(n.DockerContainerId,
                    $"{lndRoot}/data/chain/bitcoin/regtest/chainnotifier.macaroon");
                var signer = await GetBytesFromFS(n.DockerContainerId,
                    $"{lndRoot}/data/chain/bitcoin/regtest/signer.macaroon");
                var walletkit = await GetBytesFromFS(n.DockerContainerId,
                    $"{lndRoot}/data/chain/bitcoin/regtest/walletkit.macaroon");
                var router = await GetBytesFromFS(n.DockerContainerId,
                    $"{lndRoot}/data/chain/bitcoin/regtest/router.macaroon");
                File.WriteAllBytes("./loopserver-test/tls.cert", Convert.FromBase64String(tlsCertBase64));
                File.WriteAllBytes("./loopserver-test/admin.macaroon",
                    Convert.FromBase64String(adminMacaroonBase64String));
                File.WriteAllBytes("./loopserver-test/readonly.macaroon",
                    Convert.FromBase64String(readonlyBase64String));
                File.WriteAllBytes("./loopserver-test/invoices.macaroon",
                    invoices);
                File.WriteAllBytes("./loopserver-test/chainnotifier.macaroon",
                    chainnotifier);
                File.WriteAllBytes("./loopserver-test/signer.macaroon",
                    signer);
                File.WriteAllBytes("./loopserver-test/walletkit.macaroon",
                    walletkit);
                File.WriteAllBytes("./loopserver-test/router.macaroon",
                    router);
                var success2 =
                    await _dockerClient.Containers.StartContainerAsync(loopServer?.ID,
                        new ContainerStartParameters());
            }
        }

        //Setup LND Nodes
        foreach (var n in Configuration.LNDNodes) //TODO: can do multiple at once
        {
            if (n.PullImage) await _dockerClient.PullImageAndWaitForCompleted(n.Image, n.Tag);
            var createContainerParameters = new CreateContainerParameters
            {
                Image = $"{n.Image}:{n.Tag}",
                HostConfig = new HostConfig
                {
                    NetworkMode = $"{Configuration.DockerNetworkId}",
                    Links = new List<string> { n.BitcoinBackendName }
                },
                Name = n.Name,
                Hostname = n.Name,
                Cmd = n.Cmd
            };
            if (n.Binds.Any()) createContainerParameters.HostConfig.Binds = n.Binds;
            var nodeContainer = await _dockerClient.Containers.CreateContainerAsync(createContainerParameters);
            n.DockerContainerId = nodeContainer.ID;
            var success =
                await _dockerClient.Containers.StartContainerAsync(nodeContainer.ID, new ContainerStartParameters());
            //Not always having IP yet.
            var ipAddress = string.Empty;
            ContainerInspectResponse? inspectionResponse = null;
            while (ipAddress.IsEmpty())
            {
                inspectionResponse = await _dockerClient.Containers.InspectContainerAsync(n.DockerContainerId);
                ipAddress = inspectionResponse.NetworkSettings.Networks.First().Value.IPAddress;
            }

            var basePath =
                !n.Image.Contains("lightning-terminal")
                    ? lndRoot
                    : "/root/lnd/.lnd"; // "/home/lnd/.lnd" : "/root/lnd/.lnd";
            if (n.Image.Contains("lightning-terminal")) await Task.Delay(2000);
            var txt = await GetStringFromFS(n.DockerContainerId, $"{basePath}/tls.cert");
            var tlsCertBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(txt));
            var data = await GetBytesFromFS(n.DockerContainerId,
                $"{basePath}/data/chain/bitcoin/regtest/admin.macaroon");
            var adminMacaroonBase64String = Convert.ToBase64String(data);


            var adminMacaroonTar =
                await GetTarStreamFromFS(n.DockerContainerId,
                    $"{basePath}/data/chain/bitcoin/regtest/admin.macaroon");


            lndSettings.Add(new LNDSettings
            {
                GrpcEndpoint = $"https://{ipAddress}:10009/",
                MacaroonBase64 = adminMacaroonBase64String,
                TLSCertBase64 = tlsCertBase64
            });
        }


        if (Configuration.LNDNodes.Any())
        {
            var cancelSource = new CancellationTokenSource(60 * 1000); //Sanity Timeout
            //Spun up
            var nodePoolConfig = new LNDNodePoolConfig();
            nodePoolConfig.UpdateReadyStatesPeriod(1);
            foreach (var c in lndSettings)
                nodePoolConfig.AddConnectionSettings(c);
            LNDNodePool = _serviceProvider != null
                ? ActivatorUtilities.CreateInstance<LNDNodePool>(_serviceProvider, nodePoolConfig)
                : new LNDNodePool(lndSettings, 1);
            while (LNDNodePool.ReadyNodes.Count < Configuration.LNDNodes.Count)
            {
                await Task.Delay(250);
                if (cancelSource.IsCancellationRequested) throw new Exception("CANCELED");
            }

            //Cross Connect Peers
            foreach (var localNode in LNDNodePool.ReadyNodes.ToImmutableList())
            {
                var remotes = LNDNodePool.ReadyNodes.Where(x => x.LocalAlias != localNode.LocalAlias).ToImmutableList();
                foreach (var remoteNode in remotes) await ConnectPeers(localNode, remoteNode);
            }

            while (LNDNodePool.ReadyNodes.Count < Configuration.LNDNodes.Count)
            {
                await Task.Delay(250);
                if (cancelSource.IsCancellationRequested) throw new Exception("CANCELED");
            }

            //Setup Channels (this includes sending funds and waiting)
            foreach (var n in Configuration.LNDNodes) //TODO: can do multiple at once
            {
                var node = LNDNodePool.ReadyNodes.First(x => x.LocalAlias == n.Name); //Local
                n.PubKey = node.LocalNodePubKey;
                var newAddressResponse = node.LightningClient.NewAddress(new NewAddressRequest
                {
                    Type = AddressType.UnusedWitnessPubkeyHash
                });
                //Fund
                await BitcoinRpcClient.GenerateAsync(1);

                BitcoinRpcClient.SendToAddress(BitcoinAddress.Create(newAddressResponse.Address, Network.RegTest),
                    Money.Parse("42.69"));
                BitcoinRpcClient.SendToAddress(BitcoinAddress.Create(newAddressResponse.Address, Network.RegTest),
                    Money.Parse("42.69"));
                await BitcoinRpcClient.GenerateAsync(1);

                foreach (var c in n.Channels)
                {
                    //wait until they are ready...
                    var remoteNode = LNDNodePool.ReadyNodes.ToImmutableList()
                        .FirstOrDefault(x => x.LocalAlias == c.RemoteName);
                    while (remoteNode == null)
                        remoteNode = LNDNodePool.ReadyNodes.ToImmutableList()
                            .FirstOrDefault(x => x.LocalAlias == c.RemoteName);

                    //Connect Peers
                    //We are doing this above now
                    ConnectPeers(node, remoteNode);

                    //Wait until we are synced to the chain so we know we have funds secured to send
                    await WaitUntilSyncedToChain(node);

                    var channelPoint = await node.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
                    {
                        NodePubkey = ByteString.CopyFrom(Convert.FromHexString(remoteNode.LocalNodePubKey)),
                        SatPerVbyte = 10,
                        LocalFundingAmount = c.ChannelSize,
                        PushSat = c.RemotePushOnStart
                    });
                    c.ChannelPoint = channelPoint;
                    //Move things along so it is confirmed
                    await BitcoinRpcClient.GenerateAsync(10);
                    await WaitUntilSyncedToChain(node);
                    var setFeesWorked = false;
                    while (!setFeesWorked)
                        try
                        {
                            //Set fees & htlcs, TLD
                            var policyUpdateResponse = await node.LightningClient.UpdateChannelPolicyAsync(
                                new PolicyUpdateRequest
                                {
                                    BaseFeeMsat = c.BaseFeeMsat.GetValueOrDefault(),
                                    ChanPoint = channelPoint,
                                    FeeRatePpm = c.FeeRatePpm.GetValueOrDefault(),
                                    MinHtlcMsat = c.MinHtlcMsat.GetValueOrDefault(),
                                    MinHtlcMsatSpecified = c.MinHtlcMsat.HasValue,
                                    MaxHtlcMsat = c.MaxHtlcMsat.GetValueOrDefault(),
                                    TimeLockDelta = c.TimeLockDelta
                                });
                            setFeesWorked = true;
                        }
                        catch (Exception e)
                        {
                            //ignored
                        }
                }
            }
        }


        IsBuilt = true;
    }

    public static async Task WaitUntilSyncedToChain(LNDNodeConnection node)
    {
        var info = new GetInfoResponse();
        while (!info.SyncedToChain)
        {
            await Task.Delay(100);
            info = await node.LightningClient.GetInfoAsync(new GetInfoRequest());
        }
    }

    public async Task WaitUntilSyncedToChain(string alias)
    {
        await WaitUntilSyncedToChain(await GetNodeFromAlias(alias));
    }

    public async Task<LNDSettings> GetLNDSettingsFromContainer(string containerId, string lndRoot = "/home/lnd/.lnd")
    {
        var inspectionResponse = await _dockerClient.Containers.InspectContainerAsync(containerId);
        while (inspectionResponse.State.Running != true)
        {
            await Task.Delay(250);
            inspectionResponse = await _dockerClient.Containers.InspectContainerAsync(containerId);
        }

        var ipAddress = inspectionResponse.NetworkSettings.Networks.First().Value.IPAddress;
        var foundFile = false;

        GetArchiveFromContainerResponse? archResponse = null;
        //Wait until LND actually has files started


        var tlsTar = await GetTarStreamFromFS(containerId, $"{lndRoot}/tls.cert");
        var txt = GetStringFromTar(tlsTar); //GetStringFromFS(containerId, "/home/lnd/.lnd/tls.cert");
        var tlsCertBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(txt));

        var adminMacaroonTar =
            await GetTarStreamFromFS(containerId, $"{lndRoot}/data/chain/bitcoin/regtest/admin.macaroon");
        var data = GetBytesFromTar(
            adminMacaroonTar);
        var adminMacaroonBase64String = Convert.ToBase64String(data);

        return new LNDSettings
        {
            GrpcEndpoint = $"https://{ipAddress}:10009/",
            MacaroonBase64 = adminMacaroonBase64String,
            TLSCertBase64 = tlsCertBase64
        };
    }


    private async Task<GetArchiveFromContainerResponse?> GetTarStreamFromFS(string containerId, string filePath)
    {
        var foundFile = false;
        while (!foundFile)
        {
            try
            {
                var archResponse = await _dockerClient.Containers.GetArchiveFromContainerAsync(
                    containerId,
                    new GetArchiveFromContainerParameters
                    {
                        Path = filePath
                    }, false);
                return archResponse;
            }
            catch (Exception e)
            {
            }

            await Task.Delay(100);
        }

        return null;
    }

    private async Task<bool> PutFile(string containerId, string filePath, Stream stream)
    {
        await _dockerClient.Containers.ExtractArchiveToContainerAsync(containerId, new ContainerPathStatParameters
        {
            //  AllowOverwriteDirWithFile = true,
            Path = filePath
        }, stream);

        return true;
    }

    private async Task<byte[]> GetBytesFromFS(string containerId, string filePath)
    {
        return GetBytesFromTar(await GetTarStreamFromFS(containerId, filePath));
    }

    private async Task<string> GetStringFromFS(string containerId, string filePath)
    {
        return GetStringFromTar(await GetTarStreamFromFS(containerId, filePath));
    }

    private async Task ConnectPeers(LNDNodeConnection node, LNDNodeConnection remoteNode)
    {
        var retryCount = 0;
    do_again:
        try
        {
            node.LightningClient.ConnectPeer(new ConnectPeerRequest
            {
                Addr = new LightningAddress
                {
                    Host = remoteNode.Host.Replace("https://", string.Empty)
                        .Replace("http://", string.Empty)
                        .Replace("/", string.Empty).Replace(":10009", ":9735"), //because URI isn't populating 
                    Pubkey = remoteNode.LocalNodePubKey
                },
                Perm = true,
                Timeout = 5
            });
        }
        catch (RpcException e) when (e.Status.Detail.Contains("already connected to peer"))
        {
            //ignored
        }
        catch (RpcException e) when (e.Status.Detail.Contains("server is still in the process of starting"))
        {
            retryCount++;
            if (retryCount < 4)
            {
                _logger?.LogDebug("Connect Peer: Server still starting...waiting 500ms.. {RetryCount}", retryCount);
                await Task.Delay(500);
                goto do_again;
            }

            throw;
        }
    }

    public async Task WaitGraphReady(string fromAlias, int expectedNodeCount)
    {
        var graphReady = false;
        while (!graphReady)
        {
            var graph = await GetGraphFromAlias(fromAlias);
            if (graph.Nodes.Count < expectedNodeCount)
            {
                await Task.Delay(250); //let the graph sync 
            }
            else
            {
                graphReady = true;
            }
        }
    }
    private static string GetStringFromTar(GetArchiveFromContainerResponse tlsCertResponse)
    {
        using (var stream = tlsCertResponse.Stream)
        {
            var reader = ReaderFactory.Open(stream, new ReaderOptions { LookForHeader = true });
            while (reader.MoveToNextEntry())
                if (!reader.Entry.IsDirectory)
                {
                    var s = reader.OpenEntryStream();
                    return s.ReadToEnd();
                }
        }

        return string.Empty;
    }

    private static byte[] GetBytesFromTar(GetArchiveFromContainerResponse tlsCertResponse)
    {
        using (var stream = tlsCertResponse.Stream)
        {
            var reader = ReaderFactory.Open(stream, new ReaderOptions { LookForHeader = true });
            while (reader.MoveToNextEntry())
                if (!reader.Entry.IsDirectory)
                {
                    var s = reader.OpenEntryStream();
                    return s.ReadFully();
                }
        }

        return null;
    }


    public async Task<bool> ShutdownByAlias(string alias, uint waitBeforeKillSeconds = 1, bool isLND = false)
    {
        if (isLND) LNDNodePool?.RemoveNode(await GetNodeFromAlias(alias));
        return await _dockerClient.Containers.StopContainerAsync(alias, new ContainerStopParameters
        {
            WaitBeforeKillSeconds = waitBeforeKillSeconds
        });
    }

    public async Task RestartByAlias(string alias, uint waitBeforeKillSeconds = 1, bool isLND = false,
        bool resetChannels = true, string lndRoot = "/home/lnd/.lnd")
    {
        await _dockerClient.Containers.RestartContainerAsync(alias, new ContainerRestartParameters
        {
            WaitBeforeKillSeconds = waitBeforeKillSeconds
        });

        if (isLND)
        {
            LNDNodePool?.AddNode(await GetLNDSettingsFromContainer(alias, lndRoot));

            var node = await WaitUntilAliasIsServerReady(alias);
            //reset channels
            foreach (var nodes in Configuration.LNDNodes.Where(x => x.Name == alias))
                foreach (var c in nodes.Channels)
                {
                    var remoteNode = LNDNodePool.ReadyNodes.First(x => x.LocalAlias == c.RemoteName);
                    //Connect Peers
                    //We are doing this above now
                    ConnectPeers(node, remoteNode);

                    //Wait until we are synced to the chain so we know we have funds secured to send
                    var info = new GetInfoResponse();
                    while (!info.SyncedToChain && !info.SyncedToGraph)
                    {
                        info = await node.LightningClient.GetInfoAsync(new GetInfoRequest());
                        await Task.Delay(250);
                    }
                    info = new GetInfoResponse();
                    while (!info.SyncedToChain && !info.SyncedToGraph)
                    {
                        info = await remoteNode.LightningClient.GetInfoAsync(new GetInfoRequest());
                        await Task.Delay(250);
                    }

                    await this.WaitGraphReady(alias, this.LNDNodePool.TotalNodes);
                    if (resetChannels)
                    {
                        var channelPoint = await node.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
                        {
                            NodePubkey = ByteString.CopyFrom(Convert.FromHexString(remoteNode.LocalNodePubKey)),
                            SatPerVbyte = 10,
                            LocalFundingAmount = c.ChannelSize,
                            PushSat = c.RemotePushOnStart
                        });
                        c.ChannelPoint = channelPoint;
                        //Move things along so it is confirmed
                        await BitcoinRpcClient.GenerateAsync(10);
                        await this.WaitUntilSyncedToChain(alias);
                        await this.WaitGraphReady(alias, this.LNDNodePool.TotalNodes);
                        //Set fees & htlcs, TLD
                        var policySet = false;
                        var hitcount = 0;
                        while (!policySet)
                        {
                            try
                            {
                                var policyUpdateResponse = await node.LightningClient.UpdateChannelPolicyAsync(
                                    new PolicyUpdateRequest
                                    {
                                        BaseFeeMsat = c.BaseFeeMsat.GetValueOrDefault(),
                                        ChanPoint = channelPoint,
                                        FeeRatePpm = c.FeeRatePpm.GetValueOrDefault(),
                                        MinHtlcMsat = c.MinHtlcMsat.GetValueOrDefault(),
                                        MinHtlcMsatSpecified = c.MinHtlcMsat.HasValue,
                                        MaxHtlcMsat = c.MaxHtlcMsat.GetValueOrDefault(),
                                        TimeLockDelta = c.TimeLockDelta
                                    });
                                policySet = true;
                            }
                            catch (Grpc.Core.RpcException e) when (e.Status.StatusCode == StatusCode.Unknown &&
                                                                   e.Status.Detail.EqualsIgnoreCase("channel from self node has no policy"))
                            {
                                if (hitcount == 0)
                                {
                                    //generate blocks so we do a status update for sure.
                                    await BitcoinRpcClient.GenerateAsync(145);

                                }

                                hitcount++;
                                await Task.Delay(500); //give it some time
                            }
                        }

                    }
                }

            await Task.Delay(2500);
        }
    }

    public async Task<AddInvoiceResponse> GeneratePaymentRequestFromAlias(string alias, Invoice invoice)
    {
        return (await GeneratePaymentsRequestFromAlias(alias, 1, invoice)).First();
        // var node = GetNodeFromAlias(alias);
        // var response = await node.LightningClient.AddInvoiceAsync(invoice);
        // return response;
    }

    public async Task<List<AddInvoiceResponse>> GeneratePaymentsRequestFromAlias(string alias, int count,
        Invoice invoice)
    {
        var node = await GetNodeFromAlias(alias);
        var response = new ConcurrentStack<AddInvoiceResponse>();
        await Enumerable.Range(0, count).ParallelForEachAsync(async x =>
        {
            var invoiceCopy = invoice.Clone();
            response.Push(await node.LightningClient.AddInvoiceAsync(invoiceCopy));
        });
        return response.ToList();
    }

    public async Task<Payment> LookupPayment(string alias, ByteString hash)
    {
        var node = await GetNodeFromAlias(alias);
        var streamingCallResponse = node.RouterClient.TrackPaymentV2(new TrackPaymentRequest()
        {
            NoInflightUpdates = true,
            PaymentHash = hash
        });
        Payment? paymentResponse = null;
        await foreach (var res in streamingCallResponse.ResponseStream.ReadAllAsync()) paymentResponse = res;
        return paymentResponse!;
    }
    public async Task<Invoice?> LookupInvoice(string alias, ByteString rHash)
    {
        var node = await GetNodeFromAlias(alias);
        var response = await node.LightningClient.LookupInvoiceAsync(new PaymentHash { RHash = rHash });
        return response;
    }

    public async Task<LNDNodeConnection> GetNodeFromAlias(string alias)
    {
        while (true)
        {
            var node = LNDNodePool.ReadyNodes.FirstOrDefault(x => x.LocalAlias == alias);
            if (node != null)
                return node;
            await Task.Delay(250);
        }
    }

    public async Task<LNDNodeConnection> WaitUntilAliasIsServerReady(string alias)
    {
        LNDNodeConnection? ready = null;
        while (ready == null)
        {
            ready = LNDNodePool.ReadyNodes.FirstOrDefault(x => x.LocalAlias == alias);
            await Task.Delay(100);
        }

        while (!ready.IsServerReady) await Task.Delay(100);
        return ready.Clone();
    }

    public async Task<ChannelGraph> GetGraphFromAlias(string alias)
    {
        var node = await GetNodeFromAlias(alias);
        return await node.LightningClient.DescribeGraphAsync(new ChannelGraphRequest());
    }

    public async Task<ListChannelsResponse> GetChannelsFromAlias(string alias)
    {
        var node = await GetNodeFromAlias(alias);
        return await node.LightningClient.ListChannelsAsync(new ListChannelsRequest());
    }

    public async Task<Payment?> MakeLightningPaymentFromAlias(string alias, SendPaymentRequest request)
    {
        var node = await GetNodeFromAlias(alias);
        request.NoInflightUpdates = true;
        var streamingCallResponse = node.RouterClient.SendPaymentV2(request);
        Payment? paymentResponse = null;
        await foreach (var res in streamingCallResponse.ResponseStream.ReadAllAsync()) paymentResponse = res;
        return paymentResponse;
    }


    public async Task<bool> IsInterceptorActiveForAlias(string alias)
    {
        if (!InterceptorHandlers.ContainsKey(alias)) throw new Exception("Interceptor doesn't exist");
        var node = await GetNodeFromAlias(alias);
        return InterceptorHandlers[alias].Running;
    }


    public async Task<LNDSimpleHtlcInterceptorHandler> GetInterceptor(string alias)
    {
        if (!InterceptorHandlers.ContainsKey(alias)) throw new Exception("Interceptor doesn't exist");
        var node = await GetNodeFromAlias(alias);
        return InterceptorHandlers[alias];
    }

    public async Task DelayAllHTLCsOnAlias(string alias, int delayMilliseconds)
    {
        if (InterceptorHandlers.ContainsKey(alias)) throw new Exception("Interceptor already attached");
        var node = await GetNodeFromAlias(alias);
        var nodeClone = node.Clone();
        InterceptorHandlers.Add(alias, new LNDSimpleHtlcInterceptorHandler(nodeClone, async x =>
        {
            await Task.Delay(delayMilliseconds);
            Debug.Write($"DelayAllHTLCsOnAlias: Delayed HTLC {x.PaymentHash} by {delayMilliseconds} ms.");
            return new ForwardHtlcInterceptResponse
            {
                Action = ResolveHoldForwardAction.Resume,
                IncomingCircuitKey = x.IncomingCircuitKey
            };
        }));
    }

    public async Task<PolicyUpdateResponse> UpdateChannelPolicyOnAlias(string alias, PolicyUpdateRequest req)
    {
        var node = await GetNodeFromAlias(alias);
        var policyUpdateResponse = node.LightningClient.UpdateChannelPolicy(
            req);
        return policyUpdateResponse;
    }

    public async Task<PolicyUpdateResponse> UpdateGlobalFeePolicyOnAlias(string alias,
        LNUnitNetworkDefinition.Channel c)
    {
        return await UpdateChannelPolicyOnAlias(alias, new PolicyUpdateRequest
        {
            Global = true,
            BaseFeeMsat = c.BaseFeeMsat.GetValueOrDefault(),
            FeeRatePpm = c.FeeRatePpm.GetValueOrDefault(),
            MinHtlcMsat = c.MinHtlcMsat.GetValueOrDefault(),
            MinHtlcMsatSpecified = c.MinHtlcMsat.HasValue,
            MaxHtlcMsat = c.MaxHtlcMsat.GetValueOrDefault(),
            TimeLockDelta = c.TimeLockDelta
        });
    }

    /// <summary>
    ///     Only works if single channel in that direction
    /// </summary>
    /// <param name="local">local alias</param>
    /// <param name="remote">remote alias</param>
    /// <returns></returns>
    public IEnumerable<ChannelPoint> GetChannelPointFromAliases(string local, string remote)
    {
        return Configuration.LNDNodes.Where(x => x.Name == local).SelectMany(x => x.Channels)
            .Where(x => x.RemoteName == remote).Select(x => x.ChannelPoint);
    }

    public void CancelInterceptorOnAlias(string alias)
    {
        try
        {
            InterceptorHandlers[alias].Cancel();
            InterceptorHandlers.RemoveKey(alias);
        }
        catch (Exception e)
        {
        }
    }

    public void CancelAllInterceptors()
    {
        foreach (var i in InterceptorHandlers.Values) i.Cancel();
        InterceptorHandlers.Clear();
    }

    public async Task NewBlock(int count = 1)
    {
        await BitcoinRpcClient.GenerateAsync(count);
    }
}

public static class LNUnitBuilderExtensions
{
    public static async Task<string?> GetGitlabRunnerNetworkId(this DockerClient _client)
    {
        var listContainers = await _client.Containers.ListContainersAsync(new ContainersListParameters());
        var x = listContainers.FirstOrDefault(x =>
            x.Labels.Any(y => y.Key == "com.gitlab.gitlab-runner.type" && y.Value == "build") && x.State == "running");
        if (x != null)
            return x.NetworkSettings.Networks["bridge"].NetworkID;
        return null;
    }


    public static LNUnitBuilder AddBitcoinCoreNode(this LNUnitBuilder b, string name = "miner",
        string image = "polarlightning/bitcoind", string tag = "27.0", bool txIndex = true, bool pullImage = true)
    {
        return b.AddBitcoinCoreNode(new LNUnitNetworkDefinition.BitcoinNode
        {
            Image = image,
            Tag = tag,
            Name = name,
            PullImage = pullImage,
            EnvironmentVariables = new Dictionary<string, string>(),
            Cmd = new List<string>
            {
                @"bitcoind",
                @"-server=1",
                $"-{b.Configuration.Network}=1",
                @"-rpcauth=bitcoin:c8c8b9740a470454255b7a38d4f38a52$e8530d1c739a3bb0ec6e9513290def11651afbfd2b979f38c16ec2cf76cf348a",
                @"-debug=1",
                @"-zmqpubrawblock=tcp://0.0.0.0:28334",
                @"-zmqpubrawtx=tcp://0.0.0.0:28335",
                @"-zmqpubhashblock=tcp://0.0.0.0:28336",
                $"-txindex={(txIndex ? "1" : "0")}",
                @"-dnsseed=0",
                @"-upnp=0",
                @"-rpcbind=0.0.0.0",
                @"-rpcallowip=0.0.0.0/0",
                @"-rpcport=18443",
                @"-rest",
                "-rpcworkqueue=1024",
                @"-listen=1",
                @"-listenonion=0",
                @"-fallbackfee=0.0002"
            }
        });
    }

    public static LNUnitBuilder AddBitcoinCoreNode(this LNUnitBuilder b, LNUnitNetworkDefinition.BitcoinNode n)
    {
        b.Configuration.BTCNodes.Add(n);
        return b;
    }

    public static LNUnitBuilder AddPolarLNDNode(this LNUnitBuilder b, string aliasHostname,
        List<LNUnitNetworkDefinition.Channel>? channels = null, string bitcoinMinerHost = "miner",
        string rpcUser = "bitcoin", string rpcPass = "bitcoin", string imageName = "polarlightning/lnd",
        string tagName = "0.17.4-beta", bool acceptKeysend = true, bool pullImage = true, bool mapTotmp = false,
        bool gcInvoiceOnStartup = false, bool gcInvoiceOnFly = false, string? postgresDSN = null,
        string lndRoot = "/home/lnd/.lnd", bool lndkSupport = false, bool nativeSql = false, bool storeFinalHtlcResolutions = false)
    {
        var cmd = new List<string>
        {
            @"lnd",
            "--maxpendingchannels=10",
            @"--noseedbackup",
            @"--trickledelay=5000",
            $"--alias={aliasHostname}",
            $"--tlsextradomain={aliasHostname}",
            @"--listen=0.0.0.0:9735",
            @"--rpclisten=0.0.0.0:10009",
            @"--restlisten=0.0.0.0:8080",
            @"--bitcoin.active",
            $"--bitcoin.{b.Configuration.Network}",
            @"--bitcoin.node=bitcoind",
            $"--bitcoind.rpchost={bitcoinMinerHost}",
            $"--bitcoind.rpcuser={rpcUser}",
            $"--bitcoind.rpcpass={rpcPass}",
            $"--bitcoind.zmqpubrawblock=tcp://{bitcoinMinerHost}:28334",
            $"--bitcoind.zmqpubrawtx=tcp://{bitcoinMinerHost}:28335",
            "--db.bolt.auto-compact",
            "--protocol.wumbo-channels",
            "--db.bolt.auto-compact-min-age=0",
            "--gossip.sub-batch-delay=1s",
            "--gossip.max-channel-update-burst=100",
            "--gossip.channel-update-interval=1s"
        };
        // if (nativeSql)
        // {
        //     cmd.Add("--db.use-native-sql");
        //
        // }
        if (lndkSupport) //TODO: must compile LND with 'dev' flags before can play with this
        {
            cmd.Add("--protocol.custom-message=513");
            cmd.Add("--protocol.custom-nodeann=39");
            cmd.Add("--protocol.custom-init=39");
        }

        if (storeFinalHtlcResolutions)
        {
            cmd.Add("--store-final-htlc-resolutions");
        }
        if (!postgresDSN.IsEmpty())
        {
            cmd.Add("--db.backend=postgres");
            cmd.Add($"--db.postgres.dsn={postgresDSN}");
            cmd.Add("--db.postgres.timeout=300s");
            cmd.Add("--db.postgres.maxconnections=16");
        }

        if (gcInvoiceOnStartup) cmd.Add("--gc-canceled-invoices-on-startup");
        if (gcInvoiceOnFly) cmd.Add("--gc-canceled-invoices-on-the-fly");

        if (acceptKeysend) cmd.Add(@"--accept-keysend");

        var node = new LNUnitNetworkDefinition.LndNode
        {
            Image = imageName,
            Tag = tagName,
            Name = aliasHostname,
            BitcoinBackendName = "miner",
            EnvironmentVariables = new Dictionary<string, string>
            {
                { "FLAG", "true" }
            },
            Cmd = cmd,
            PullImage = pullImage,
            Channels = channels ?? new List<LNUnitNetworkDefinition.Channel>()
        };
        if (mapTotmp)
        {
            var dir = $"/tmp/lnunit/{aliasHostname}";
            if (Directory.Exists(dir)) RecursiveDelete(new DirectoryInfo(dir));
            Directory.CreateDirectory($"/tmp/lnunit/{aliasHostname}");

            node.Binds = new List<string> { $"{Path.GetFullPath(dir)}:{lndRoot}/" };
        }

        return b.AddLNDNode(node);
    }

    public static LNUnitBuilder AddPolarEclairNode(this LNUnitBuilder b, string aliasHostname,
        List<LNUnitNetworkDefinition.Channel>? channels = null, string bitcoinMinerHost = "miner",
        string rpcUser = "bitcoin", string rpcPass = "bitcoin", string imageName = "polarlightning/eclair",
        string tagName = "0.6.0", bool acceptKeysend = true, bool pullImage = true, bool mapTotmp = false,
        bool gcInvoiceOnStartup = false, bool gcInvoiceOnFly = false)
    {
        var cmd = new List<string>
        {
            "polar-eclair",
            $"--node-alias={aliasHostname}",
            $"--server.public-ips.0={aliasHostname}",
            "--server.port=9735",
            "--api.enabled=true",
            "--api.binding-ip=0.0.0.0",
            "--api.port=8080",
            $"--api.password={rpcPass}",
            "--chain=regtest",
            $"--bitcoind.host={bitcoinMinerHost}",
            "--bitcoind.rpcport=18443",
            $"--bitcoind.rpcuser={rpcUser}",
            $"--bitcoind.rpcpassword={rpcPass}",
            $"--bitcoind.zmqblock=tcp://{bitcoinMinerHost}:28334",
            $"--bitcoind.zmqtx=tcp://{bitcoinMinerHost}:28335",
            "--datadir=/home/eclair/.eclair",
            "--printToConsole=true",
            "--on-chain-fees.feerate-tolerance.ratio-low=0.00001",
            "--on-chain-fees.feerate-tolerance.ratio-high=10000.0"
        };


        var node = new LNUnitNetworkDefinition.EclairNode
        {
            Image = imageName,
            Tag = tagName,
            Name = aliasHostname,
            BitcoinBackendName = "miner",
            EnvironmentVariables = new Dictionary<string, string>
            {
                { "FLAG", "true" }
            },
            Cmd = cmd,
            PullImage = pullImage,
            Channels = channels ?? new List<LNUnitNetworkDefinition.Channel>()
        };
        if (mapTotmp)
        {
            var dir = $"/tmp/lnunit/{aliasHostname}";
            if (Directory.Exists(dir)) RecursiveDelete(new DirectoryInfo(dir));
            Directory.CreateDirectory($"/tmp/lnunit/{aliasHostname}");

            node.Binds = new List<string> { $"{Path.GetFullPath(dir)}:/home/eclair/.eclair/" };
        }

        b.Configuration.EclairNodes.Add(node);
        return b;
    }

    public static LNUnitBuilder AddPolarCLNNode(this LNUnitBuilder b, string aliasHostname,
        List<LNUnitNetworkDefinition.Channel>? channels = null, string bitcoinMinerHost = "miner",
        string rpcUser = "bitcoin", string rpcPass = "bitcoin", string imageName = "polarlightning/clightning",
        string tagName = "0.10.0", bool acceptKeysend = true, bool pullImage = true, bool mapTotmp = false,
        bool gcInvoiceOnStartup = false, bool gcInvoiceOnFly = false)
    {
        var cmd = new List<string>
        {
            "lightningd",
            $"--alias={aliasHostname}",
            $"--addr={aliasHostname}",
            "--network=regtest",
            $"--bitcoin-rpcuser={rpcUser}",
            $"--bitcoin-rpcpassword={rpcPass}",
            $"--bitcoin-rpcconnect={bitcoinMinerHost}",
            "--bitcoin-rpcport=18443",
            "--log-level=debug",
            "--dev-bitcoind-poll=2",
            "--dev-fast-gossip",
            "--plugin=/opt/c-lightning-rest/plugin.js",
            "--rest-port=8080",
            "--rest-protocol=http"
        };


        var node = new LNUnitNetworkDefinition.CLNNode
        {
            Image = imageName,
            Tag = tagName,
            Name = aliasHostname,
            BitcoinBackendName = "miner",
            EnvironmentVariables = new Dictionary<string, string>
            {
                { "FLAG", "true" }
            },
            Cmd = cmd,
            PullImage = pullImage,
            Channels = channels ?? new List<LNUnitNetworkDefinition.Channel>()
        };
        if (mapTotmp)
        {
            var dir = $"/tmp/lnunit/{aliasHostname}";
            if (Directory.Exists(dir)) RecursiveDelete(new DirectoryInfo(dir));
            Directory.CreateDirectory($"/tmp/lnunit/{aliasHostname}");

            node.Binds = new List<string> { $"{Path.GetFullPath(dir)}:/home/eclair/.eclair/" };
        }

        b.Configuration.CLNNodes.Add(node);
        return b;
    }

    public static LNUnitBuilder AddLightningTerminalNode(this LNUnitBuilder b, string aliasHostname,
        List<LNUnitNetworkDefinition.Channel>? channels = null, string bitcoinMinerHost = "miner",
        string rpcUser = "bitcoin", string rpcPass = "bitcoin", string imageName = "lightninglabs/lightning-terminal",
        string tagName = "v0.12.2-alpha", bool acceptKeysend = true, bool pullImage = true)
    {
        var cmd = new List<string>
        {
            //"litd",
            "--autopilot.disable",
            "--httpslisten=0.0.0.0:8443",
            "--uipassword=1235678!",
            "--network=regtest",
            "--lnd-mode=integrated",
            "--lnd.maxpendingchannels=10",
            @"--lnd.noseedbackup",
            @"--lnd.trickledelay=5000",
            $"--lnd.alias={aliasHostname}",
            $"--lnd.tlsextradomain={aliasHostname}",
            @"--lnd.listen=0.0.0.0:9735",
            @"--lnd.rpclisten=0.0.0.0:10009",
            @"--lnd.restlisten=0.0.0.0:8080",
            @"--lnd.bitcoin.active",
            $"--lnd.bitcoin.{b.Configuration.Network}",
            @"--lnd.bitcoin.node=bitcoind",
            $"--lnd.bitcoind.rpchost={bitcoinMinerHost}",
            $"--lnd.bitcoind.rpcuser={rpcUser}",
            $"--lnd.bitcoind.rpcpass={rpcPass}",
            $"--lnd.bitcoind.zmqpubrawblock=tcp://{bitcoinMinerHost}:28334",
            $"--lnd.bitcoind.zmqpubrawtx=tcp://{bitcoinMinerHost}:28335",
            "--loop-mode=integrated",
            "--pool-mode=integrated",
            "--loop.server.host=loopserver:11009",
            "--loop.server.notls",
            "--pool.auctionserver=test.pool.lightning.finance:12010",
            "--lnd.gossip.sub-batch-delay=1s",
            "--lnd.gossip.max-channel-update-burst=100",
            "--lnd.gossip.channel-update-interval=1s"
            // "--faraday.min_monitored=48h",
            // "--faraday.connect_bitcoin",
            // $"--faraday.bitcoin.host={bitcoinMinerHost} ",
            // $"--faraday.bitcoin.user={rpcUser}",
            // $"--faraday.bitcoin.password={rpcPass}",
            // @"lnd",
        };
        if (acceptKeysend) cmd.Add(@"--lnd.accept-keysend");
        return b.AddLNDNode(new LNUnitNetworkDefinition.LndNode
        {
            Image = imageName,
            Tag = tagName,
            Name = aliasHostname,
            BitcoinBackendName = "miner",
            EnvironmentVariables = new Dictionary<string, string>
            {
                { "FLAG", "true" }
            },
            Cmd = cmd,
            PullImage = pullImage,
            Channels = channels ?? new List<LNUnitNetworkDefinition.Channel>()
        });
    }

    public static LNUnitBuilder AddLNDNode(this LNUnitBuilder b, LNUnitNetworkDefinition.LndNode n)
    {
        b.Configuration.LNDNodes.Add(n);
        return b;
    }

    public static LNUnitBuilder AddLoopServerRegtest(this LNUnitBuilder b,
        string aliasHostname = "loopserver",
        List<LNUnitNetworkDefinition.Channel>? channels = null, string bitcoinMinerHost = "miner",
        string rpcUser = "bitcoin", string rpcPass = "bitcoin", string imageName = "polarlightning/lnd",
        string tagName = "0.16.0-beta", bool acceptKeysend = true, bool pullImage = true)
    {
        /*
         * $ docker pull lightninglabs/loopserver:latest
$ docker run -d \
    -p 11009:11009 \
    -v /some/dir/to/lnd:/root/.lnd \
    lightninglabs/loopserver:latest \
      daemon \
      --maxamt=5000000 \
      --lnd.host=some-lnd-node:10009 \
      --lnd.macaroondir=/root/.lnd/data/chain/bitcoin/regtest \
      --lnd.tlspath=/root/.lnd/tls.cert \
         */
        b.AddPolarLNDNode($"{aliasHostname}-lnd");
        if (Directory.Exists("./loopserver-test")) RecursiveDelete(new DirectoryInfo("./loopserver-test"));

        Directory.CreateDirectory("./loopserver-test");
        var x = b.Configuration.LNDNodes.First(x => x.Name == $"{aliasHostname}-lnd");
        b.Configuration.LoopServer = x;
        b.Configuration.LNDNodes.Remove(x);
        x.DependentContainers.Add(
            new LNUnitNetworkDefinition.GenericDockerNode
            {
                Image = "lightninglabs/loopserver",
                Tag = "latest",
                Name = aliasHostname,
                Cmd = new List<string>
                {
                    "daemon",
                    "--maxamt=5000000",
                    "--lnd.host=loopserver-lnd",
                    "--lnd.macaroondir=/home/lnd/.lnd/",
                    "--lnd.tlspath=/home/lnd/.lnd/tls.cert",
                    "--maxamt=5000000",
                    $"--bitcoin.host={bitcoinMinerHost}:18443",
                    $"--bitcoin.user={rpcUser}",
                    $"--bitcoin.password={rpcPass}",
                    $"--bitcoin.zmqpubrawblock=tcp://{bitcoinMinerHost}:28334",
                    $"--bitcoin.zmqpubrawtx=tcp://{bitcoinMinerHost}:28335"
                },
                ExposedPorts = new Dictionary<string, EmptyStruct>
                {
                    { "11009", new EmptyStruct() }
                },

                Binds = new List<string> { $"{Path.GetFullPath("./loopserver-test/")}:/home/lnd/.lnd/" },

                PullImage = true
            });
        return b;
    }

    private static void RecursiveDelete(DirectoryInfo baseDir)
    {
        if (!baseDir.Exists)
            return;

        foreach (var dir in baseDir.EnumerateDirectories()) RecursiveDelete(dir);

        baseDir.Delete(true);
    }
}