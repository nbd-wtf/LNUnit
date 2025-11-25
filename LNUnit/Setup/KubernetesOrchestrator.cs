using k8s;
using k8s.Models;

namespace LNUnit.Setup;

/// <summary>
/// Kubernetes implementation of IContainerOrchestrator
/// </summary>
public class KubernetesOrchestrator : IContainerOrchestrator
{
    private readonly IKubernetes _client;
    private readonly Dictionary<string, string> _networkToNamespaceMap = new();
    private readonly string _defaultNamespace;

    public KubernetesOrchestrator(string? defaultNamespace = null)
    {
        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
        config.SkipTlsVerify = true; // For development environments
        _client = new Kubernetes(config);
        _defaultNamespace = defaultNamespace ?? "default";
    }

    public KubernetesOrchestrator(IKubernetes client, string? defaultNamespace = null)
    {
        _client = client;
        _defaultNamespace = defaultNamespace ?? "default";
    }

    // Network/Namespace management
    public async Task<string> CreateNetworkAsync(string networkName)
    {
        // In Kubernetes, networks are namespaces
        var namespaceName = await _client.CreateTestNamespace(networkName).ConfigureAwait(false);
        _networkToNamespaceMap[networkName] = namespaceName;
        return namespaceName;
    }

    public async Task DeleteNetworkAsync(string networkId)
    {
        await _client.DeleteNamespace(networkId).ConfigureAwait(false);

        // Remove from map if it exists
        var key = _networkToNamespaceMap.FirstOrDefault(x => x.Value == networkId).Key;
        if (key != null)
        {
            _networkToNamespaceMap.Remove(key);
        }
    }

    // Container/Pod management
    public async Task<ContainerInfo> CreateContainerAsync(ContainerCreateOptions options)
    {
        var namespaceName = options.NetworkId ?? _defaultNamespace;

        // Convert Docker-style volume binds to Kubernetes volumes
        List<V1Volume>? volumes = null;
        List<V1VolumeMount>? volumeMounts = null;

        if (options.Binds?.Any() == true)
        {
            volumes = new List<V1Volume>();
            volumeMounts = new List<V1VolumeMount>();

            foreach (var bind in options.Binds)
            {
                // Docker format: /host/path:/container/path or volumeName:/container/path
                var parts = bind.Split(':');
                if (parts.Length >= 2)
                {
                    var volumeName = $"{options.Name}-vol-{volumes.Count}";
                    var mountPath = parts[1];

                    volumes.Add(new V1Volume
                    {
                        Name = volumeName,
                        EmptyDir = new V1EmptyDirVolumeSource() // Using emptyDir for simplicity
                    });

                    volumeMounts.Add(new V1VolumeMount
                    {
                        Name = volumeName,
                        MountPath = mountPath
                    });
                }
            }
        }

        var labels = options.Labels ?? new Dictionary<string, string> { { "app", options.Name } };

        var pod = await _client.CreatePodAndWaitForRunning(
            namespaceName,
            options.Name,
            options.Image,
            options.Tag,
            command: options.Command,
            env: options.Environment,
            volumeMounts: volumeMounts,
            volumes: volumes,
            labels: labels,
            timeoutSeconds: 60
        ).ConfigureAwait(false);

        // Create a headless service for DNS resolution (mimics Docker's container name resolution)
        await CreateHeadlessServiceForPod(namespaceName, options.Name, labels).ConfigureAwait(false);

        return new ContainerInfo
        {
            Id = pod.Metadata.Uid,
            Name = pod.Metadata.Name,
            Image = $"{options.Image}:{options.Tag}",
            State = pod.Status.Phase,
            IpAddress = pod.Status.PodIP,
            Labels = pod.Metadata.Labels
        };
    }

