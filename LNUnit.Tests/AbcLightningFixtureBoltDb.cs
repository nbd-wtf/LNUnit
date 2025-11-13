using LNUnit.Tests.Abstract;
// using LNBolt;

namespace LNUnit.Tests;

[Ignore("only local")]
//[TestFixture("boltdb", "custom_lnd", "latest", "/home/lnd/.lnd", false)]
[TestFixture("boltdb", "lightninglabs/lnd", "v0.19.3-beta", "/root/.lnd", true)]
public class AbcLightningAbstractTestsBoltDb : AbcLightningAbstractTests
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