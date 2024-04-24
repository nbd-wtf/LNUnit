namespace BTCPayServer.Lightning.Eclair.Models;

public class PayInvoiceRequestV2
{
    public string Invoice { get; set; }
    public long? AmountMsat { get; set; }
    public int? MaxAttempts { get; set; }
    public int? MaxFeePct { get; set; }
    public long? MaxFeeFlatSat { get; set; }
    public bool? Blocking { get; set; }
}

public class OpenRequestV2
{
    public string NodeId { get; set; }
    public long FundingSatoshis { get; set; }
    public long? PushMsat { get; set; }
    public long? FundingFeerateSatByte { get; set; }
    // public ChannelFlags? ChannelFlags { get; set; }
    public string ChannelType { get; set; } = "standard";
    public bool AnnounceChannel { get; set; } = true;
    public int OpenTimeoutSeconds { get; set; } = 10;
}