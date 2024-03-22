namespace LNUnit.LND;

public static class LNDNodePoolConfigBuilder
{
    public static LNDNodePoolConfig AddNode(this LNDNodePoolConfig config, LNDNodeConnection connection)
    {
        //TODO: verify not already in here        
        config.Nodes.Add(connection);
        return config;
    }

    public static LNDNodePoolConfig AddConnectionSettings(this LNDNodePoolConfig config, LNDSettings settings)
    {
        //TODO: verify not already in here        
        config.ConnectTo.Add(settings);
        return config;
    }

    public static LNDNodePoolConfig UpdateReadyStatesPeriod(this LNDNodePoolConfig config, int period)
    {
        config.UpdateReadyStatesPeriod = period;
        return config;
    }
}