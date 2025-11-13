using System.Collections.Immutable;
using System.Diagnostics;
using Grpc.Core;
using Lnrpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Routerrpc;
using ServiceStack;

namespace LNUnit.LND;

public class LNDNodePool : IDisposable
{
    private const long StartupMaxTimeMilliseconds = 10_000;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ILogger<LNDNodePool>? _logger;
    private readonly Stopwatch _runtime = Stopwatch.StartNew();
    private readonly IServiceProvider? _serviceProvider;
    private readonly List<LNDSettings> _lndNodesNotYetInitialized = new();
    private readonly List<LNDNodeConnection> _nodes = new();
    public readonly List<LNDNodeConnection> ReadyNodes = new();

    private readonly TimeSpan _updateReadyStatesPeriod;

    private bool _isDisposed;
    private bool _quickStartupMode;
    private PeriodicTimer? _rpcCheckTimer;


    public LNDNodePool(IOptionsSnapshot<LNDNodePoolConfig> lndNodePoolConfig, ILogger<LNDNodePool> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        var config = lndNodePoolConfig;
        _nodes = config.Value.Nodes;
        _updateReadyStatesPeriod = TimeSpan.FromSeconds(lndNodePoolConfig.Value.UpdateReadyStatesPeriod);
        _lndNodesNotYetInitialized.AddRange(config.Value.ConnectTo);
        TotalNodes = _nodes.Count + config.Value.ConnectTo.Count;
        _quickStartupMode = config.Value.QuickStartupMode;

        SetupTimers();
        _logger.LogDebug("LNDNodePool Created with {TotalNodes} nodes.", TotalNodes);
    }

    public LNDNodePool(LNDNodePoolConfig lndNodePoolConfig, ILogger<LNDNodePool> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        var config = lndNodePoolConfig;
        _nodes = config.Nodes;
        _updateReadyStatesPeriod = TimeSpan.FromSeconds(lndNodePoolConfig.UpdateReadyStatesPeriod);
        _lndNodesNotYetInitialized.AddRange(config.ConnectTo);
        TotalNodes = _nodes.Count + config.ConnectTo.Count;
        _quickStartupMode = config.QuickStartupMode;

        SetupTimers();
        _logger.LogDebug("LNDNodePool Created with {TotalNodes} nodes.", TotalNodes);
    }

    // [Obsolete("Going towards .NET DI Options pattern as also makes it easy to enlist logger")]
    // public LNDNodePool(List<LNDNodeConnection> nodes, int updateReadyStatesPeriod = 5)
    // {
    //     UpdateReadyStatesPeriod = TimeSpan.FromSeconds(updateReadyStatesPeriod);
    //     Nodes = nodes;
    //     SetupTimers();
    // }

    [Obsolete("Going towards .NET DI Options pattern as also makes it easy to enlist logger")]
    public LNDNodePool(List<LNDSettings> nodeSettings, int updateReadyStatesPeriod = 5, bool quickStartupMode = true)
    {
        _updateReadyStatesPeriod = TimeSpan.FromSeconds(updateReadyStatesPeriod);
        _lndNodesNotYetInitialized.AddRange(nodeSettings);
        TotalNodes = nodeSettings.Count;
        _quickStartupMode = quickStartupMode;
        SetupTimers();
    }

    public int TotalNodes { get; internal set; }

    /// <summary>
    ///     Set if you want to Persist record somewhere
    /// </summary>
    public Func<BalanceTask, Task>? SaveRebalanceAction { get; set; } = null;

