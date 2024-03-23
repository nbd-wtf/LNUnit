using System.Text;
using ServiceStack.Text;

namespace LNBolt.BOLT11;

public class PayReq
{
    public string Build(bool isMainnet)
    {
        var prefix = BuildPrefix(isMainnet);
        var timestamp = BuildTimestamp();
        throw new NotImplementedException();
    }

    private List<byte> BuildTimestamp()
    {
        var words = new List<byte>();
        var bits = 5;
        var timestamp = DateTime.Now.ToUnixTime();
        while (timestamp > 0)
        {
            words.Add((byte)(timestamp & ((long)Math.Pow(2, bits) - 1)));
            timestamp = (long)Math.Floor(timestamp / Math.Pow(2, bits));
        }

        words.Reverse();
        return words;
    }

    private static string BuildPrefix(bool isMainnet)
    {
        var prefix = new StringBuilder();
        prefix.Append("ln");
        //Prefix
        if (isMainnet)
            prefix.Append("bc");
        else
            prefix.Append("tb");
        return prefix.ToString();
    }
}

public enum TAGCODE
{
    payment_hash = 1,
    payment_secret = 16,
    description = 13,
    payee_node_key = 19,
    purpose_commit_hash = 23, // commit to longer descriptions (like a website)
    expire_time = 6, // default: 3600 (1 hour)
    min_final_cltv_expiry = 24, // default: 9
    fallback_address = 9,
    routing_info = 3, // for extra routing info (private etc.)
    feature_bits = 5
}