using System.Linq;
using Kermalis.EndianBinaryIO;
using NUnit.Framework;
using ServiceStack;

namespace LNBolt.Tests;

public class TLVTests
{
    [Test]
    public void ParseTLV()
    {
        var data = "Hello World!".ToUtf8Bytes();
        var tlv = new TLV(29, data);
        var tlvBuffer = tlv.ToEncoding();
        var expandedBuffer = tlvBuffer.Concat(Enumerable.Repeat((byte)10, 42)).ToArray();
        var restoredTlv = TLV.Parse(expandedBuffer);
        Assert.AreEqual(restoredTlv.Type, 29);
        Assert.AreEqual(restoredTlv.DataSize, 12);
        Assert.AreEqual(restoredTlv.TLVSize, 14);
        Assert.AreEqual(restoredTlv.Value, data);

        var parsed = TLV.Parse("010401000000".HexToBytes());
        Assert.AreEqual(parsed.Type, 1);
        Assert.AreEqual(parsed.Value.TrimmedBE64ToUInt64(), 16777216);
        parsed = TLV.Parse("01080100000000000000".HexToBytes());
        Assert.AreEqual(parsed.Type, 1);
        Assert.AreEqual(parsed.Value.TrimmedBE64ToUInt64(), 72057594037927936);
    }

    [Test]
    public void ParseTLV21_22_vectors()
    {
        var data =
            "0331023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb00000000000000010000000000000002"
                .HexToBytes();
        var tlv0 = TLV.Parse(data);
        Assert.AreEqual(tlv0.Type, 3); //tlv3
        Assert.AreEqual(tlv0.Value[..33].ToHex(),
            "023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb"); //node_id
        Assert.AreEqual(tlv0.Value[33..(33 + 8)].BE64ToUInt64(), 1); //amount_msat_1
        Assert.AreEqual(tlv0.Value[(33 + 8)..].BE64ToUInt64(), 2); //amount_msat_2
    }

    [Test]
    public void HopPayloadLegacySerialization()
    {
        var hopPayload = new HopPayload
        {
            AmountToForward = (ulong)0.7e8,
            OutgoingCltvValue = 124,
            ChannelId = new byte[] { 10, 11, 12, 13, 14, 15, 16, 17 },
            HopPayloadType = HopPayloadType.Legacy
        };

        var serialization = hopPayload.ToSphinxBuffer();
        Assert.AreEqual(serialization.ToHex(), "000a0b0c0d0e0f101100000000042c1d800000007c000000000000000000000000");

        Assert.AreEqual(hopPayload.SphinxSize, serialization.Length);
        Assert.AreEqual(hopPayload.SphinxSize, 33);
    }

    [Test]
    public void HopPayloadTLVSerialization()
    {
        var hopPayload = new HopPayload
        {
            AmountToForward = (ulong)0.5e8,
            OutgoingCltvValue = 137,
            ChannelId = null,
            HopPayloadType = HopPayloadType.TLV
        };

        var serialization = hopPayload.ToSphinxBuffer();
        Assert.AreEqual(hopPayload.SphinxSize, serialization.Length);
        Assert.AreEqual(serialization.ToHex(), "09020402faf080040189");
    }

    [Test]
    public void TLVSerialization()
    {
        var amountToForwardTlv = new TLV(2,
            EndianBitConverter.UInt64sToBytes(((ulong)23).InArray(), 0, 1, Endianness.BigEndian).TrimZeros());
        var outgoingCltvValueTlv = new TLV(4,
            EndianBitConverter.UInt32sToBytes(((uint)34).InArray(), 0, 1, Endianness.BigEndian).TrimZeros());
        var channelIdTlv = new TLV(6, "abcdef10".HexToBytes());
        var tlvStream = amountToForwardTlv.ToEncoding().Concat(outgoingCltvValueTlv.ToEncoding())
            .Concat(channelIdTlv.ToEncoding()).ToArray();
        Assert.AreEqual(tlvStream.ToHex(), "0201170401220604abcdef10");
    }

    [Test]
    public void HopPayloadLegacyDeserialization()
    {
        var undelimitedBuffer = "000a0b0c0d0e0f101100000000042c1d800000007c000000000000000000000000".HexToBytes();
        var hopPayload = HopPayload.ParseSphinx(undelimitedBuffer);
        Assert.AreEqual(hopPayload.ChannelId.ToHex(), "0a0b0c0d0e0f1011");
        Assert.AreEqual(hopPayload.AmountToForward, (ulong)0.7e8);
        Assert.AreEqual(hopPayload.OutgoingCltvValue, 124);
    }
}