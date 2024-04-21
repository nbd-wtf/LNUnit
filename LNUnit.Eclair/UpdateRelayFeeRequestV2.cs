namespace BTCPayServer.Lightning.Eclair.Models
{
    public class UpdateRelayFeeRequestV2
    {
        public string NodeId { get; set; } 
        public int FeeBaseMsat { get; set; }
        public int FeeProportionalMillionths { get; set; }

    }
}