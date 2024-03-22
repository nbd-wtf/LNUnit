using System.Security.Cryptography;
using Lnrpc;
using LNUnit.LND;
using ServiceStack;

namespace LNUnit.Extentions;

public static class LNDExtensions
{
    private static Random r = new();


    public static LNDNodeConnection GetClient(this LNDSettings settings)
    {
        var client = new LNDNodeConnection(settings);
        return client;
    }

    public static long PackUnsignedToInt64(this ulong i)
    {
        var unsigned = BitConverter.GetBytes(i);
        var signedInt = BitConverter.ToInt64(unsigned);
        return signedInt;
    }

    public static ulong UnpackSignedToUInt64(this long i)
    {
        var byteArray = BitConverter.GetBytes(i);
        var backToUnsigned = BitConverter.ToUInt64(byteArray);
        return backToUnsigned;
    }

    public static LightningNode ToLightningNode(this LNDNodeConnection node)
    {
        return new LightningNode
        {
            Alias = node.LocalAlias,
            PubKey = node.LocalNodePubKey
        };
    }

    public static List<LightningNode> ToLightningNodes(this List<LNDNodeConnection> nodes)
    {
        return nodes.ConvertAll(x => x.ToLightningNode());
    }

    public static (byte[] data, byte[] iv) EncryptStringToAesBytes(this byte[] ClearData, byte[] Key, byte[] IV)
    {
        // Check arguments.
        if (ClearData.Length <= 0)
            throw new ArgumentNullException("ClearData");
        if (Key == null || Key.Length <= 0)
            throw new ArgumentNullException("Key");
        byte[] encrypted;
        // Create an Aes object
        // with the specified key and IV.
        using (var aesAlg = Aes.Create())
        {
            aesAlg.Key = Key;
            if (IV != null)
                IV = aesAlg.IV;
            aesAlg.Mode = CipherMode.CBC;
            // Create an encryptor to perform the stream transform.
            IV = aesAlg.IV;
            var encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);
            // Create the streams used for encryption.
            using (var msEncrypt = new MemoryStream())
            {
                using (var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                {
                    csEncrypt.Write(ClearData);
                }

                encrypted = msEncrypt.ToArray();
            }
        }

        // Return the encrypted bytes from the memory stream.
        return (encrypted, IV);
    }

    public static byte[] DecryptStringFromBytesAes(this byte[] CipherData, byte[] Key, byte[] IV)
    {
        // Check arguments.
        if (CipherData.Length <= 0)
            throw new ArgumentNullException("CipherData");
        if (Key == null || Key.Length <= 0)
            throw new ArgumentNullException("Key");
        if (IV == null || IV.Length <= 0)
            throw new ArgumentNullException("IV");

        // Create an Aes object
        // with the specified key and IV.
        using (var aesAlg = Aes.Create())
        {
            aesAlg.Key = Key;
            aesAlg.IV = IV;
            aesAlg.Mode = CipherMode.CBC;
            // Create a decryptor to perform the stream transform.
            var decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

            // Create the streams used for decryption.
            using (var msDecrypt = new MemoryStream(CipherData))
            {
                using (var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                {
                    return csDecrypt.ReadFully();
                }
            }
        }
    }
}