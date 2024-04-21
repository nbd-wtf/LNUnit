using BTCPayServer.Lightning;
using BTCPayServer.Lightning.Eclair;

namespace LNUnit.Eclair;

public class EclairNodeConnection : IDisposable
{
    private readonly EclairSettings _settings;

    public EclairNodeConnection(EclairSettings s)
    {
        _settings = s;
        Host = _settings.Host;
        Setup().GetAwaiter().GetResult();
    }

    public string LocalNodePubKey { get; internal set; }

    public byte[] LocalNodePubKeyBytes => Convert.FromHexString(LocalNodePubKey);
    public string LocalAlias { get; internal set; }
    public EclairClientV2 NodeClient { get; internal set; }
    public string Host { get; internal set; }

    public void Dispose()
    {
        // TODO release managed resources here
    }

    private async Task Setup()
    {
        // ILightningClientFactory factory = new LightningClientFactory(_settings.Network);
        //NodeClient = factory.Create(_settings.BtcPayConnectionString);
        NodeClient = new EclairClientV2(_settings.Uri, _settings.EclairPassword, _settings.Network);
        var info = await NodeClient.GetInfo();
        LocalAlias = info.Alias;
        LocalNodePubKey = info.NodeId.ToString();
    }
}