    public bool AllReady => ReadyNodes.Count == TotalNodes;


    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _logger?.LogDebug("Disposing Pool with {ReadyNodeCount} Ready and {TotalNodeCount} Total",
                ReadyNodes.Count,
                TotalNodes);
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _rpcCheckTimer?.Dispose();
            _nodes.ForEach(x => x.Dispose());
        }
    }


    private void SetupNotYetInitializedNodes()
    {
        if (_lndNodesNotYetInitialized.Any())
        {
            var lndNodes = _lndNodesNotYetInitialized.CreateCopy();

            foreach (var settings in lndNodes)
                try
                {
                    var node = _serviceProvider != null
                        ? ActivatorUtilities.CreateInstance(_serviceProvider, typeof(LNDNodeConnection), settings) as
                            LNDNodeConnection
                        : new LNDNodeConnection(settings); //No logging injection
                    _nodes.Add(node!);
                    _lndNodesNotYetInitialized.Remove(
                        _lndNodesNotYetInitialized.First(x => x.GrpcEndpoint == settings.GrpcEndpoint));
                    _logger?.LogDebug("Connected to {Alias} @ {GrpcEndpoint}", node!.LocalAlias,
                        settings.GrpcEndpoint);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to initialize node @ {GrpcEndpoint}", settings.GrpcEndpoint);
                }
        }
    }

    private void SetupTimers()
    {
        _rpcCheckTimer = _quickStartupMode
            ? new PeriodicTimer(TimeSpan.FromMilliseconds(100))
            : new PeriodicTimer(_updateReadyStatesPeriod);
        Task.Run(async () => await UpdateReadyStates(), _cancellationTokenSource.Token);
        _logger?.LogDebug("UpdateReadyStates: Task Started.");
    }

    private async Task UpdateReadyStates() //TIMER
    {
        while (await _rpcCheckTimer!.WaitForNextTickAsync(_cancellationTokenSource.Token))
        {
            _logger?.LogDebug("UpdateReadyStates: Starting. Quick: {QuickMode}", _quickStartupMode);
            SetupNotYetInitializedNodes();
            foreach (var node in _nodes)
                if (ReadyNodes.Contains(node) && !IsServerActive(node))
                {
                    _logger?.LogDebug("UpdateReadyStates: {Alias} is NOT Ready, removing from pool.", node.LocalAlias);
                    ReadyNodes.Remove(node);
                }
                else
                {
                    if (!ReadyNodes.Contains(node))
                    {
                        _logger?.LogDebug("UpdateReadyStates: {Alias} is Ready, adding to pool.", node.LocalAlias);
                        ReadyNodes.Add(node);
                    }
                }

            if (_quickStartupMode && (_runtime.ElapsedMilliseconds > StartupMaxTimeMilliseconds ||
                                      ReadyNodes.Count == TotalNodes)) // shutdown if all nodes are up OR time ran out
            {
                _logger?.LogDebug("UpdateReadyStates: Quick Startup Mode disabled.");
                _quickStartupMode = false;
                _rpcCheckTimer = new PeriodicTimer(_updateReadyStatesPeriod); //use provided standard polling period.
            }

            _logger?.LogDebug("UpdateReadyStates: Done.");
        }
    }

    private bool IsServerActive(LNDNodeConnection node)
    {
        return GetState(node) is { State: WalletState.ServerActive };
    }

    // private bool IsRPCReady(LNDNodeConnection node)
    // {
    //     return GetState(node) is { State: WalletState.RpcActive };
    // }

    private GetStateResponse? GetState(LNDNodeConnection node)
    {
        try
        {
            var res = node.StateClient.GetState(new GetStateRequest(), null, DateTime.UtcNow.AddSeconds(2));
            return res;
        }
        catch (RpcException e)
        {
            _logger?.LogDebug(e, "{FunctionName} RPC Exception for {Alias}", nameof(GetState), node.LocalAlias);
            // ignored
        }
        catch (Exception e)
        {
            _logger?.LogDebug(e, "GetState Exception for {Alias}", node.LocalAlias);
            // ignored
        }

        return null;
    }

    /// <summary>
    ///     Gets next free LNDNode based on logic
    /// </summary>
    /// <returns></returns>
    public LNDNodeConnection GetLNDNodeConnection()
    {
        // this is dumb, but could do fancy stuff like push to node with the lowest load, etc.
        // we do have information to make that happen.
        return ReadyNodes.First();
    }

    
    
    /// <summary>
    ///     Takes all members in a pool and will 50/50 balance channels between them via invoice/payment method. only direct
    ///     peers, so 0 fees.
    /// </summary>
    /// <param name="deltaThreshold">minimum threshold to perform rebalance</param>
    /// <returns></returns>
    public async Task<PoolRebalanceStats> RebalanceNodePool(int deltaThreshold = 100_000)
    {
        _logger?.LogDebug("RebalanceNodePool Start");

        //Get all channels across pool
        //Filter for all cross-links
        //select origin from all with >50% balance
        var rebalanceTasks = await GetInteralNodeEvenBalaceTasks(this, deltaThreshold);
        var stats = new PoolRebalanceStats();
        foreach (var t in rebalanceTasks)
        {
            var src = ReadyNodes.First(x => x.LocalNodePubKey == t.SrcPk);
            var dest = ReadyNodes.First(x => x.LocalNodePubKey == t.DestPk);
            var paymentHash = await InvoicePayRebalance(src, dest, t.Amount, _logger, t.ChanId);
            if (!paymentHash.IsNullOrEmpty())
            {
                //update stats
                stats.TotalAmount += (ulong)t.Amount;
                stats.TotalRebalanceCount++;
                //Updated PaymentHash info
                t.PaymentHash = Convert.FromHexString(paymentHash!);
                //write to db
                if (SaveRebalanceAction != null) await SaveRebalanceAction(t);
                stats.Tasks.Add(t);
            }
        }

        return stats;
    }

    /// <summary>
    ///     Simple cross-node rebalancing between nodes: generate invoice on destination, pay on origin
    /// </summary>
    /// <param name="src">Funds going from local to remote</param>
    /// <param name="dest">Receiving funds from remote to local balance</param>
    /// <param name="valueInSataoshis"></param>
    /// <param name="logger"></param>
    /// <param name="channelId"></param>
    /// <returns>PaymentHash if successful</returns>
    public static async Task<string?> InvoicePayRebalance(LNDNodeConnection src, LNDNodeConnection dest,
        long valueInSataoshis,
        ILogger? logger = null, ulong channelId = 0)
    {
        try
        {
            logger?.LogDebug(
                "InvoicePayRebalance: Attemping rebalance of {Value} sats from {Source} to {Destination}.",
                valueInSataoshis,
                src.LocalAlias, dest.LocalAlias);

            var invoice = await dest.LightningClient.AddInvoiceAsync(new Invoice
            {
                Value = valueInSataoshis,
                Memo = "InvoicePayRebalance",
                Expiry = 60 //1 minute
            });
            logger?.LogDebug("InvoicePayRebalance: {PaymentRequest} for {Value} sats from {Source}",
                invoice.PaymentRequest, valueInSataoshis, src.LocalAlias);

            var payment = new SendPaymentRequest
            {
                PaymentRequest = invoice.PaymentRequest,
                TimeoutSeconds = 20,
                NoInflightUpdates = true
            };
            if (channelId != 0) payment.OutgoingChanIds.Add(channelId);
            var streamingCallResponse = src.RouterClient.SendPaymentV2(payment);
            await streamingCallResponse.ResponseStream.MoveNext();
            // var response = streamingCallResponse.ResponseStream.Current.Status == Payment.Types.PaymentStatus.Succeeded;
            logger?.LogDebug(
                "InvoicePayRebalance: {PaymentRequest} for {Value} sats from {Source} paid by {PaymentHash}",
                invoice.PaymentRequest, valueInSataoshis,
                src.LocalAlias, streamingCallResponse.ResponseStream.Current.PaymentHash);

            return streamingCallResponse.ResponseStream.Current.PaymentHash;
        }
        catch (Exception)
        {
            return null;
        }
    }


    /// <summary>
    ///     Assemble list of pool 50/50 split balance tasks for all channels between pool members.
    ///     This will push balance from higher node to lower to try to get to 0 difference.
    /// </summary>
    /// <param name="pool"></param>
    /// <param name="deltaThreshold"></param>
    /// <returns>List of balance tasks to get to 50/50 split</returns>
    public static async Task<List<BalanceTask>> GetInteralNodeEvenBalaceTasks(LNDNodePool pool,
        int deltaThreshold = 100_000)
    {
        var balanceList = new List<BalanceTask>();
        var ourPoolMemberPKs = pool.ReadyNodes.Select(x => x.LocalNodePubKey).ToImmutableList();
        foreach (var node in pool.ReadyNodes.ToImmutableList())
        {
            var activeChannels = await node.LightningClient.ListChannelsAsync(new ListChannelsRequest
            {
                ActiveOnly = true,
                PeerAliasLookup = false
            });
            //Filter to channels with internal pool peers
            var poolPeerChannels =
                activeChannels.Channels.Where(x => ourPoolMemberPKs.Contains(x.RemotePubkey)).ToList();

            foreach (var peerChannel in poolPeerChannels)
            {
                //Check that we don't already have a balanceTask for this from its remote.
                if (balanceList.Any(x => x.ChannelPoint == peerChannel.ChannelPoint)) continue; //skip
                if (Math.Abs(peerChannel.LocalBalance - peerChannel.RemoteBalance) < deltaThreshold)
                    continue; //skip below limit

                //Check which is higher balance that is our source
                //Then create a balance task for this
                var even = peerChannel.Capacity / 2;

                if (peerChannel.RemoteBalance > peerChannel.LocalBalance)
                {
                    var b = new BalanceTask
                    {
                        ChanId = peerChannel.ChanId,
                        SrcPk = peerChannel.RemotePubkey,
                        DestPk = node.LocalNodePubKey,
                        Amount = peerChannel.RemoteBalance - even,
                        ChannelPoint = peerChannel.ChannelPoint
                    };
                    var run = AdjustLimits(b, peerChannel);
                    if (run)
                        balanceList.Add(b);
                }
                else if (peerChannel.RemoteBalance < peerChannel.LocalBalance)
                {
                    var b = new BalanceTask
                    {
                        ChanId = peerChannel.ChanId,
                        SrcPk = node.LocalNodePubKey,
                        DestPk = peerChannel.RemotePubkey,
                        Amount = peerChannel.LocalBalance - even,
                        ChannelPoint = peerChannel.ChannelPoint
                    };
                    var run = AdjustLimits(b, peerChannel);
                    if (run)
                        balanceList.Add(b);
                }
                //same, do nothing
            }
        }

        bool AdjustLimits(BalanceTask balanceTask, Channel channel)
        {
            var max = Math.Max((long)(channel.LocalConstraints.MaxPendingAmtMsat / 1000),
                (long)(channel.RemoteConstraints.MaxPendingAmtMsat / 1000));
            var min = Math.Min((long)(channel.LocalConstraints.MinHtlcMsat / 1000),
                (long)(channel.RemoteConstraints.MinHtlcMsat / 1000));
            if (balanceTask.Amount > max)
                balanceTask.Amount = max - 1;
            else if (balanceTask.Amount < min) return false;

            return true;
        }

        return balanceList;
    }

    /// <summary>
    ///     Gets specific node based on pubkey, if not found returns null
    /// </summary>
    /// <param name="pubkey"></param>
    /// <returns></returns>
    public LNDNodeConnection? GetLNDNodeConnection(string pubkey)
    {
        foreach (var node in _nodes)
            if (node.LocalNodePubKey == pubkey)
                return node;
        return null;
    }

    /// <summary>
    ///     Remove node from pool
    /// </summary>
    /// <param name="node"></param>
    public void RemoveNode(LNDNodeConnection node)
    {
        _nodes.Remove(node);
        ReadyNodes.Remove(node);
    }

    /// <summary>
    ///     Add new node to pool. Don't do stupid shit like add same node settings for now.
    /// </summary>
    /// <param name="nodeSettings"></param>
    public void AddNode(LNDSettings nodeSettings)
    {
        _lndNodesNotYetInitialized.Add(nodeSettings);
    }

    public class PoolRebalanceStats
    {
        public int TotalRebalanceCount { get; set; }
        public ulong TotalAmount { get; set; }
        public List<BalanceTask> Tasks { get; set; } = new();
    }

    public record BalanceTask
    {
        public required string ChannelPoint { get; set; }
        public ulong ChanId { get; set; }
        public required string SrcPk { get; set; }
        public required string DestPk { get; set; }
        public long Amount { get; set; }
        public byte[]? PaymentHash { get; set; }
    }
}