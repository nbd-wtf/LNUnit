using System.ComponentModel.DataAnnotations;

namespace LNUnit.LND;

public class LNDNodePoolConfig
{
    /// <summary>
    ///     Node connection strings to connect to
    /// </summary>
    public List<LNDSettings> ConnectTo { get; } = new();

    /// <summary>
    ///     Existing Connections to enlist
    /// </summary>
    public List<LNDNodeConnection> Nodes { get; } = new();

    /// <summary>
    ///     How often in seconds we validate node is READY
    /// </summary>
    [Range(1, 60, ErrorMessage = "Value for {0} must be between {1} and {2}.")]
    public int UpdateReadyStatesPeriod { get; set; } = 5;

    /// <summary>
    ///     Will poll at 100ms intervals until timeout of 10s or all nodes connected then use
    ///     UpdateReadyStatesPeriod value for timer.
    /// </summary>
    public bool QuickStartupMode { get; } = true;
}