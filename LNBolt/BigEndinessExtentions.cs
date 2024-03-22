using Kermalis.EndianBinaryIO;
using ServiceStack;

namespace LNBolt;

public static class BigEndinessExtentions
{
    public static byte[] UInt64ToBE64(this ulong num)
    {
        return EndianBitConverter.UInt64sToBytes(num.InArray(), 0, 1, Endianness.BigEndian);
    }

    public static byte[] UInt32ToBE32(this uint num)
    {
        return EndianBitConverter.UInt32sToBytes(num.InArray(), 0, 1, Endianness.BigEndian);
    }

    public static byte[] UInt16ToBE16(this ushort num)
    {
        return EndianBitConverter.UInt16sToBytes(num.InArray(), 0, 1, Endianness.BigEndian);
    }

    public static byte[] UInt64ToTrimmedBE64Bytes(this ulong num)
    {
        return EndianBitConverter.UInt64sToBytes(num.InArray(), 0, 1, Endianness.BigEndian).TrimZeros();
    }

    public static byte[] UInt32ToTrimmedBE32Bytes(this uint num)
    {
        return EndianBitConverter.UInt32sToBytes(num.InArray(), 0, 1, Endianness.BigEndian).TrimZeros();
    }

    public static byte[] UInt16ToTrimmedBE16(this ushort num)
    {
        return EndianBitConverter.UInt16sToBytes(num.InArray(), 0, 1, Endianness.BigEndian).TrimZeros();
    }

    public static byte[] TrimZeros(this byte[] data)
    {
        var trimOffset = 0;
        for (var i = 0; i < data.Length; i++)
            if (data[i] == 0)
                trimOffset++;
            else
                break;
        return data[trimOffset..];
    }

    public static ulong BE64ToUInt64(this byte[] data)
    {
        return EndianBitConverter.BytesToUInt64s(data, 0, 1, Endianness.BigEndian).First();
    }

    public static uint BE32ToUInt32(this byte[] data)
    {
        return EndianBitConverter.BytesToUInt32s(data, 0, 1, Endianness.BigEndian).First();
    }

    public static ushort BE16ToUInt16(this byte[] data)
    {
        return EndianBitConverter.BytesToUInt16s(data, 0, 1, Endianness.BigEndian).First();
    }

    private static byte[] Untrim(byte[] buffer, int fullByteCount)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));
        if (buffer.Length > fullByteCount)
            throw new ArgumentException($"{nameof(buffer)}.Length > {nameof(fullByteCount)} value of {fullByteCount}");
        var untrimmedBuffer = new byte[fullByteCount];
        buffer.CopyTo(untrimmedBuffer, untrimmedBuffer.Length - buffer.Length);
        return untrimmedBuffer;
    }

    public static ulong TrimmedBE64ToUInt64(this byte[] data)
    {
        var untrimmed = Untrim(data, 8);
        return EndianBitConverter.BytesToUInt64s(untrimmed, 0, 1, Endianness.BigEndian).First();
    }

    public static uint TrimmedBE32ToUInt32(this byte[] data)
    {
        var untrimmed = Untrim(data, 4);
        return EndianBitConverter.BytesToUInt32s(untrimmed, 0, 1, Endianness.BigEndian).First();
    }

    public static ushort TrimmedBE16ToUInt16(this byte[] data)
    {
        var untrimmed = Untrim(data, 2);
        return EndianBitConverter.BytesToUInt16s(untrimmed, 0, 1, Endianness.BigEndian).First();
    }

    public static void CopyWithin(this byte[] array, int target, int start)
    {
        var copyArray = array.ToArray();
        var y = 0;
        for (var i = target; i < array.Length; i++)
        {
            array[i] = copyArray[start + y];
            y++;
        }
    }
}