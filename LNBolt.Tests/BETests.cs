using NUnit.Framework;

namespace LNBolt.Tests;

public class BETests
{
    [Test]
    public void TestBEExtentions()
    {
        var one = (ulong)1;
        var max = ulong.MaxValue;

        var oneBytes = one.UInt64ToBE64();
        var maxBytes = max.UInt64ToBE64();
        Assert.AreEqual(oneBytes.Length, 8);
        Assert.AreEqual(maxBytes.Length, 8);

        var oneBytesTrimmed = one.UInt64ToTrimmedBE64Bytes();
        var maxBytesTrimmed = max.UInt64ToTrimmedBE64Bytes();
        Assert.AreEqual(oneBytesTrimmed.Length, 1);
        Assert.AreEqual(maxBytesTrimmed.Length, 8);

        var oneFull = oneBytes.BE64ToUInt64();
        var oneFullFromTrim = oneBytesTrimmed.TrimmedBE64ToUInt64();
        var maxFull = maxBytes.BE64ToUInt64();
        var maxFullFromTrim = maxBytesTrimmed.TrimmedBE64ToUInt64();


        Assert.AreEqual(oneFull, oneFullFromTrim);
        Assert.AreEqual(one, oneFullFromTrim);
        Assert.AreEqual(maxFull, maxFullFromTrim);
        Assert.AreEqual(max, maxFullFromTrim);
    }

    [Test]
    public void ShiftTest()
    {
        var source = new byte[] { 1, 2, 3, 4, 5 };
        source.CopyWithin(2, 0);
        Assert.AreEqual(source, new byte[] { 1, 2, 1, 2, 3 });
    }
}