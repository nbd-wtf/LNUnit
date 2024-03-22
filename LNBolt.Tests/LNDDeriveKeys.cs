using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using ServiceStack;

namespace LNBolt.Tests;

public class LNToolsTests
{
    //private LNDNodeConnection Alice;

    [SetUp]
    public void Setup()
    {
        //Alice = new LNDNodeConnection(new LNDSettings
        //{
        //    TLSCertBase64 = $"LS0tLS1CRUdJTiBDRVJUSUZJQ0FURS0tLS0tCk1JSUNLakNDQWRDZ0F3SUJBZ0lRVFNKbXJrWEd2eVptcEVIMG9yc2VNekFLQmdncWhrak9QUVFEQWpBNE1SOHcKSFFZRFZRUUtFeFpzYm1RZ1lYVjBiMmRsYm1WeVlYUmxaQ0JqWlhKME1SVXdFd1lEVlFRREV3dzBaVEZrTkRnMQpaRGd5WkdFd0hoY05Nakl3TXpFM01UUTFPRE0xV2hjTk1qTXdOVEV5TVRRMU9ETTFXakE0TVI4d0hRWURWUVFLCkV4WnNibVFnWVhWMGIyZGxibVZ5WVhSbFpDQmpaWEowTVJVd0V3WURWUVFERXd3MFpURmtORGcxWkRneVpHRXcKV1RBVEJnY3Foa2pPUFFJQkJnZ3Foa2pPUFFNQkJ3TkNBQVNIQ0d5dkJtNktTcnZCRkZjVEJLbEpzaXI0czJEYgpndjFRVi9MM1ZxR2h6c2pySkxJYjY2Q1Eyb2krU2RBWU5FTFJqWUpTODgwZTZ6aGFrODVzbk1oZm80RzdNSUc0Ck1BNEdBMVVkRHdFQi93UUVBd0lDcERBVEJnTlZIU1VFRERBS0JnZ3JCZ0VGQlFjREFUQVBCZ05WSFJNQkFmOEUKQlRBREFRSC9NQjBHQTFVZERnUVdCQlQxTDJDYW5iYVhjR0FtdmV1MU5HRG83STR6dlRCaEJnTlZIUkVFV2pCWQpnZ3cwWlRGa05EZzFaRGd5WkdHQ0NXeHZZMkZzYUc5emRJSUVkVzVwZUlJS2RXNXBlSEJoWTJ0bGRJSUhZblZtClkyOXVib2NFZndBQUFZY1FBQUFBQUFBQUFBQUFBQUFBQUFBQUFZY0VyQkVBQW9jRUNoVUVaakFLQmdncWhrak8KUFFRREFnTklBREJGQWlCenNsYUtaamxFK1UvWnNvc2o1WHRJRk9GK0dvWlRSY3RhTGZ0b3dTb09wUUloQU5UZQpGUDVRbW9qcENRcEVLdWRjcXBLUkFCZlhIdHNKRFhBd3N1KzY3dTNUCi0tLS0tRU5EIENFUlRJRklDQVRFLS0tLS0K",
        //    MacaroonBase64 = $"AgEDbG5kAvgBAwoQz8x2/BG8y02tK1Iuz+tX9BIBMBoWCgdhZGRyZXNzEgRyZWFkEgV3cml0ZRoTCgRpbmZvEgRyZWFkEgV3cml0ZRoXCghpbnZvaWNlcxIEcmVhZBIFd3JpdGUaIQoIbWFjYXJvb24SCGdlbmVyYXRlEgRyZWFkEgV3cml0ZRoWCgdtZXNzYWdlEgRyZWFkEgV3cml0ZRoXCghvZmZjaGFpbhIEcmVhZBIFd3JpdGUaFgoHb25jaGFpbhIEcmVhZBIFd3JpdGUaFAoFcGVlcnMSBHJlYWQSBXdyaXRlGhgKBnNpZ25lchIIZ2VuZXJhdGUSBHJlYWQAAAYgpsoEidbnsavbWJ2Ew7YSP2aVE8+aUcM3UewejfM3cYs=",
        //    GrpcEndpoint = $"https://10.21.4.102:10009",
        //});
    }

    [Test]
    public void SCIDToLNDChannelId()
    {
        const ulong ChanId = 771399766003482624;
        const string ScidString = "701584x2165x0";
        var lnd = LNTools.SCIDToLNDChannelId(ScidString);
        var cln = LNTools.LNDChannelIdToSCID(ChanId);
        Assert.That(ChanId == lnd);
        Assert.That(ScidString == LNTools.SCIDToString(cln));
    }

