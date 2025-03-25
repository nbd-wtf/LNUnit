

namespace LNUnit.Tests.Fixture;

[Ignore("only local")]
//[TestFixture("postgres", "lightninglabs/lnd", "v0.18.3-beta", "/root/.lnd", true)]
[TestFixture("postgres", "custom_lnd", "latest", "/home/lnd/.lnd", false)]
public class AbcLightningAbstractTestsPostgres : LNUnit.Tests.Abstract.AbcLightningAbstractTests
{
    public AbcLightningAbstractTestsPostgres(string dbType = "postgres",
        string lndImage = "custom_lnd",
        string tag = "latest",
        string lndRoot = "/root/.lnd",
        bool pullImage = false
    ) : base(dbType, lndImage, tag, lndRoot, pullImage)
    {

    }
}

