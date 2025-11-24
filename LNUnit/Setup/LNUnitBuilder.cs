using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Google.Protobuf;
using Grpc.Core;
using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.RPC;
using Routerrpc;
using ServiceStack;
using SharpCompress.Readers;
using AddressType = Lnrpc.AddressType;
using HostConfig = Docker.DotNet.Models.HostConfig;
using Network = NBitcoin.Network;

namespace LNUnit.Setup;

public class LNUnitBuilder : IDisposable
{
    private readonly IContainerOrchestrator _orchestrator;
    private readonly ILogger<LNUnitBuilder>? _logger;
    private readonly IServiceProvider? _serviceProvider;
    private bool _loopLNDReady;
    public Dictionary<string, LNDChannelInterceptorHandler> ChannelHandlers = new();

    public Dictionary<string, LNDSimpleHtlcInterceptorHandler> InterceptorHandlers = new();

    // New primary constructor with orchestrator parameter
    public LNUnitBuilder(
        IContainerOrchestrator? orchestrator = null,
        LNUnitNetworkDefinition? config = null,
        ILogger<LNUnitBuilder>? logger = null,
        IServiceProvider? serviceProvider = null)
    {
        _orchestrator = orchestrator ?? new DockerOrchestrator();
        Configuration = config ?? new LNUnitNetworkDefinition();
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    // Legacy constructor for backward compatibility
    public LNUnitBuilder(
        LNUnitNetworkDefinition? c = null,
        ILogger<LNUnitBuilder>? logger = null,
        IServiceProvider? serviceProvider = null)
        : this(null, c, logger, serviceProvider)
    {
    }

    public int WaitForBitcoinNodeStartupTimeout { get; set; } = 30_000; //ms timeout

    public bool IsBuilt { get; internal set; }
    public bool IsDestoryed { get; internal set; }
    public RPCClient? BitcoinRpcClient { get; internal set; }
    public LNDNodePool? LNDNodePool { get; internal set; }
    public LNUnitNetworkDefinition Configuration { get; set; } = new();

    public void Dispose()
    {
        _orchestrator?.Dispose();
        if (LNDNodePool != null)
            LNDNodePool.Dispose();
    }

    // Helper method to generate network name with random suffix
    private static string GenerateNetworkName(string baseName)
    {
        var randomHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLower();
        return $"{baseName}_{randomHex}";
    }

    public static async Task<LNUnitBuilder> LoadConfigurationFile(string path)
    {
        var data = await File.ReadAllTextAsync(path).ConfigureAwait(false);
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
            await _orchestrator.StopContainerAsync(n.DockerContainerId, waitSeconds: 1).ConfigureAwait(false);
            await _orchestrator.RemoveContainerAsync(n.DockerContainerId, removeVolumes: true).ConfigureAwait(false);
        }

        foreach (var n in Configuration.LNDNodes.Where(x => !x.DockerContainerId.IsEmpty()))
        {
            await _orchestrator.StopContainerAsync(n.DockerContainerId, waitSeconds: 1).ConfigureAwait(false);
            await _orchestrator.RemoveContainerAsync(n.DockerContainerId, removeVolumes: true).ConfigureAwait(false);
        }

        if (destoryNetwork)
            await _orchestrator.DeleteNetworkAsync(Configuration.DockerNetworkId).ConfigureAwait(false);
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
        {
            var networkName = GenerateNetworkName(Configuration.BaseName);
            Configuration.DockerNetworkId = await _orchestrator.CreateNetworkAsync(networkName).ConfigureAwait(false);
        }

        //Setup BTC Nodes

        foreach (var n in Configuration.BTCNodes) //TODO: can do multiple at once
        {
            if (n.PullImage) await _orchestrator.PullImageAsync(n.Image, n.Tag).ConfigureAwait(false);

            var containerInfo = await _orchestrator.CreateContainerAsync(new ContainerCreateOptions
            {
                Name = n.Name,
                Image = n.Image,
                Tag = n.Tag,
                NetworkId = Configuration.DockerNetworkId,
                Command = n.Cmd,
                Environment = null,
                Binds = null,
                Links = null,
                Labels = new Dictionary<string, string> { { "lnunit", "bitcoin" } }
            }).ConfigureAwait(false);

            n.DockerContainerId = containerInfo.Id;
            await _orchestrator.StartContainerAsync(containerInfo.Id).ConfigureAwait(false);
            await Task.Delay(500).ConfigureAwait(false);

            //Setup wallet and basic funds
            var inspectedContainer = await _orchestrator.InspectContainerAsync(n.DockerContainerId).ConfigureAwait(false);
            var ipAddress = inspectedContainer.IpAddress;

            BitcoinRpcClient = new RPCClient("bitcoin:bitcoin", ipAddress, Bitcoin.Instance.Regtest);
            WaitForBitcoinNodeStartupTimeout = 30000;
            BitcoinRpcClient.HttpClient = new HttpClient
            { Timeout = TimeSpan.FromMilliseconds(WaitForBitcoinNodeStartupTimeout) };

            await BitcoinRpcClient.CreateWalletAsync("default", new CreateWalletOptions { LoadOnStartup = true })
                .ConfigureAwait(false);
            var utxos = await BitcoinRpcClient.GenerateAsync(200).ConfigureAwait(false);
        }


        var lndSettings = new List<LNDSettings>();
        string? loopServerId = null;

        if (Configuration.LoopServer != null)
        {
            var n = Configuration.LoopServer;
            if (n.PullImage) await _orchestrator.PullImageAsync(n.Image, n.Tag).ConfigureAwait(false);

            var containerInfo = await _orchestrator.CreateContainerAsync(new ContainerCreateOptions
            {
                Name = n.Name,
                Image = n.Image,
                Tag = n.Tag,
                NetworkId = Configuration.DockerNetworkId,
                Command = n.Cmd,
                Environment = null,
                Binds = null,
                Links = new List<string> { n.BitcoinBackendName },
                Labels = new Dictionary<string, string> { { "lnunit", "loop-lnd" } }
            }).ConfigureAwait(false);

            n.DockerContainerId = containerInfo.Id;
            await _orchestrator.StartContainerAsync(containerInfo.Id).ConfigureAwait(false);

            var inspectedContainer = await _orchestrator.InspectContainerAsync(n.DockerContainerId).ConfigureAwait(false);
            var ipAddress = inspectedContainer.IpAddress;

            var txt = await GetStringFromFS(n.DockerContainerId, $"{lndRoot}/tls.cert").ConfigureAwait(false);
            var tlsCertBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(txt));
            var data = await GetBytesFromFS(n.DockerContainerId,
                $"{lndRoot}/data/chain/bitcoin/regtest/admin.macaroon").ConfigureAwait(false);
            var adminMacaroonBase64String = Convert.ToBase64String(data);

            var lndConfig = new LNDSettings
            {
                GrpcEndpoint = $"https://{ipAddress}:10009/",
                MacaroonBase64 = adminMacaroonBase64String,
                TlsCertBase64 = tlsCertBase64
            };
            //lndSettings.Add(lndConfig);
            foreach (var dependentContainer in n.DependentContainers)
            {
                if (dependentContainer.PullImage)
                    await _orchestrator.PullImageAsync(dependentContainer.Image, dependentContainer.Tag)
                        .ConfigureAwait(false);

                var depContainerInfo = await _orchestrator.CreateContainerAsync(new ContainerCreateOptions
                {
                    Name = dependentContainer.Name,
                    Image = dependentContainer.Image,
                    Tag = dependentContainer.Tag,
                    NetworkId = Configuration.DockerNetworkId,
                    Command = dependentContainer.Cmd,
                    Environment = null,
                    Binds = dependentContainer.Binds,
                    Links = new List<string> { n.BitcoinBackendName, "loopserver-lnd", "alice" },
                    Labels = new Dictionary<string, string> { { "lnunit", "loopserver" } },
                    ExposedPorts = dependentContainer.ExposedPorts?.Keys
                        .Select(portStr => int.Parse(portStr.Split('/')[0]))
                        .ToList()
                }).ConfigureAwait(false);

                loopServerId = depContainerInfo.Id;
                dependentContainer.DockerContainerId = depContainerInfo.Id;
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

                if (loopServerId != null)
                    await _orchestrator.StartContainerAsync(loopServerId).ConfigureAwait(false);
            }
        }