    [Test]
    public void HMACTest()
    {
        var salt = "2640f52eebcd9e882958951c794250eedb28002c05d7dc2ea0f195406042caf1";
        var data = "1e2fb3c8fe8fb9f262f649f64d26ecf0f2c0a805a767cf02dc2d77a6ef1fdcc3";
        var hmac = LNTools.CalculateHMAC(Convert.FromHexString(salt), Convert.FromHexString(data));
        Assert.That(hmac.ToHex() == "f224df1c0e16949394542ce779461d8f130b569112d3d45bbaed9e9749862f16");
    }

    [Test]
    public async Task LNDNodeKeyCreation()
    {
        var xprv =
            "xprv9s21ZrQH143K3EH5SqkxGeb1prC9TsuVnu5GcNva3Bgvp7rRMPUUDeYBrQuxZorFXE3L9XZtqm95MPLpuSkp8q2WtQHap9NDcHDsNXu2Pep";
        var lndPubKey = "0257780624efb6fd6f49fe06ab38857b5afea7c2e75923a1da6808f01fd217f51b";
        var (lndPrivate, lndPub) = LNTools.DeriveLNDNodeKeys(xprv, false);
        Assert.That(lndPub.ToHex() == lndPubKey);
    }

    [Test]
    public async Task DeriveSharedSecret()
    {
        var sessionKey = Convert.FromHexString("021b148a7760576194f575ed92aa171e15295ea15587a678002cfabce46478f1e5");
        var xprv =
            "xprv9s21ZrQH143K3EH5SqkxGeb1prC9TsuVnu5GcNva3Bgvp7rRMPUUDeYBrQuxZorFXE3L9XZtqm95MPLpuSkp8q2WtQHap9NDcHDsNXu2Pep";
        var (lndPrivate, lndPub) = LNTools.DeriveLNDNodeKeys(xprv, false);
        var shared = LNTools.DeriveSharedSecret(sessionKey, lndPrivate);
        Assert.That(shared.ToHex() == "943909951d92bc114eb59ca2eeb7912ac5ee475e876edcaf216809760525c12e");
    }

    [Test]
    public async Task DeriveRhoKey()
    {
        var sharedSecret = Convert.FromHexString("b5756b9b542727dbafc6765a49488b023a725d631af688fc031217e90770c328");
        var rhoKey = LNTools.GenerateRhoKey(sharedSecret);
        Assert.That("034e18b8cc718e8af6339106e706c52d8df89e2b1f7e9142d996acf88df8799b" == rhoKey.ToHex());
    }

    [Test]
    public async Task CalculateSharedSecret()
    {
        var publicNodeKey = Convert.FromHexString("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619");
        var sessionKey = Convert.FromHexString("4141414141414141414141414141414141414141414141414141414141414141");
        Assert.That(LNTools.DeriveSharedSecret(publicNodeKey, sessionKey).ToHex() ==
                    "53eb63ea8a3fec3b3cd433b85cd62a4b145e1dda09391b348c4e1cd36a03ea66");
    }

    [Test]
    public void CalculateSharedSecretsForMultipleHops()
    {
        //This includes "blinding" after first key
        var hopPublicKeys = new List<byte[]>
        {
            Convert.FromHexString("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619"),
            Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c"),
            Convert.FromHexString("027f31ebc5462c1fdce1b737ecff52d37d75dea43ce11c74d25aa297165faa2007"),
            Convert.FromHexString("032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7b1e668680991"),
            Convert.FromHexString("02edabbd16b41c8371b92ef2f04c1185b4f03b6dcd52ba9b78d9d7c89c8f221145")
        };
        var sessionKey = Convert.FromHexString("4141414141414141414141414141414141414141414141414141414141414141");

        var results = LNTools.CalculatedSharedSecrets(sessionKey, hopPublicKeys);
        Assert.That(results.Count == 5);
        Assert.That(results[0].ToHex() == "53eb63ea8a3fec3b3cd433b85cd62a4b145e1dda09391b348c4e1cd36a03ea66");
        Assert.That(results[1].ToHex() == "a6519e98832a0b179f62123b3567c106db99ee37bef036e783263602f3488fae");
        Assert.That(results[2].ToHex() == "3a6b412548762f0dbccce5c7ae7bb8147d1caf9b5471c34120b30bc9c04891cc");
        Assert.That(results[3].ToHex() == "21e13c2d7cfe7e18836df50872466117a295783ab8aab0e7ecc8c725503ad02d");
        Assert.That(results[4].ToHex() == "b5756b9b542727dbafc6765a49488b023a725d631af688fc031217e90770c328");
    }
}