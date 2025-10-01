using NBitcoin;
using NBitcoin.RPC;

namespace LNUnit.Tests;

public static class PsbtMultiOutputBuilderExtensions
{
    /// <summary>
    /// Creates a PSBT template with n outputs to the same destination address, no inputs
    /// </summary>
    public static async Task<string> CreateMultiOutputPsbt(this RPCClient rpcClient,
        string destinationAddress,
        decimal amountPerOutput,
        int numberOfOutputs)
    {
        // Get the scriptPubKey for the address
        var address = BitcoinAddress.Create(destinationAddress, rpcClient.Network);
        var scriptPubKey = address.ScriptPubKey;

        // Build raw transaction manually
        var rawTxHex = BuildRawTransaction(scriptPubKey, amountPerOutput, numberOfOutputs);

        // Convert to PSBT using RPC
        var psbt = await rpcClient.SendCommandAsync("converttopsbt", rawTxHex);
        return psbt.Result.ToString();
    }

    private static string BuildRawTransaction(Script scriptPubKey, decimal amountPerOutput, int numberOfOutputs)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Version (4 bytes)
        writer.Write((uint)2);

        // Input count (variable length integer) - 0 inputs
        WriteVarInt(writer, 0);

        // Output count
        WriteVarInt(writer, (ulong)numberOfOutputs);

        // Write each output
        var scriptBytes = scriptPubKey.ToBytes();
        var satoshis = (ulong)(amountPerOutput);

        for (int i = 0; i < numberOfOutputs; i++)
        {
            // Amount in satoshis (8 bytes, little-endian)
            writer.Write(satoshis);

            // Script length
            WriteVarInt(writer, (ulong)scriptBytes.Length);

            // Script
            writer.Write(scriptBytes);
        }

        // Locktime (4 bytes)
        writer.Write((uint)0);

        // Convert to hex
        return BytesToHex(stream.ToArray());
    }

    private static void WriteVarInt(BinaryWriter writer, ulong value)
    {
        if (value < 0xfd)
        {
            writer.Write((byte)value);
        }
        else if (value <= 0xffff)
        {
            writer.Write((byte)0xfd);
            writer.Write((ushort)value);
        }
        else if (value <= 0xffffffff)
        {
            writer.Write((byte)0xfe);
            writer.Write((uint)value);
        }
        else
        {
            writer.Write((byte)0xff);
            writer.Write(value);
        }
    }

    private static string BytesToHex(byte[] bytes)
    {
        return BitConverter.ToString(bytes).Replace("-", "").ToLower();
    }
}
