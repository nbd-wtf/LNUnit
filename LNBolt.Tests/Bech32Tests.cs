using System;
using System.Text;
using System.Threading.Tasks;
using LNBolt.BOLT11;
using NUnit.Framework;
using org.ldk.structs;

namespace LNBolt.Tests;

public class Bech32Tests
{
    [Test]
    public async Task ldk()
    {
        for (var i = 0; i < 10000; i++)
        {
            var bolt11 = Bolt11Invoice.from_str(
                "lnbc100p1psj9jhxdqud3jxktt5w46x7unfv9kz6mn0v3jsnp4q0d3p2sfluzdx45tqcsh2pu5qc7lgq0xs578ngs6s0s68ua4h7cvspp5q6rmq35js88zp5dvwrv9m459tnk2zunwj5jalqtyxqulh0l5gflssp5nf55ny5gcrfl30xuhzj3nphgj27rstekmr9fw3ny5989s300gyus9qyysgqcqpcrzjqw2sxwe993h5pcm4dxzpvttgza8zhkqxpgffcrf5v25nwpr3cmfg7z54kuqq8rgqqqqqqqq2qqqqq9qq9qrzjqd0ylaqclj9424x9m8h2vcukcgnm6s56xfgu3j78zyqzhgs4hlpzvznlugqq9vsqqqqqqqlgqqqqqeqq9qrzjqwldmj9dha74df76zhx6l9we0vjdquygcdt3kssupehe64g6yyp5yz5rhuqqwccqqyqqqqlgqqqqjcqq9qrzjqf9e58aguqr0rcun0ajlvmzq3ek63cw2w282gv3z5uupmuwvgjtq2z55qsqqg6qqqyqqqrtnqqqzq3cqygrzjqvphmsywntrrhqjcraumvc4y6r8v4z5v593trte429v4hredj7ms5z52usqq9ngqqqqqqqlgqqqqqqgq9qrzjq2v0vp62g49p7569ev48cmulecsxe59lvaw3wlxm7r982zxa9zzj7z5l0cqqxusqqyqqqqlgqqqqqzsqygarl9fh38s0gyuxjjgux34w75dnc6xp2l35j7es3jd4ugt3lu0xzre26yg5m7ke54n2d5sym4xcmxtl8238xxvw5h5h5j5r6drg6k6zcqj0fcwg");
            var x = bolt11 as Result_Bolt11InvoiceParseOrSemanticErrorZ.Result_Bolt11InvoiceParseOrSemanticErrorZ_OK;
            var res = new PaymentRequest
            {
                PaymentHash = x.res.payment_hash(),
                AmountMilliSatoshis = x.res.amount_milli_satoshis(),
                expiry_time = x.res.expiry_time(),
                payee_pub_key = x.res.payee_pub_key()
                //RouteHints = x.res.route_hints(),
            };
            var y = x.res.payment_hash();
            Assert.AreEqual(Convert.ToHexString(y), "0687B0469281CE20D1AC70D85DD6855CECA1726E9525DF81643039FBBFF4427F");
            //Console.WriteLine($"{i}");
        }
    }

    [Test]
    public void ValidChecksums()
    {
        string[] valid_checksum =
        {
            "A12UEL5L",
            "an83characterlonghumanreadablepartthatcontainsthenumber1andtheexcludedcharactersbio1tt5tgs",
            "abcdef1qpzry9x8gf2tvdw0s3jn54khce6mua7lmqqqxw",
            "11qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqc8247j",
            "split1checkupstagehandshakeupstreamerranterredcaperred2y9e3w"
        };

        foreach (var encoded in valid_checksum)
        {
            string hrp;
            byte[] data;
            Bech32Engine.Decode(encoded, out hrp, out data);
            Assert.IsNotNull(data, "bech32_decode fails: {0}", encoded);

            var rebuild = Bech32Engine.Encode(hrp, data);
            Assert.IsNotNull(rebuild, "bech32_encode fails: {0}", encoded);
            Assert.AreEqual(encoded.ToLower(), rebuild, "bech32_encode produces incorrect result : {0}", encoded);
        }
    }

    [Test]
    public void InvalidChecksums()
    {
        string[] invalid_checksum =
        {
            "tc1qw508d6qejxtdg4y5r3zarvary0c5xw7kg3g4ty",
            "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t5",
            "BC13W508D6QEJXTDG4Y5R3ZARVARY0C5XW7KN40WF2",
            "bc1rw5uspcuh",
            "bc10w508d6qejxtdg4y5r3zarvary0c5xw7kw508d6qejxtdg4y5r3zarvary0c5xw7kw5rljs90",
            "BC1QR508D6QEJXTDG4Y5R3ZARVARYV98GJ9P",
            "tb1qrp33g0q5c5txsp9arysrx4k6zdkfs4nce4xj0gdcccefvpysxf3q0sL5k7"
        };

        foreach (var encoded in invalid_checksum)
        {
            string hrp;
            byte[] data;
            Bech32Engine.Decode(encoded, out hrp, out data);
            Assert.IsNull(data, "bech32_decode should fail: {0}", encoded);
        }
    }

    [Test]
    public void LNURLDecode()
    {
        string hrp;
        byte[] data;
        var lnurl =
            "LNURL1DP68GURN8GHJ7UM9WFMXJCM99E3K7MF0V9CXJ0M385EKVCENXC6R2C35XVUKXEFCV5MKVV34X5EKZD3EV56NYD3HXQURZEPEXEJXXEPNXSCRVWFNV9NXZCN9XQ6XYEFHVGCXXCMYXYMNSERXFQ5FNS";
        Bech32Engine.Decode(lnurl, out hrp, out data);
        Assert.AreEqual(hrp, "lnurl");
        var url = Encoding.UTF8.GetString(data);
        Assert.AreEqual("https://service.com/api?q=3fc3645b439ce8e7f2553a69e5267081d96dcd340693afabe04be7b0ccd178df",
            url);
    }

    public record PaymentRequest
    {
        public byte[] PaymentHash { get; set; }
        public Option_u64Z AmountMilliSatoshis { get; set; }
        public long expiry_time { get; set; }
        public byte[] payee_pub_key { get; set; }
    }
}