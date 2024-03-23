using Docker.DotNet.Models;
using Lnrpc;

namespace LNUnit.Setup;

public class LNUnitNetworkDefinition
{
    /// <summary>
    ///     Base name of network
    /// </summary>
    public string BaseName { get; set; } = "unit_test";

    /// <summary>
    ///     Loaded by builder from Docker if not
    /// </summary>
    public string DockerNetworkId { get; set; }

    public List<BitcoinNode> BTCNodes { get; set; } = new();
    public List<LndNode> LNDNodes { get; set; } = new();
    public List<EclairNode> EclairNodes { get; set; } = new();
    public List<CLNNode> CLNNodes { get; set; } = new();
    public LndNode? LoopServer { get; set; } = null;

    //  public List<GenericDockerNode> OtherDockerContainer { get; set; } = new();
    public string Network { get; set; } = "regtest"; //default regtest


    /// <summary>
    ///     Generic Docker settings to build a container
    /// </summary>
    public class GenericDockerNode
    {
        public string Name { get; set; }
        public string Image { get; set; }
        public string Tag { get; set; } = "latest";
        public IDictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();
        public List<string> Cmd { get; set; } = new();
        public List<string> Binds { get; set; } = new();

        public bool PullImage { get; set; } = false;

        /// <summary>
        ///     Loaded by builder from Docker
        /// </summary>
        internal string DockerContainerId { get; set; }

        public IDictionary<string, EmptyStruct> ExposedPorts { get; set; } = new Dictionary<string, EmptyStruct>();
        public IDictionary<string, EmptyStruct> Volumes { get; set; } = new Dictionary<string, EmptyStruct>();
    }

    public class BitcoinNode : GenericDockerNode
    {
        public string BitcoinNetwork = "regtest";
    }

    public class LndNode : GenericDockerNode
    {
        public List<Channel> Channels { get; set; } = new();
        public string BitcoinBackendName { get; set; }

        internal string? PubKey { get; set; }
        public List<GenericDockerNode> DependentContainers { get; set; } = new();
    }

    public class CLNNode : GenericDockerNode
    {
        public List<Channel> Channels { get; set; } = new();
        public string BitcoinBackendName { get; set; }

        internal string? PubKey { get; set; }
        public List<GenericDockerNode> DependentContainers { get; set; } = new();
    }

    public class EclairNode : GenericDockerNode
    {
        public List<Channel> Channels { get; set; } = new();
        public string BitcoinBackendName { get; set; }

        internal string? PubKey { get; set; }
        public List<GenericDockerNode> DependentContainers { get; set; } = new();
    }

    public class Channel
    {
        //Init Setup
        public string RemoteName { get; set; }
        public long ChannelSize { get; set; }

        public long RemotePushOnStart { get; set; }

        //Static Fees
        public long? BaseFeeMsat { get; set; }

        public uint? FeeRatePpm { get; set; } = 0;

        //HTLC Limits
        public ulong? MinHtlcMsat { get; set; }
        public ulong? MaxHtlcMsat { get; set; }

        //Active Management
        public double? MaintainLocalBalanceRatio { get; set; } //TODO: Implement this
        public uint TimeLockDelta { get; set; } = 40; // LND default

        /// <summary>
        ///     Used internally to know what channel we are
        /// </summary>
        internal ChannelPoint ChannelPoint { get; set; }
    }
}