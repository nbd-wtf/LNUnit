namespace LNBolt;

public class TLV
{
    public TLV(ulong type, byte[] value)
    {
        Type = type;
        Value = value;
    }

    public ulong Type { get; set; }
    public byte[] Value { get; set; }

    public int TLVSize
    {
        get
        {
            var typeSize = new BigSize(Type).Length;
            var lengthSize = new BigSize((ulong)DataSize).Length;
            return typeSize + lengthSize + DataSize;
        }
    }

    public int DataSize => Value.Length;

    public byte[] ToEncoding()
    {
        var typeBuffer = new BigSize(Type).Encoding;
        var lengthBuffer = new BigSize((ulong)DataSize).Encoding;
        return typeBuffer.Concat(lengthBuffer).Concat(Value).ToArray();
    }

    public static TLV Parse(byte[] undelimitedTLVBuffer)
    {
        var type = BigSize.Parse(undelimitedTLVBuffer);
        var lengthBuffer = undelimitedTLVBuffer[type.Length..];
        var length = BigSize.Parse(lengthBuffer);
        var startIndex = length.Length;
        var endIndex = startIndex + (int)length.Value;
        var data = lengthBuffer[startIndex..endIndex];
        return new TLV(type.Value, data);
    }
}