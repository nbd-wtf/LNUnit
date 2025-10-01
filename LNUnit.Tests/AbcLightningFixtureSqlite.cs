

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

//[Ignore("only local")]
[TestFixture("sqlite", "custom_lnd", "latest", "/home/lnd/.lnd", false)]
//[TestFixture("sqlite", "lightninglabs/lnd", "v0.19.3-beta", "/root/.lnd", true)]
public class AbcLightningAbstractTestsSqlite : LNUnit.Tests.Abstract.AbcLightningAbstractTests
{
    public AbcLightningAbstractTestsSqlite(string dbType = "sqlite",
        string lndImage = "custom_lnd",
        string tag = "latest",
        string lndRoot = "/root/.lnd",
        bool pullImage = false
    ) : base(dbType, lndImage, tag, lndRoot, pullImage)
    {

    }



}