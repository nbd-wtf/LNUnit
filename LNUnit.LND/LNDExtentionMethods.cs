using System.Security.Cryptography;
using Lnrpc;
using ServiceStack;

namespace LNUnit.LND;

public static class LNDExtensions
{
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

    public static (byte[] data, byte[] iv) EncryptStringToAesBytes(this byte[] clearData, byte[] key, byte[]? iv)
    {
        // Check arguments.
        if (clearData.Length <= 0)
            throw new ArgumentNullException("clearData");
        if (key == null || key.Length <= 0)
            throw new ArgumentNullException("key");
        byte[] encrypted;
        // Create an Aes object
        // with the specified key and IV.
        using (var aesAlg = Aes.Create())
        {
            aesAlg.Key = key;
            if (iv != null)
                aesAlg.IV = iv;
            aesAlg.Mode = CipherMode.CBC;
            // Create an encryptor to perform the stream transform.
            iv = aesAlg.IV;
            var encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);
            // Create the streams used for encryption.
            using (var msEncrypt = new MemoryStream())
            {
                using (var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                {
                    csEncrypt.Write(clearData);
                }

                encrypted = msEncrypt.ToArray();
            }
        }

        // Return the encrypted bytes from the memory stream.
        return (encrypted, iv);
    }

    public static byte[] DecryptStringFromBytesAes(this byte[] cipherData, byte[] key, byte[] iv)
    {
        // Check arguments.
        if (cipherData.Length <= 0)
            throw new ArgumentNullException("cipherData");
        if (key == null || key.Length <= 0)
            throw new ArgumentNullException("key");
        if (iv == null || iv.Length <= 0)
            throw new ArgumentNullException("iv");

        // Create an Aes object
        // with the specified key and IV.
        using (var aesAlg = Aes.Create())
        {
            aesAlg.Key = key;
            aesAlg.IV = iv;
            aesAlg.Mode = CipherMode.CBC;
            // Create a decryptor to perform the stream transform.
            var decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

            // Create the streams used for decryption.
            using (var msDecrypt = new MemoryStream(cipherData))
            {
                using (var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                {
                    return csDecrypt.ReadFully();
                }
            }
        }
    }
}