using Kermalis.EndianBinaryIO;
using ServiceStack;

namespace LNBolt;

public class BigSize
{
    public BigSize(ulong value)
    {
        Value = value;
        if (value < 0xfd)
            Encoding = ((byte)value).InArray();
        else if (value < 0x10000)
            Encoding = new byte[] { 0xfd }
                .Concat(EndianBitConverter.UInt16sToBytes(((ushort)value).InArray(), 0, 1, Endianness.BigEndian))
                .ToArray();
        else if (value < 0x100000000)
            Encoding = new byte[] { 0xfe }
                .Concat(EndianBitConverter.UInt32sToBytes(((uint)value).InArray(), 0, 1, Endianness.BigEndian))
                .ToArray();
        else //(value > 0x100000000)
            Encoding = new byte[] { 0xff }
                .Concat(EndianBitConverter.UInt64sToBytes(value.InArray(), 0, 1, Endianness.BigEndian)).ToArray();
    }

    public byte[] Encoding { get; internal set; }
    public ulong Value { get; internal set; }
    public int Length => Encoding.Length;

    public static BigSize Parse(byte[] data)
    {
        if (data == null || data.Length == 0)
            throw new Exception("EOF");
        BigSize result;
        switch (data[0])
        {
            case var x when x < 0xfd: //uint8
                result = new BigSize(Convert.ToByte(data[0]));
                break;
            case var x when x < 0xfe: //uint16 Big-endian
                if (data.Length < 3)
                    throw new Exception("unexpected EOF");
                result = new BigSize(EndianBitConverter.BytesToUInt16s(data[1..3], 0, 1, Endianness.BigEndian).First());
                if (result.Value < 0xfd)
                    throw new Exception("decoded bigsize is not canonical");
                break;
            case var x when x < 0xff: //uint32 Big-endian
                if (data.Length < 5)
                    throw new Exception("unexpected EOF");
                result = new BigSize(EndianBitConverter.BytesToUInt32s(data[1..5], 0, 1, Endianness.BigEndian).First());
                if (result.Value <= ushort.MaxValue)
                    throw new Exception("decoded bigsize is not canonical");
                break;
            default: //uint64 Big-endian
                if (data.Length < 9)
                    throw new Exception("unexpected EOF");
                result = new BigSize(EndianBitConverter.BytesToUInt64s(data[1..9], 0, 1, Endianness.BigEndian).First());
                if (result.Value <= uint.MaxValue)
                    throw new Exception("decoded bigsize is not canonical");
                break;
        }

        return result;
    }
}