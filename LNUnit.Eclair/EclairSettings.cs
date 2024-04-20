using NBitcoin;

namespace LNUnit.Eclair;

public class EclairSettings
{
    public string Host { get; set; }
    public int Port { get; set; }
    public string EclairPassword { get; set; }
    public string BitcoinHost { get; set; }
    public string BitcoinAuth { get; set; }

    public string BtcPayConnectionString =>
        $"type=eclair;server=http://{Host}:{Port};password={EclairPassword};bitcoin-host={BitcoinHost};bitcoin-auth={BitcoinAuth}";

    public Network Network { get; set; } = Network.RegTest;
}