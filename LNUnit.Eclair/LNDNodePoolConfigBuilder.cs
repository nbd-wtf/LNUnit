using LNUnit.Eclair;

namespace LNUnit.LND;

public static class EclairNodePoolConfigBuilder
{
    public static EclairNodePoolConfig AddNode(this EclairNodePoolConfig config, EclairNodeConnection connection)
    {
        //TODO: verify not already in here        
        config.Nodes.Add(connection);
        return config;
    }

    public static EclairNodePoolConfig AddConnectionSettings(this EclairNodePoolConfig config, EclairSettings settings)
    {
        //TODO: verify not already in here        
        config.ConnectTo.Add(settings);
        return config;
    }

    public static EclairNodePoolConfig UpdateReadyStatesPeriod(this EclairNodePoolConfig config, int period)
    {
        config.UpdateReadyStatesPeriod = period;
        return config;
    }
}