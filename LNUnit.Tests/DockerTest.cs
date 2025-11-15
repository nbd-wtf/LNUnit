using Docker.DotNet;
using Docker.DotNet.Models;
using LNUnit.Setup;

namespace LNUnit.Tests.Abstract;

[Ignore("only local")]
public class DockerTest
{
    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly Random _random = new();

    private bool _sampleImagePulled;

    [OneTimeSetUp]
    public async Task Setup()
    {
        await PullRedis5();
        await _client.Networks.PruneNetworksAsync(new NetworksDeleteUnusedParameters());
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        _client.Dispose();
    }

    private void ResetContainer(string name)
    {
        try
        {
            _client.Containers.RemoveContainerAsync(name,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
        }
        catch
        {
            // ignored
        }
    }

    [Test]
    [Category("Docker")]
    public async Task ListContainer()
    {
        IList<ContainerListResponse> containers = await _client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                Limit = 10
            });
        // Assert.That(containers.Any());
    }


    [Test]
    [Category("Docker")]
    public async Task MakeContainer()
    {
        var x = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = "redis:5.0",
            HostConfig = new HostConfig
            {
                DNS = new List<string>
                {
                    "8.8.8.8"
                }
            }
        });
        await _client.Containers.RemoveContainerAsync(x.ID,
            new ContainerRemoveParameters { RemoveVolumes = true, Force = true });
    }

    [Test]
    [Category("Docker")]
    public async Task Docker_PullImage()
    {
        await PullRedis5();
    }

    private async Task PullRedis5()
    {
        if (_sampleImagePulled)
            return;
        await _client.PullImageAndWaitForCompleted("redis", "5.0");
        _sampleImagePulled = true;
    }


    [Test]
    [Category("Docker")]
    public async Task CreateDestroyNetwork()
    {
        var randomString = GetRandomHexString();
        var networksCreateResponse = await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = $"unit_test_{randomString}",
            Driver = "bridge"
            //CheckDuplicate = true
        });
        await _client.Networks.DeleteNetworkAsync(networksCreateResponse.ID);
    }

    private string GetRandomHexString(int size = 8)
    {
        var b = new byte[size];
        _random.NextBytes(b);
        return Convert.ToHexString(b);
    }


    [Test]
    [Category("Docker")]
    public async Task BuildDockerImage()
    {
        await _client.CreateDockerImageFromPath("./../../../../Docker/lnd",
            new List<string> { "custom_lnd", "custom_lnd:latest" });
    }

    // [Test]
    // [Category("Docker")]
    // public async Task BuildBitcoin_27_0_rc1_DockerImage()
    // {
    //     await _client.CreateDockerImageFromPath("./../../../../Docker/bitcoin/27.0rc1",
    //         new List<string> { "bitcoin:27.0rc1" });
    // }


    [Test]
    [Category("Docker")]
    public async Task DetectGitlabRunner()
    {
        await _client.GetGitlabRunnerNetworkId();
    }

    // public string GetIPAddress()
    // {
    //     string IPAddress = "";
    //     IPHostEntry Host = default(IPHostEntry);
    //     string Hostname = null;
    //     Hostname = System.Environment.MachineName;
    //     Host = Dns.GetHostEntry(Hostname);
    //     foreach (IPAddress IP in Host.AddressList)
    //     {
    //         if (IP.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    //         {
    //             IPAddress = Convert.ToString(IP);
    //         }
    //     }
    //
    //     return IPAddress;
    // }

    [Test]
    [Category("Docker")]
    public async Task ComposeAndDestroyCluster()
    {
        await PullRedis5();
        var randomString = GetRandomHexString();
        var networksCreateResponse = await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = $"unit_test_{randomString}",
            Driver = "bridge"
            // CheckDuplicate = true
        });
        Assert.IsEmpty(networksCreateResponse.Warning);

        var alice = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = "redis:5.0",
            HostConfig = new HostConfig
            {
                NetworkMode = $"unit_test_{randomString}"
            }
        });
        Assert.IsEmpty(alice.Warnings);

        var bob = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = "redis:5.0",
            HostConfig = new HostConfig
            {
                NetworkMode = $"unit_test_{randomString}"
            }
        });
        Assert.IsEmpty(bob.Warnings);

        await _client.Containers.RemoveContainerAsync(alice.ID,
            new ContainerRemoveParameters { RemoveVolumes = true, Force = true });
        await _client.Containers.RemoveContainerAsync(bob.ID,
            new ContainerRemoveParameters { RemoveVolumes = true, Force = true });
        await _client.Networks.DeleteNetworkAsync(networksCreateResponse.ID);
    }
}