namespace LNUnit.Eclair;

public class EclairNodePoolConfig
{
    public List<EclairNodeConnection> Nodes { get; set; } = new();

    public double UpdateReadyStatesPeriod { get; set; } = 1;

    public List<EclairSettings> ConnectTo { get; set; } = new();

    public bool QuickStartupMode { get; set; }
}