    public async Task<bool> StartContainerAsync(string containerId)
    {
        // Kubernetes pods auto-start after creation
        // This is a no-op, but we verify the pod exists
        try
        {
            var pod = await FindPodByIdOrName(containerId).ConfigureAwait(false);
            return pod != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StopContainerAsync(string containerId, uint waitSeconds = 1)
    {
        try
        {
            var (namespaceName, podName) = await GetPodNamespaceAndName(containerId).ConfigureAwait(false);

            // Delete the service first
            try
            {
                await _client.CoreV1.DeleteNamespacedServiceAsync(podName, namespaceName).ConfigureAwait(false);
            }
            catch
            {
                // Ignore if service doesn't exist
            }

            await _client.RemovePod(namespaceName, podName, gracePeriodSeconds: (int)waitSeconds).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task RemoveContainerAsync(string containerId, bool removeVolumes = true)
    {
        try
        {
            var (namespaceName, podName) = await GetPodNamespaceAndName(containerId).ConfigureAwait(false);

            // Delete the service first
            try
            {
                await _client.CoreV1.DeleteNamespacedServiceAsync(podName, namespaceName).ConfigureAwait(false);
            }
            catch
            {
                // Ignore if service doesn't exist
            }

            await _client.RemovePod(namespaceName, podName, gracePeriodSeconds: 0).ConfigureAwait(false);
        }
        catch
        {
            // Ignore if already deleted
        }
    }

    public async Task<bool> RestartContainerAsync(string containerId, uint waitSeconds = 1)
    {
        try
        {
            var (namespaceName, podName) = await GetPodNamespaceAndName(containerId).ConfigureAwait(false);

            // Get pod details before deletion
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(podName, namespaceName).ConfigureAwait(false);

            // Delete the pod
            await _client.RemovePod(namespaceName, podName, gracePeriodSeconds: (int)waitSeconds).ConfigureAwait(false);

            // Wait for deletion
            await Task.Delay(2000).ConfigureAwait(false);

            // Recreate pod with same spec (remove runtime fields)
            pod.Metadata.ResourceVersion = null;
            pod.Metadata.Uid = null;
            pod.Status = null;

            await _client.CoreV1.CreateNamespacedPodAsync(pod, namespaceName).ConfigureAwait(false);
            await _client.WaitForPodRunning(namespaceName, podName, timeoutSeconds: 60).ConfigureAwait(false);

            return true;
        }
        catch
        {
            return false;
        }
    }

    // Inspection
    public async Task<ContainerInfo> InspectContainerAsync(string containerId)
    {
        var pod = await FindPodByIdOrName(containerId).ConfigureAwait(false);

        if (pod == null)
        {
            throw new Exception($"Pod not found: {containerId}");
        }

        return new ContainerInfo
        {
            Id = pod.Metadata.Uid,
            Name = pod.Metadata.Name,
            Image = pod.Spec.Containers.FirstOrDefault()?.Image ?? "",
            State = pod.Status.Phase,
            IpAddress = pod.Status.PodIP,
            Labels = pod.Metadata.Labels
        };
    }

    public async Task<List<ContainerInfo>> ListContainersAsync()
    {
        var allPods = new List<V1Pod>();

        // List pods from all tracked namespaces
        foreach (var ns in _networkToNamespaceMap.Values.Distinct())
        {
            var pods = await _client.CoreV1.ListNamespacedPodAsync(ns).ConfigureAwait(false);
            allPods.AddRange(pods.Items);
        }

        // Also check default namespace if not already included
        if (!_networkToNamespaceMap.Values.Contains(_defaultNamespace))
        {
            var defaultPods = await _client.CoreV1.ListNamespacedPodAsync(_defaultNamespace).ConfigureAwait(false);
            allPods.AddRange(defaultPods.Items);
        }

        return allPods.Select(pod => new ContainerInfo
        {
            Id = pod.Metadata.Uid,
            Name = pod.Metadata.Name,
            Image = pod.Spec.Containers.FirstOrDefault()?.Image ?? "",
            State = pod.Status.Phase,
            IpAddress = pod.Status.PodIP,
            Labels = pod.Metadata.Labels
        }).ToList();
    }

    // File operations
    public async Task<byte[]> ExtractBinaryFileAsync(string containerId, string filePath)
    {
        var (namespaceName, podName) = await GetPodNamespaceAndName(containerId).ConfigureAwait(false);
        var containerName = podName; // Assuming container name matches pod name

        return await _client.ExecAndReadBinaryFile(namespaceName, podName, containerName, filePath).ConfigureAwait(false);
    }

    public async Task<string> ExtractTextFileAsync(string containerId, string filePath)
    {
        var (namespaceName, podName) = await GetPodNamespaceAndName(containerId).ConfigureAwait(false);
        var containerName = podName; // Assuming container name matches pod name

        return await _client.ExecAndReadTextFile(namespaceName, podName, containerName, filePath).ConfigureAwait(false);
    }

    public async Task PutFileAsync(string containerId, string targetPath, Stream content)
    {
        // Kubernetes doesn't have a direct equivalent to Docker's copy-to-container
        // We'll use kubectl cp equivalent via exec
        var (namespaceName, podName) = await GetPodNamespaceAndName(containerId).ConfigureAwait(false);

        // Read content from stream
        using var memoryStream = new MemoryStream();
        await content.CopyToAsync(memoryStream).ConfigureAwait(false);
        var bytes = memoryStream.ToArray();
        var base64Content = Convert.ToBase64String(bytes);

        // Write file using base64 decode in pod
        var command = new[] { "sh", "-c", $"echo '{base64Content}' | base64 -d > {targetPath}" };

        // Execute command (we'd need to extend KubernetesHelper for this, or inline it here)
        // For now, throw not implemented
        throw new NotImplementedException("PutFileAsync requires extending KubernetesHelper with exec command support");
    }

    // Image management
    public async Task PullImageAsync(string image, string tag)
    {
        // Kubernetes automatically pulls images when creating pods
        // We can verify the image by creating a temporary pod
        // For now, this is a no-op
        await Task.CompletedTask;
    }

    public async Task<bool> ImageExistsAsync(string image, string tag)
    {
        // Kubernetes doesn't expose image listings directly
        // We'll assume the image exists and let pod creation fail if it doesn't
        await Task.CompletedTask;
        return true;
    }

    public void Dispose()
    {
        _client?.Dispose();
    }

    // Helper methods
    private async Task<V1Pod?> FindPodByIdOrName(string idOrName)
    {
        // Try to find by name in tracked namespaces first
        foreach (var ns in _networkToNamespaceMap.Values.Distinct().Append(_defaultNamespace))
        {
            try
            {
                var pod = await _client.CoreV1.ReadNamespacedPodAsync(idOrName, ns).ConfigureAwait(false);
                if (pod != null) return pod;
            }
            catch
            {
                // Continue searching
            }
        }

        // Try to find by UID across all namespaces
        foreach (var ns in _networkToNamespaceMap.Values.Distinct().Append(_defaultNamespace))
        {
            try
            {
                var pods = await _client.CoreV1.ListNamespacedPodAsync(ns).ConfigureAwait(false);
                var pod = pods.Items.FirstOrDefault(p => p.Metadata.Uid == idOrName);
                if (pod != null) return pod;
            }
            catch
            {
                // Continue searching
            }
        }

        return null;
    }

    private async Task<(string namespaceName, string podName)> GetPodNamespaceAndName(string idOrName)
    {
        var pod = await FindPodByIdOrName(idOrName).ConfigureAwait(false);

        if (pod == null)
        {
            throw new Exception($"Pod not found: {idOrName}");
        }

        return (pod.Metadata.NamespaceProperty, pod.Metadata.Name);
    }

    /// <summary>
    /// Creates a headless service for a pod to enable DNS resolution by pod name.
    /// This mimics Docker's behavior where container names are automatically DNS-resolvable.
    /// </summary>
    private async Task CreateHeadlessServiceForPod(string namespaceName, string podName, Dictionary<string, string> labels)
    {
        try
        {
            var service = new V1Service
            {
                Metadata = new V1ObjectMeta
                {
                    Name = podName,
                    Labels = labels
                },
                Spec = new V1ServiceSpec
                {
                    ClusterIP = "None", // Headless service
                    Selector = labels,
                    Ports = new List<V1ServicePort>
                    {
                        // Add a dummy port to satisfy Kubernetes requirements
                        new V1ServicePort
                        {
                            Name = "default",
                            Port = 1,
                            TargetPort = 1,
                            Protocol = "TCP"
                        }
                    }
                }
            };

            await _client.CoreV1.CreateNamespacedServiceAsync(service, namespaceName).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ignore if service already exists or creation fails
        }
    }
}
