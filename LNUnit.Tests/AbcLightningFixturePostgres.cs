using LNUnit.Tests.Abstract;

namespace LNUnit.Tests.Fixture;

[TestFixture("postgres", "lightninglabs/lnd", "daily-testing-only", "/root/.lnd", true)]
//[TestFixture("postgres", "custom_lnd", "latest", "/home/lnd/.lnd", false)]
public class AbcLightningAbstractTestsPostgres : AbcLightningAbstractTests
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