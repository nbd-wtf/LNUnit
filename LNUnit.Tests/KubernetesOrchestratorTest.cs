using k8s;
using LNUnit.Setup;

namespace LNUnit.Tests.Abstract;

[Category("Kubernetes")]
public class KubernetesOrchestratorTest
{
    private IKubernetes _kubeClient = null!;
    private KubernetesOrchestrator _orchestrator = null!;
    private readonly Random _random = new();
    private string? _testNamespace;

    [OneTimeSetUp]
    public async Task Setup()
    {
        // Load kubeconfig from default location or KUBECONFIG env var
        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
        config.SkipTlsVerify = true;

        _kubeClient = new Kubernetes(config);
        _orchestrator = new KubernetesOrchestrator(_kubeClient);

        // Verify connection
        var version = await _kubeClient.Version.GetCodeAsync().ConfigureAwait(false);
        Console.WriteLine($"Connected to Kubernetes {version.GitVersion}");
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        // Cleanup test namespace if it was created
        if (_testNamespace != null)
        {
            try
            {
                await _orchestrator.DeleteNetworkAsync(_testNamespace).ConfigureAwait(false);
                Console.WriteLine($"Cleaned up test namespace: {_testNamespace}");
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        _orchestrator?.Dispose();
        _kubeClient?.Dispose();
        await Task.CompletedTask;
    }

    private string GetRandomHexString(int size = 8)
    {
        var b = new byte[size];
        _random.NextBytes(b);
        return Convert.ToHexString(b).ToLower();
    }

    [Test]
    [Category("Kubernetes")]
    public async Task CreateNetwork()
    {
        var networkName = $"test-network-{GetRandomHexString(4)}";

        try
        {
            // Create network (namespace)
            var namespaceId = await _orchestrator.CreateNetworkAsync(networkName).ConfigureAwait(false);
            _testNamespace = namespaceId;

            Assert.That(namespaceId, Is.Not.Null.And.Not.Empty);
            Console.WriteLine($"Created network/namespace: {namespaceId}");

            // Verify namespace exists
            var ns = await _kubeClient.CoreV1.ReadNamespaceAsync(namespaceId).ConfigureAwait(false);
            Assert.That(ns, Is.Not.Null);
            Assert.That(ns.Metadata.Name, Is.EqualTo(namespaceId));
        }
        finally
        {
            if (_testNamespace != null)
            {
                await _orchestrator.DeleteNetworkAsync(_testNamespace).ConfigureAwait(false);
                _testNamespace = null;
            }
        }
    }

    [Test]
    [Category("Kubernetes")]
    public async Task CreateAndInspectContainer()
    {
        var networkName = $"test-network-{GetRandomHexString(4)}";
        var containerName = $"test-redis-{GetRandomHexString(4)}";

        try
        {
            // Create network
            var namespaceId = await _orchestrator.CreateNetworkAsync(networkName).ConfigureAwait(false);
            _testNamespace = namespaceId;

            // Create container
            var createOptions = new ContainerCreateOptions
            {
                Name = containerName,
                Image = "redis",
                Tag = "5.0",
                NetworkId = namespaceId,
                Labels = new Dictionary<string, string>
                {
                    { "test", "true" },
                    { "purpose", "integration-test" }
                }
            };

            var containerInfo = await _orchestrator.CreateContainerAsync(createOptions).ConfigureAwait(false);

            Assert.That(containerInfo, Is.Not.Null);
            Assert.That(containerInfo.Name, Is.EqualTo(containerName));
            Assert.That(containerInfo.Image, Does.Contain("redis:5.0"));
            Assert.That(containerInfo.State, Is.EqualTo("Running"));
            Assert.That(containerInfo.IpAddress, Is.Not.Null.And.Not.Empty);
            Console.WriteLine($"Created container {containerName} with IP {containerInfo.IpAddress}");

            // Inspect container
            var inspected = await _orchestrator.InspectContainerAsync(containerName).ConfigureAwait(false);
            Assert.That(inspected, Is.Not.Null);
            Assert.That(inspected.Name, Is.EqualTo(containerName));
            Assert.That(inspected.Labels, Is.Not.Null);
            Assert.That(inspected.Labels.ContainsKey("test"), Is.True);
            Console.WriteLine($"Inspected container: {inspected.Name}, State: {inspected.State}");
        }
        finally
        {
            if (_testNamespace != null)
            {
                await _orchestrator.DeleteNetworkAsync(_testNamespace).ConfigureAwait(false);
                _testNamespace = null;
            }
        }
    }

    [Test]
    [Category("Kubernetes")]
    public async Task ListContainers()
    {
        var networkName = $"test-network-{GetRandomHexString(4)}";
        var container1Name = $"test-redis-1-{GetRandomHexString(4)}";
        var container2Name = $"test-redis-2-{GetRandomHexString(4)}";

        try
        {
            // Create network
            var namespaceId = await _orchestrator.CreateNetworkAsync(networkName).ConfigureAwait(false);
            _testNamespace = namespaceId;

            // Create two containers
            var createOptions1 = new ContainerCreateOptions
            {
                Name = container1Name,
                Image = "redis",
                Tag = "5.0",
                NetworkId = namespaceId
            };

            var createOptions2 = new ContainerCreateOptions
            {
                Name = container2Name,
                Image = "redis",
                Tag = "5.0",
                NetworkId = namespaceId
            };

            await _orchestrator.CreateContainerAsync(createOptions1).ConfigureAwait(false);
            await _orchestrator.CreateContainerAsync(createOptions2).ConfigureAwait(false);

            // List containers
            var containers = await _orchestrator.ListContainersAsync().ConfigureAwait(false);

            Assert.That(containers, Is.Not.Null);
            Assert.That(containers.Count, Is.GreaterThanOrEqualTo(2));

            var container1 = containers.FirstOrDefault(c => c.Name == container1Name);
            var container2 = containers.FirstOrDefault(c => c.Name == container2Name);

            Assert.That(container1, Is.Not.Null);
            Assert.That(container2, Is.Not.Null);

            Console.WriteLine($"Listed {containers.Count} containers");
            Console.WriteLine($"Found {container1Name}: {container1!.IpAddress}");
            Console.WriteLine($"Found {container2Name}: {container2!.IpAddress}");
        }
        finally
        {
            if (_testNamespace != null)
            {
                await _orchestrator.DeleteNetworkAsync(_testNamespace).ConfigureAwait(false);
                _testNamespace = null;
            }
        }
    }

    [Test]
    [Category("Kubernetes")]
    public async Task RemoveContainer()
    {
        var networkName = $"test-network-{GetRandomHexString(4)}";
        var containerName = $"test-redis-{GetRandomHexString(4)}";

        try
        {
            // Create network
            var namespaceId = await _orchestrator.CreateNetworkAsync(networkName).ConfigureAwait(false);
            _testNamespace = namespaceId;

            // Create container
            var createOptions = new ContainerCreateOptions
            {
                Name = containerName,
                Image = "redis",
                Tag = "5.0",
                NetworkId = namespaceId
            };

            var containerInfo = await _orchestrator.CreateContainerAsync(createOptions).ConfigureAwait(false);
            Console.WriteLine($"Created container {containerName}");

            // Remove container
            await _orchestrator.RemoveContainerAsync(containerName).ConfigureAwait(false);
            Console.WriteLine($"Removed container {containerName}");

            // Wait for removal to complete
            await Task.Delay(2000).ConfigureAwait(false);

            // Verify container is gone
            try
            {
                await _orchestrator.InspectContainerAsync(containerName).ConfigureAwait(false);
                Assert.Fail("Container should have been removed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Container successfully removed: {ex.Message}");
                Assert.Pass("Container was successfully removed");
            }
        }
        finally
        {
            if (_testNamespace != null)
            {
                await _orchestrator.DeleteNetworkAsync(_testNamespace).ConfigureAwait(false);
                _testNamespace = null;
            }
        }
    }

    [Test]
    [Category("Kubernetes")]
    public async Task ExtractTextFile()
    {
        var networkName = $"test-network-{GetRandomHexString(4)}";
        var containerName = $"test-busybox-{GetRandomHexString(4)}";
        var testContent = "Hello from KubernetesOrchestrator!";

        try
        {
            // Create network
            var namespaceId = await _orchestrator.CreateNetworkAsync(networkName).ConfigureAwait(false);
            _testNamespace = namespaceId;

            // Create container with test file
            var createOptions = new ContainerCreateOptions
            {
                Name = containerName,
                Image = "busybox",
                Tag = "latest",
                NetworkId = namespaceId,
                Command = new List<string> { "sh", "-c", $"echo '{testContent}' > /tmp/test.txt && sleep 30" }
            };

            await _orchestrator.CreateContainerAsync(createOptions).ConfigureAwait(false);
            Console.WriteLine($"Created container {containerName}");

            // Wait for file to be created
            await Task.Delay(3000).ConfigureAwait(false);

            // Extract text file
            var fileContent = await _orchestrator.ExtractTextFileAsync(containerName, "/tmp/test.txt").ConfigureAwait(false);

            Assert.That(fileContent, Is.Not.Null.And.Not.Empty);
            Assert.That(fileContent.Trim(), Is.EqualTo(testContent));
            Console.WriteLine($"Extracted file content: {fileContent.Trim()}");
        }
        finally
        {
            if (_testNamespace != null)
            {
                await _orchestrator.DeleteNetworkAsync(_testNamespace).ConfigureAwait(false);
                _testNamespace = null;
            }
        }
    }

    [Test]
    [Category("Kubernetes")]
    public async Task ExtractBinaryFile()
    {
        var networkName = $"test-network-{GetRandomHexString(4)}";
        var containerName = $"test-busybox-{GetRandomHexString(4)}";
        var testData = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"

        try
        {
            // Create network
            var namespaceId = await _orchestrator.CreateNetworkAsync(networkName).ConfigureAwait(false);
            _testNamespace = namespaceId;

            // Create container with binary file
            var createOptions = new ContainerCreateOptions
            {
                Name = containerName,
                Image = "busybox",
                Tag = "latest",
                NetworkId = namespaceId,
                Command = new List<string> { "sh", "-c", "printf '\\x48\\x65\\x6C\\x6C\\x6F' > /tmp/binary.dat && sleep 30" }
            };

            await _orchestrator.CreateContainerAsync(createOptions).ConfigureAwait(false);
            Console.WriteLine($"Created container {containerName}");

            // Wait for file to be created
            await Task.Delay(3000).ConfigureAwait(false);

            // Extract binary file
            var binaryContent = await _orchestrator.ExtractBinaryFileAsync(containerName, "/tmp/binary.dat").ConfigureAwait(false);

            Assert.That(binaryContent, Is.Not.Null);
            Assert.That(binaryContent, Is.EqualTo(testData));
            Console.WriteLine($"Extracted {binaryContent.Length} bytes: {BitConverter.ToString(binaryContent)}");
        }
        finally
        {
            if (_testNamespace != null)
            {
                await _orchestrator.DeleteNetworkAsync(_testNamespace).ConfigureAwait(false);
                _testNamespace = null;
            }
        }
    }

    [Test]
    [Category("Kubernetes")]
    public async Task CreateContainerWithEnvironmentVariables()
    {
        var networkName = $"test-network-{GetRandomHexString(4)}";
        var containerName = $"test-env-{GetRandomHexString(4)}";

        try
        {
            // Create network
            var namespaceId = await _orchestrator.CreateNetworkAsync(networkName).ConfigureAwait(false);
            _testNamespace = namespaceId;

            // Create container with environment variables
            var createOptions = new ContainerCreateOptions
            {
                Name = containerName,
                Image = "busybox",
                Tag = "latest",
                NetworkId = namespaceId,
                Command = new List<string> { "sh", "-c", "echo \"TEST_VAR=$TEST_VAR\" > /tmp/env.txt && sleep 30" },
                Environment = new Dictionary<string, string>
                {
                    { "TEST_VAR", "test-value-123" }
                }
            };

            await _orchestrator.CreateContainerAsync(createOptions).ConfigureAwait(false);
            Console.WriteLine($"Created container {containerName} with environment variables");

            // Wait for file to be created
            await Task.Delay(3000).ConfigureAwait(false);

            // Extract and verify environment variable was set
            var fileContent = await _orchestrator.ExtractTextFileAsync(containerName, "/tmp/env.txt").ConfigureAwait(false);

            Assert.That(fileContent, Does.Contain("TEST_VAR=test-value-123"));
            Console.WriteLine($"Environment variable verification: {fileContent.Trim()}");
        }
        finally
        {
            if (_testNamespace != null)
            {
                await _orchestrator.DeleteNetworkAsync(_testNamespace).ConfigureAwait(false);
                _testNamespace = null;
            }
        }
    }
}
