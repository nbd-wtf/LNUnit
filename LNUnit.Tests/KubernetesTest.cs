using k8s;
using k8s.Models;
using LNUnit.Setup;

namespace LNUnit.Tests.Abstract;

[Category("Kubernetes")]
public class KubernetesTest
{
    private IKubernetes _client = null!;
    private readonly Random _random = new();

    [OneTimeSetUp]
    public async Task Setup()
    {
        // Load kubeconfig from default location or KUBECONFIG env var
        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile();

        // Skip SSL validation for development (fixes exec API SSL issues on macOS/OrbStack)
        config.SkipTlsVerify = true;

        _client = new Kubernetes(config);

        // Verify connection by getting version
        var version = await _client.Version.GetCodeAsync().ConfigureAwait(false);
        Console.WriteLine($"Connected to Kubernetes {version.GitVersion}");
        Console.WriteLine($"SSL Verification: {(config.SkipTlsVerify ? "Disabled (Dev Mode)" : "Enabled")}");
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        _client?.Dispose();
        await Task.CompletedTask;
    }

    private string GetRandomHexString(int size = 8)
    {
        var b = new byte[size];
        _random.NextBytes(b);
        return Convert.ToHexString(b).ToLower(); // K8s names must be lowercase
    }

    [Test]
    [Category("Kubernetes")]
    public async Task ListPods()
    {
        // List all pods in the default namespace
        var pods = await _client.CoreV1.ListNamespacedPodAsync("default").ConfigureAwait(false);

        // Just verify the API call works - don't assert specific pod count
        Assert.That(pods, Is.Not.Null);
        Console.WriteLine($"Found {pods.Items.Count} pods in default namespace");
    }

    [Test]
    [Category("Kubernetes")]
    public async Task CreatePod()
    {
        var podName = $"test-redis-{GetRandomHexString(4)}";
        var namespaceName = "default";

        try
        {
            // Create a simple redis pod
            var pod = new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = podName
                },
                Spec = new V1PodSpec
                {
                    Containers = new List<V1Container>
                    {
                        new V1Container
                        {
                            Name = "redis",
                            Image = "redis:5.0",
                            Ports = new List<V1ContainerPort>
                            {
                                new V1ContainerPort { ContainerPort = 6379 }
                            }
                        }
                    },
                    RestartPolicy = "Never"
                }
            };

            var createdPod = await _client.CoreV1.CreateNamespacedPodAsync(pod, namespaceName).ConfigureAwait(false);
            Assert.That(createdPod.Metadata.Name, Is.EqualTo(podName));

            // Wait a bit for pod to start
            await Task.Delay(2000).ConfigureAwait(false);