        //Setup LND Nodes
        foreach (var n in Configuration.LNDNodes) //TODO: can do multiple at once
        {
            if (n.PullImage) await _orchestrator.PullImageAsync(n.Image, n.Tag).ConfigureAwait(false);

            var containerInfo = await _orchestrator.CreateContainerAsync(new ContainerCreateOptions
            {
                Name = n.Name,
                Image = n.Image,
                Tag = n.Tag,
                NetworkId = Configuration.DockerNetworkId,
                Command = n.Cmd,
                Environment = null,
                Binds = n.Binds.Any() ? n.Binds : null,
                Links = new List<string> { n.BitcoinBackendName },
                Labels = new Dictionary<string, string> { { "lnunit", "lnd" } }
            }).ConfigureAwait(false);

            n.DockerContainerId = containerInfo.Id;
            await _orchestrator.StartContainerAsync(containerInfo.Id).ConfigureAwait(false);

            //Not always having IP yet - poll until available
            var ipAddress = string.Empty;
            while (ipAddress.IsEmpty())
            {
                var inspectedContainer = await _orchestrator.InspectContainerAsync(n.DockerContainerId).ConfigureAwait(false);
                ipAddress = inspectedContainer.IpAddress ?? string.Empty;
                if (ipAddress.IsEmpty())
                    await Task.Delay(100).ConfigureAwait(false);
            }

            var basePath =
                !n.Image.Contains("lightning-terminal")
                    ? lndRoot
                    : "/root/lnd/.lnd"; // "/home/lnd/.lnd" : "/root/lnd/.lnd";
            if (n.Image.Contains("lightning-terminal")) await Task.Delay(2000);
            var txt = await GetStringFromFS(n.DockerContainerId, $"{basePath}/tls.cert").ConfigureAwait(false);
            var tlsCertBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(txt));
            var data = await GetBytesFromFS(n.DockerContainerId,
                $"{basePath}/data/chain/bitcoin/regtest/admin.macaroon").ConfigureAwait(false);
            var adminMacaroonBase64String = Convert.ToBase64String(data);


