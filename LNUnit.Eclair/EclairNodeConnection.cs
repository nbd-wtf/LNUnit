using BTCPayServer.Lightning;

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
    public ILightningClient Client { get; internal set; }
    public string Host { get; internal set; }

    public void Dispose()
    {
        // TODO release managed resources here
    }

    private async Task Setup()
    {
        ILightningClientFactory factory = new LightningClientFactory(_settings.Network);
        Client = factory.Create(_settings.BtcPayConnectionString);
        var info = await Client.GetInfo();
        LocalAlias = info.Alias;
        LocalNodePubKey = info.NodeInfoList.First().NodeId.ToString();
    }
}