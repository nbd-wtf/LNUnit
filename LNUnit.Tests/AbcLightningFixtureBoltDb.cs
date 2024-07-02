

namespace LNUnit.Tests;

[TestFixture("boltdb", "lightninglabs/lnd", "daily-testing-only", "/root/.lnd", true)]
// [TestFixture("boltdb", "lightninglabs/lnd", "v0.17.5-beta", "/root/.lnd", false)]
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