            // var adminMacaroonTar =
            //     await GetTarStreamFromFS(n.DockerContainerId,
            //         $"{basePath}/data/chain/bitcoin/regtest/admin.macaroon");


            lndSettings.Add(new LNDSettings
            {
                GrpcEndpoint = $"https://{ipAddress}:10009/",
                MacaroonBase64 = adminMacaroonBase64String,
                TlsCertBase64 = tlsCertBase64
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
                await Task.Delay(250).ConfigureAwait(false);
                if (cancelSource.IsCancellationRequested) throw new Exception("CANCELED");
            }

            //Cross Connect Peers
            foreach (var localNode in LNDNodePool.ReadyNodes.ToImmutableList())
            {
                var remotes = LNDNodePool.ReadyNodes.Where(x => x.LocalAlias != localNode.LocalAlias).ToImmutableList();
                foreach (var remoteNode in remotes) await ConnectPeers(localNode, remoteNode).ConfigureAwait(false);
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
                await BitcoinRpcClient.GenerateAsync(1).ConfigureAwait(false);

                BitcoinRpcClient.SendToAddress(BitcoinAddress.Create(newAddressResponse.Address, Network.RegTest),
                    Money.Parse("42.69"));
                BitcoinRpcClient.SendToAddress(BitcoinAddress.Create(newAddressResponse.Address, Network.RegTest),
                    Money.Parse("42.69"));
                await BitcoinRpcClient.GenerateAsync(1).ConfigureAwait(false);

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
                    await ConnectPeers(node, remoteNode).ConfigureAwait(false);

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
                    await BitcoinRpcClient.GenerateAsync(10).ConfigureAwait(false);
                    await WaitUntilSyncedToChain(node).ConfigureAwait(false);
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
                                }).ConfigureAwait(false);
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
            info = await node.LightningClient.GetInfoAsync(new GetInfoRequest()).ConfigureAwait(false);
        }
    }

    public async Task WaitUntilSyncedToChain(string alias)
    {
        await WaitUntilSyncedToChain(await GetNodeFromAlias(alias).ConfigureAwait(false)).ConfigureAwait(false);
    }

    public async Task<LNDSettings> GetLNDSettingsFromContainer(string containerId, string lndRoot = "/home/lnd/.lnd")
    {
        var containerInfo = await _orchestrator.InspectContainerAsync(containerId).ConfigureAwait(false);
        while (containerInfo.State != "Running")
        {
            await Task.Delay(250).ConfigureAwait(false);
            containerInfo = await _orchestrator.InspectContainerAsync(containerId).ConfigureAwait(false);
        }

        var ipAddress = containerInfo.IpAddress;

        //Wait until LND actually has files started
        var txt = await _orchestrator.ExtractTextFileAsync(containerId, $"{lndRoot}/tls.cert").ConfigureAwait(false);
        var tlsCertBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(txt));

        var data = await _orchestrator.ExtractBinaryFileAsync(containerId,
            $"{lndRoot}/data/chain/bitcoin/regtest/admin.macaroon").ConfigureAwait(false);
        var adminMacaroonBase64String = Convert.ToBase64String(data);

        return new LNDSettings
        {
            GrpcEndpoint = $"https://{ipAddress}:10009/",
            MacaroonBase64 = adminMacaroonBase64String,
            TlsCertBase64 = tlsCertBase64
        };
    }

    /// <summary>
    /// Gets file stats from container filesystem.
    /// NOTE: Docker-specific implementation. Only works with DockerOrchestrator.
    /// </summary>
    public async Task<ContainerPathStatResponse?> GetFileSize(string containerId, string filePath)
    {
        if (_orchestrator is not DockerOrchestrator dockerOrchestrator)
        {
            throw new NotSupportedException("GetFileSize is only supported with DockerOrchestrator");
        }

        try
        {
            var archResponse = await dockerOrchestrator.Client.Containers.GetArchiveFromContainerAsync(
                containerId,
                new GetArchiveFromContainerParameters
                {
                    Path = filePath
                }, true).ConfigureAwait(false);
            return archResponse.Stat;
        }
        catch (Exception e)
        {
        }

        return null;
    }

    [Obsolete("Use orchestrator file methods instead")]
    private async Task<GetArchiveFromContainerResponse?> GetTarStreamFromFS(string containerId, string filePath)
    {
        if (_orchestrator is not DockerOrchestrator dockerOrchestrator)
        {
            throw new NotSupportedException("GetTarStreamFromFS is only supported with DockerOrchestrator");
        }

        var foundFile = false;
        while (!foundFile)
        {
            try
            {
                var archResponse = await dockerOrchestrator.Client.Containers.GetArchiveFromContainerAsync(
                    containerId,
                    new GetArchiveFromContainerParameters
                    {
                        Path = filePath
                    }, false).ConfigureAwait(false);
                return archResponse;
            }
            catch (Exception e)
            {
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<bool> PutFile(string containerId, string filePath, Stream stream)
    {
        await _orchestrator.PutFileAsync(containerId, filePath, stream)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<byte[]> GetBytesFromFS(string containerId, string filePath)
    {
        // Retry until file exists (container may be starting up)
        while (true)
        {
            try
            {
                return await _orchestrator.ExtractBinaryFileAsync(containerId, filePath)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> GetStringFromFS(string containerId, string filePath)
    {
        // Retry until file exists (container may be starting up)
        while (true)
        {
            try
            {
                return await _orchestrator.ExtractTextFileAsync(containerId, filePath)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
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
            var graph = await GetGraphFromAlias(fromAlias).ConfigureAwait(false);
            if (graph.Nodes.Count < expectedNodeCount)
                await Task.Delay(250); //let the graph sync 
            else
                graphReady = true;
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
        using (var stream = tlsCertResponse.Stream.CopyToNewMemoryStream())
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
        return await _orchestrator.StopContainerAsync(alias, waitSeconds: waitBeforeKillSeconds).ConfigureAwait(false);
    }

    public async Task RestartByAlias(string alias, uint waitBeforeKillSeconds = 1, bool isLND = false,
        bool resetChannels = true, string lndRoot = "/home/lnd/.lnd")
    {
        await _orchestrator.RestartContainerAsync(alias, waitSeconds: waitBeforeKillSeconds).ConfigureAwait(false);

        if (isLND)
        {
            LNDNodePool?.AddNode(await GetLNDSettingsFromContainer(alias, lndRoot));

            var node = await WaitUntilAliasIsServerReady(alias).ConfigureAwait(false);
            //reset channels
            foreach (var nodes in Configuration.LNDNodes.Where(x => x.Name == alias))
                foreach (var c in nodes.Channels)
                {
                    var remoteNode = LNDNodePool.ReadyNodes.First(x => x.LocalAlias == c.RemoteName);
                    //Connect Peers
                    //We are doing this above now
                    await ConnectPeers(node, remoteNode).ConfigureAwait(false);

                    //Wait until we are synced to the chain so we know we have funds secured to send
                    var info = new GetInfoResponse();
                    while (!info.SyncedToChain && !info.SyncedToGraph)
                    {
                        info = await node.LightningClient.GetInfoAsync(new GetInfoRequest()).ConfigureAwait(false);
                        await Task.Delay(250);
                    }

                    info = new GetInfoResponse();
                    while (!info.SyncedToChain && !info.SyncedToGraph)
                    {
                        info = await remoteNode.LightningClient.GetInfoAsync(new GetInfoRequest()).ConfigureAwait(false);
                        await Task.Delay(250);
                    }

                    await WaitGraphReady(alias, LNDNodePool.TotalNodes).ConfigureAwait(false);
                    if (resetChannels)
                    {
                        var channelPoint = await node.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
                        {
                            NodePubkey = ByteString.CopyFrom(Convert.FromHexString(remoteNode.LocalNodePubKey)),
                            SatPerVbyte = 10,
                            LocalFundingAmount = c.ChannelSize,
                            PushSat = c.RemotePushOnStart
                        }).ConfigureAwait(false);
                        c.ChannelPoint = channelPoint;
                        //Move things along so it is confirmed
                        await BitcoinRpcClient.GenerateAsync(10).ConfigureAwait(false);
                        await WaitUntilSyncedToChain(alias).ConfigureAwait(false);
                        await WaitGraphReady(alias, LNDNodePool.TotalNodes).ConfigureAwait(false);
                        //Set fees & htlcs, TLD
                        var policySet = false;
                        var hitcount = 0;
                        while (!policySet)
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
                                    }).ConfigureAwait(false);
                                policySet = true;
                            }
                            catch (RpcException e) when (e.Status.StatusCode == StatusCode.Unknown &&
                                                         e.Status.Detail.EqualsIgnoreCase(
                                                             "channel from self node has no policy"))
                            {
                                if (hitcount == 0)
                                    //generate blocks so we do a status update for sure.
                                    await BitcoinRpcClient.GenerateAsync(145).ConfigureAwait(false);

                                hitcount++;
                                await Task.Delay(500).ConfigureAwait(false); //give it some time
                            }
                    }
                }

            //   await Task.Delay(2500).ConfigureAwait(false);
        }
    }

    public async Task<AddInvoiceResponse> GeneratePaymentRequestFromAlias(string alias, Invoice invoice)
    {
        return (await GeneratePaymentsRequestFromAlias(alias, 1, invoice).ConfigureAwait(false)).First();
        // var node = GetNodeFromAlias(alias);
        // var response = await node.LightningClient.AddInvoiceAsync(invoice);
        // return response;
    }

    public async Task<List<AddInvoiceResponse>> GeneratePaymentsRequestFromAlias(string alias, int count,
        Invoice invoice)
    {
        var node = await GetNodeFromAlias(alias).ConfigureAwait(false);
        var response = new ConcurrentBag<AddInvoiceResponse>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (x, ct) =>
            {
                var invoiceCopy = invoice.Clone();
                var result = await node.LightningClient.AddInvoiceAsync(invoiceCopy, cancellationToken: ct)
                    .ConfigureAwait(false);
                response.Add(result);
            });
        return response.ToList();
    }

    public async Task<Payment> LookupPayment(string alias, ByteString hash)
    {
        var node = await GetNodeFromAlias(alias);
        var streamingCallResponse = node.RouterClient.TrackPaymentV2(new TrackPaymentRequest
        {
            NoInflightUpdates = true,
            PaymentHash = hash
        });
        Payment? paymentResponse = null;
        await foreach (var res in streamingCallResponse.ResponseStream.ReadAllAsync().ConfigureAwait(false))
            paymentResponse = res;
        return paymentResponse!;
    }

    public async Task<Invoice?> LookupInvoice(string alias, ByteString rHash)
    {
        var node = await GetNodeFromAlias(alias).ConfigureAwait(false);
        var response = await node.LightningClient.LookupInvoiceAsync(new PaymentHash { RHash = rHash })
            .ConfigureAwait(false);
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

        while (!ready.IsServerReady) await Task.Delay(100).ConfigureAwait(false);
        return ready.Clone();
    }

    public async Task<ChannelGraph> GetGraphFromAlias(string alias)
    {
        var node = await GetNodeFromAlias(alias).ConfigureAwait(false);
        return await node.LightningClient.DescribeGraphAsync(new ChannelGraphRequest()).ConfigureAwait(false);
    }

    public async Task<ListChannelsResponse> GetChannelsFromAlias(string alias)
    {
        var node = await GetNodeFromAlias(alias).ConfigureAwait(false);
        return await node.LightningClient.ListChannelsAsync(new ListChannelsRequest()).ConfigureAwait(false);
    }

    public async Task<Payment?> MakeLightningPaymentFromAlias(string alias, SendPaymentRequest request)
    {
        var node = await GetNodeFromAlias(alias).ConfigureAwait(false);
        request.NoInflightUpdates = true;
        var streamingCallResponse = node.RouterClient.SendPaymentV2(request);
        Payment? paymentResponse = null;
        await foreach (var res in streamingCallResponse.ResponseStream.ReadAllAsync().ConfigureAwait(false))
            paymentResponse = res;
        return paymentResponse;
    }


    public async Task<bool> IsInterceptorActiveForAlias(string alias)
    {
        if (!InterceptorHandlers.ContainsKey(alias)) throw new Exception("Interceptor doesn't exist");
        var node = await GetNodeFromAlias(alias).ConfigureAwait(false);
        return InterceptorHandlers[alias].Running;
    }


    public async Task<LNDSimpleHtlcInterceptorHandler> GetInterceptor(string alias)
    {
        if (!InterceptorHandlers.ContainsKey(alias)) throw new Exception("Interceptor doesn't exist");
        var node = await GetNodeFromAlias(alias).ConfigureAwait(false);
        return InterceptorHandlers[alias];
    }

    public async Task DelayAllHTLCsOnAlias(string alias, int delayMilliseconds)
    {
        if (InterceptorHandlers.ContainsKey(alias)) throw new Exception("Interceptor already attached");
        var node = await GetNodeFromAlias(alias).ConfigureAwait(false);
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
        var node = await GetNodeFromAlias(alias).ConfigureAwait(false);
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
        }).ConfigureAwait(false);
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
        await BitcoinRpcClient.GenerateAsync(count).ConfigureAwait(false);
    }
}

public static class LNUnitBuilderExtensions
{
    public static async Task<string?> GetGitlabRunnerNetworkId(this DockerClient _client)
    {
        var listContainers = await _client.Containers.ListContainersAsync(new ContainersListParameters())
            .ConfigureAwait(false);
        var x = listContainers.FirstOrDefault(x =>
            x.Labels.Any(y => y.Key == "com.gitlab.gitlab-runner.type" && y.Value == "build") && x.State == "running");
        if (x != null)
            return x.NetworkSettings.Networks["bridge"].NetworkID;
        return null;
    }


    public static LNUnitBuilder AddBitcoinCoreNode(this LNUnitBuilder b, string name = "miner",
        string image = "polarlightning/bitcoind", string tag = "29.0", bool txIndex = true, bool pullImage = true)
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
                @"-natpmp=0", //changed in v30 was upnp=0 now invalid.
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
        string lndRoot = "/home/lnd/.lnd", bool lndkSupport = false, bool nativeSql = false,
        bool storeFinalHtlcResolutions = false)
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
        if (nativeSql) cmd.Add("--db.use-native-sql");
        if (lndkSupport) //TODO: must compile LND with 'dev' flags before can play with this
        {
            cmd.Add("--protocol.custom-message=513");
            cmd.Add("--protocol.custom-nodeann=39");
            cmd.Add("--protocol.custom-init=39");
        }

        if (storeFinalHtlcResolutions) cmd.Add("--store-final-htlc-resolutions");
        if (!postgresDSN.IsEmpty())
        {
            cmd.Add("--db.backend=postgres");
            cmd.Add($"--db.postgres.dsn={postgresDSN}");
            cmd.Add("--db.postgres.timeout=300s");
            cmd.Add("--db.postgres.maxconnections=16");
        }
        else if (postgresDSN.IsEmpty() && nativeSql)
        {
            //Sqlite mode
            cmd.Add("--db.backend=sqlite");
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