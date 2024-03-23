using System.Text;
using ServiceStack;

namespace LNBolt.TLVDecoders;

public class Podcasting20Decoder
{
    public static readonly ulong PODCASTING20_TLV_TYPE = 7629169;

    public static Podcasting20Datagram Decode(TLV record)
    {
        var json = Encoding.UTF8.GetString(record.Value);
        return json.FromJson<Podcasting20Datagram>();
    }

    public static TLV Encode(Podcasting20Datagram record)
    {
        var json = record.ToJson();
        var tlv = new TLV(PODCASTING20_TLV_TYPE, Encoding.UTF8.GetBytes(json));
        return tlv;
    }
}

public class Podcasting20Datagram
{
    public string? podcast { get; set; }
    public int? feedId { get; set; }
    public string? url { get; set; }
    public string? guid { get; set; }

    public string? episode { get; set; }
    public string? episode_guid { get; set; }
    public int? itemId { get; set; }

    public int? ts { get; set; }
    public string? time { get; set; }


    public string? action { get; set; }
    public string? app_name { get; set; }
    public string? app_version { get; set; }
    public string? boost_link { get; set; }
    public string? message { get; set; }
    public string? name { get; set; }
    public string? pubkey { get; set; }
    public int? seconds_back { get; set; }
    public string? sender_key { get; set; }
    public string? sender_name { get; set; }
    public string? sender_id { get; set; }
    public string? sig_fields { get; set; }
    public string? signature { get; set; }
    public string? speed { get; set; }
    public string? uuid { get; set; }
    public int value_msat_total { get; set; }
    public int value_msat { get; set; }
}