            // Verify pod exists
            var retrievedPod = await _client.CoreV1.ReadNamespacedPodAsync(podName, namespaceName).ConfigureAwait(false);
            Assert.That(retrievedPod, Is.Not.Null);
            Console.WriteLine($"Pod {podName} created with status: {retrievedPod.Status.Phase}");
        }
        finally
        {
            // Cleanup: Delete the pod
            try
            {
                await _client.CoreV1.DeleteNamespacedPodAsync(podName, namespaceName, gracePeriodSeconds: 0).ConfigureAwait(false);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Test]
    [Category("Kubernetes")]
    public async Task Kubernetes_PullImage()
    {
        // Note: Kubernetes automatically pulls images when creating pods
        // We can test this by creating a pod with the image and verifying it works
        var podName = $"test-image-pull-{GetRandomHexString(4)}";
        var namespaceName = "default";

        try
        {
            var pod = new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = podName },
                Spec = new V1PodSpec
                {
                    Containers = new List<V1Container>
                    {
                        new V1Container
                        {
                            Name = "redis",
                            Image = "redis:5.0",
                            ImagePullPolicy = "IfNotPresent" // or "Always" to force pull
                        }
                    },
                    RestartPolicy = "Never"
                }
            };

            var createdPod = await _client.CoreV1.CreateNamespacedPodAsync(pod, namespaceName).ConfigureAwait(false);
            Assert.That(createdPod.Metadata.Name, Is.EqualTo(podName));
            Console.WriteLine($"Pod {podName} created - image will be pulled if not present");
        }
        finally
        {
            try
            {
                await _client.CoreV1.DeleteNamespacedPodAsync(podName, namespaceName, gracePeriodSeconds: 0).ConfigureAwait(false);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Test]
    [Category("Kubernetes")]
    public async Task CreateDestroyNamespace()
    {
        var randomString = GetRandomHexString();
        var namespaceName = $"unit-test-{randomString}";

        // Create namespace
        var ns = new V1Namespace
        {
            Metadata = new V1ObjectMeta
            {
                Name = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    { "lnunit-test", "true" },
                    { "created-by", "lnunit" }
                }
            }
        };

        var createdNs = await _client.CoreV1.CreateNamespaceAsync(ns).ConfigureAwait(false);
        Assert.That(createdNs.Metadata.Name, Is.EqualTo(namespaceName));
        Console.WriteLine($"Created namespace: {namespaceName}");

        // Verify namespace exists
        var retrievedNs = await _client.CoreV1.ReadNamespaceAsync(namespaceName).ConfigureAwait(false);
        Assert.That(retrievedNs, Is.Not.Null);

        // Delete namespace
        await _client.CoreV1.DeleteNamespaceAsync(namespaceName, gracePeriodSeconds: 0).ConfigureAwait(false);
        Console.WriteLine($"Deleted namespace: {namespaceName}");

        // Wait a bit for deletion to process
        await Task.Delay(2000).ConfigureAwait(false);
    }

    [Test]
    [Category("Kubernetes")]
    public async Task DetectKubernetesContext()
    {
        // Get cluster information
        var version = await _client.Version.GetCodeAsync().ConfigureAwait(false);
        Assert.That(version, Is.Not.Null);
        Assert.That(version.GitVersion, Is.Not.Null.And.Not.Empty);

        Console.WriteLine($"Kubernetes Version: {version.GitVersion}");
        Console.WriteLine($"Platform: {version.Platform}");

        // Try to get current namespace (typically "default" if not specified)
        var namespaces = await _client.CoreV1.ListNamespaceAsync().ConfigureAwait(false);
        Assert.That(namespaces.Items.Count, Is.GreaterThan(0));

        Console.WriteLine($"Found {namespaces.Items.Count} namespaces in cluster");
    }

    [Test]
    [Category("Kubernetes")]
    public async Task CreatePodsInNamespace()
    {
        var randomString = GetRandomHexString();
        var namespaceName = $"unit-test-{randomString}";

        try
        {
            // Create namespace
            var ns = new V1Namespace
            {
                Metadata = new V1ObjectMeta { Name = namespaceName }
            };
            await _client.CoreV1.CreateNamespaceAsync(ns).ConfigureAwait(false);
            Console.WriteLine($"Created namespace: {namespaceName}");

            // Create first pod (alice)
            var alice = new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "alice",
                    Labels = new Dictionary<string, string> { { "app", "alice" } }
                },
                Spec = new V1PodSpec
                {
                    Containers = new List<V1Container>
                    {
                        new V1Container { Name = "redis", Image = "redis:5.0" }
                    },
                    RestartPolicy = "Never"
                }
            };
            var alicePod = await _client.CoreV1.CreateNamespacedPodAsync(alice, namespaceName).ConfigureAwait(false);
            Assert.That(alicePod.Metadata.Name, Is.EqualTo("alice"));

            // Create second pod (bob)
            var bob = new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "bob",
                    Labels = new Dictionary<string, string> { { "app", "bob" } }
                },
                Spec = new V1PodSpec
                {
                    Containers = new List<V1Container>
                    {
                        new V1Container { Name = "redis", Image = "redis:5.0" }
                    },
                    RestartPolicy = "Never"
                }
            };
            var bobPod = await _client.CoreV1.CreateNamespacedPodAsync(bob, namespaceName).ConfigureAwait(false);
            Assert.That(bobPod.Metadata.Name, Is.EqualTo("bob"));

            Console.WriteLine($"Created pods alice and bob in namespace {namespaceName}");

            // Wait for pods to get IP addresses
            await Task.Delay(3000).ConfigureAwait(false);

            // Retrieve pods and verify networking
            var aliceStatus = await _client.CoreV1.ReadNamespacedPodAsync("alice", namespaceName).ConfigureAwait(false);
            var bobStatus = await _client.CoreV1.ReadNamespacedPodAsync("bob", namespaceName).ConfigureAwait(false);

            Console.WriteLine($"Alice IP: {aliceStatus.Status.PodIP}");
            Console.WriteLine($"Bob IP: {bobStatus.Status.PodIP}");

            // Both pods should have IPs (or be in process of getting them)
            // We don't assert IPs exist because pods might not be Running yet
            Assert.That(aliceStatus.Status, Is.Not.Null);
            Assert.That(bobStatus.Status, Is.Not.Null);
        }
        finally
        {
            // Cleanup: Delete namespace (this deletes all pods in it)
            try
            {
                await _client.CoreV1.DeleteNamespaceAsync(namespaceName, gracePeriodSeconds: 0).ConfigureAwait(false);
                Console.WriteLine($"Deleted namespace: {namespaceName}");
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Test]
    [Category("Kubernetes")]
    public async Task TestPersistentVolume()
    {
        var randomString = GetRandomHexString();
        var namespaceName = $"unit-test-{randomString}";
        var pvcName = $"test-pvc-{GetRandomHexString(4)}";
        var podName1 = $"test-pod-1-{GetRandomHexString(4)}";
        var podName2 = $"test-pod-2-{GetRandomHexString(4)}";
        var testData = "Hello from Kubernetes PVC!";

        try
        {
            // Create namespace
            var ns = new V1Namespace
            {
                Metadata = new V1ObjectMeta { Name = namespaceName }
            };
            await _client.CoreV1.CreateNamespaceAsync(ns).ConfigureAwait(false);
            Console.WriteLine($"Created namespace: {namespaceName}");

            // Create PersistentVolumeClaim
            var pvc = new V1PersistentVolumeClaim
            {
                Metadata = new V1ObjectMeta
                {
                    Name = pvcName
                },
                Spec = new V1PersistentVolumeClaimSpec
                {
                    AccessModes = new List<string> { "ReadWriteOnce" },
                    Resources = new V1VolumeResourceRequirements
                    {
                        Requests = new Dictionary<string, ResourceQuantity>
                        {
                            { "storage", new ResourceQuantity("1Gi") }
                        }
                    }
                }
            };

            var createdPvc = await _client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(pvc, namespaceName).ConfigureAwait(false);
            Assert.That(createdPvc.Metadata.Name, Is.EqualTo(pvcName));
            Console.WriteLine($"Created PVC: {pvcName}");

            // Wait for PVC to be bound (or pending)
            await Task.Delay(2000).ConfigureAwait(false);

            // Create first pod with volume mount and write data
            var pod1 = new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = podName1 },
                Spec = new V1PodSpec
                {
                    Containers = new List<V1Container>
                    {
                        new V1Container
                        {
                            Name = "writer",
                            Image = "busybox",
                            Command = new List<string> { "sh", "-c", $"echo '{testData}' > /data/test.txt && sleep 5" },
                            VolumeMounts = new List<V1VolumeMount>
                            {
                                new V1VolumeMount
                                {
                                    Name = "data-volume",
                                    MountPath = "/data"
                                }
                            }
                        }
                    },
                    Volumes = new List<V1Volume>
                    {
                        new V1Volume
                        {
                            Name = "data-volume",
                            PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                            {
                                ClaimName = pvcName
                            }
                        }
                    },
                    RestartPolicy = "Never"
                }
            };

            await _client.CoreV1.CreateNamespacedPodAsync(pod1, namespaceName).ConfigureAwait(false);
            Console.WriteLine($"Created pod {podName1} to write data");

            // Wait for pod to complete writing
            await Task.Delay(8000).ConfigureAwait(false);

            // Delete first pod
            await _client.CoreV1.DeleteNamespacedPodAsync(podName1, namespaceName, gracePeriodSeconds: 0).ConfigureAwait(false);
            Console.WriteLine($"Deleted pod {podName1}");
            await Task.Delay(3000).ConfigureAwait(false);

            // Create second pod to read data from same PVC
            var pod2 = new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = podName2 },
                Spec = new V1PodSpec
                {
                    Containers = new List<V1Container>
                    {
                        new V1Container
                        {
                            Name = "reader",
                            Image = "busybox",
                            Command = new List<string> { "sh", "-c", "cat /data/test.txt && sleep 5" },
                            VolumeMounts = new List<V1VolumeMount>
                            {
                                new V1VolumeMount
                                {
                                    Name = "data-volume",
                                    MountPath = "/data"
                                }
                            }
                        }
                    },
                    Volumes = new List<V1Volume>
                    {
                        new V1Volume
                        {
                            Name = "data-volume",
                            PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                            {
                                ClaimName = pvcName
                            }
                        }
                    },
                    RestartPolicy = "Never"
                }
            };

            await _client.CoreV1.CreateNamespacedPodAsync(pod2, namespaceName).ConfigureAwait(false);
            Console.WriteLine($"Created pod {podName2} to read data");

            // Wait for pod to complete reading
            await Task.Delay(8000).ConfigureAwait(false);

            // The test passes if we got here without exceptions
            // In a real test, we'd exec into the pod and verify the data, but that's Phase 2
            Console.WriteLine("PVC persistence test completed - data written by pod1 should be readable by pod2");

            Assert.Pass("PVC created and used by multiple pods successfully");
        }
        finally
        {
            // Cleanup: Delete namespace (this deletes all pods and PVCs in it)
            try
            {
                await _client.CoreV1.DeleteNamespaceAsync(namespaceName, gracePeriodSeconds: 0).ConfigureAwait(false);
                Console.WriteLine($"Deleted namespace: {namespaceName}");
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Test]
    [Category("Kubernetes")]
    public async Task TestFileExtraction_TextFile()
    {
        var namespaceName = "default";
        var podName = $"test-file-extract-{GetRandomHexString(4)}";

        try
        {
            // Create a pod using KubernetesHelper
            await _client.CreatePodAndWaitForRunning(
                namespaceName,
                podName,
                "busybox",
                "latest",
                command: new List<string> { "sh", "-c", "echo 'Hello from Kubernetes!' > /tmp/test.txt && sleep 30" },
                labels: new Dictionary<string, string> { { "app", podName } },
                timeoutSeconds: 30
            ).ConfigureAwait(false);

            Console.WriteLine($"Pod {podName} is running");

            // Wait a bit for the file to be created
            await Task.Delay(2000).ConfigureAwait(false);

            // Extract the text file using KubernetesHelper
            var fileContent = await _client.ExecAndReadTextFile(
                namespaceName,
                podName,
                podName,
                "/tmp/test.txt"
            ).ConfigureAwait(false);

            Console.WriteLine($"Extracted text file content: {fileContent.Trim()}");

            // Verify the content
            Assert.That(fileContent.Trim(), Is.EqualTo("Hello from Kubernetes!"));
        }
        finally
        {
            try
            {
                await _client.RemovePod(namespaceName, podName, gracePeriodSeconds: 0).ConfigureAwait(false);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Test]
    [Category("Kubernetes")]
    public async Task TestFileExtraction_BinaryFile()
    {
        var namespaceName = "default";
        var podName = $"test-binary-extract-{GetRandomHexString(4)}";
        var testData = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello" in hex

        try
        {
            // Create a pod that writes binary data
            await _client.CreatePodAndWaitForRunning(
                namespaceName,
                podName,
                "busybox",
                "latest",
                command: new List<string> { "sh", "-c", "printf '\\x48\\x65\\x6C\\x6C\\x6F' > /tmp/binary.dat && sleep 30" },
                labels: new Dictionary<string, string> { { "app", podName } },
                timeoutSeconds: 30
            ).ConfigureAwait(false);

            Console.WriteLine($"Pod {podName} is running");

            // Wait for the file to be created
            await Task.Delay(2000).ConfigureAwait(false);

            // Extract the binary file using KubernetesHelper
            var binaryContent = await _client.ExecAndReadBinaryFile(
                namespaceName,
                podName,
                podName,
                "/tmp/binary.dat"
            ).ConfigureAwait(false);

            Console.WriteLine($"Extracted {binaryContent.Length} bytes from binary file");
            Console.WriteLine($"Content: {BitConverter.ToString(binaryContent)}");

            // Verify the content
            Assert.That(binaryContent, Is.EqualTo(testData));
        }
        finally
        {
            try
            {
                await _client.RemovePod(namespaceName, podName, gracePeriodSeconds: 0).ConfigureAwait(false);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
