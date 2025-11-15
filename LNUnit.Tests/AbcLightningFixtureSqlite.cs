using LNUnit.Tests.Abstract;

// using LNBolt;

namespace LNUnit.Tests;

//[Ignore("only local")]
//[TestFixture("sqlite", "custom_lnd", "latest", "/home/lnd/.lnd", false)]
[TestFixture("sqlite", "lightninglabs/lnd", "v0.20.0-beta", "/root/.lnd", true)]
public class AbcLightningAbstractTestsSqlite : AbcLightningAbstractTests
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