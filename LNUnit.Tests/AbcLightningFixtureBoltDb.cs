

using System.Security.Cryptography;
using Google.Protobuf;
// using LNBolt;
using Lnrpc;
using LNUnit.LND;
using NBitcoin;
using Routerrpc;
using ServiceStack;
using ServiceStack.Text;
using Feature = ServiceStack.Feature;

namespace LNUnit.Tests;

[Ignore("only local")]
//[TestFixture("boltdb", "custom_lnd", "latest", "/home/lnd/.lnd", false)]
[TestFixture("boltdb", "lightninglabs/lnd", "v0.19.3-beta", "/root/.lnd", true)]
public class AbcLightningAbstractTestsBoltDb : LNUnit.Tests.Abstract.AbcLightningAbstractTests
{
    public AbcLightningAbstractTestsBoltDb(string dbType = "boltdb",
        string lndImage = "custom_lnd",
        string tag = "latest",
        string lndRoot = "/root/.lnd",
        bool pullImage = false
    ) : base(dbType, lndImage, tag, lndRoot, pullImage)
    {

    }



}