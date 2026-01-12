using System.Text;
using k8s;
using k8s.Models;

namespace LNUnit.Setup;

public static class KubernetesHelper
{
    private static readonly Random Random = new();

    /// <summary>
    /// Create a pod with specified options and wait for it to be running
    /// </summary>
    public static async Task<V1Pod> CreatePodAndWaitForRunning(
        this IKubernetes client,
        string namespaceName,
        string podName,
        string image,
        string tag = "latest",
        List<string>? command = null,
        Dictionary<string, string>? env = null,
        List<V1VolumeMount>? volumeMounts = null,
        List<V1Volume>? volumes = null,
        Dictionary<string, string>? labels = null,
        int timeoutSeconds = 60)
    {
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                Labels = labels ?? new Dictionary<string, string>()
            },
            Spec = new V1PodSpec
            {
                Containers = new List<V1Container>
                {
                    new V1Container
                    {
                        Name = podName,
                        Image = $"{image}:{tag}",
                        Command = command,
                        Env = env?.Select(kvp => new V1EnvVar{ Name = kvp.Key, Value = kvp.Value}).ToList(),
                        VolumeMounts = volumeMounts
                    }
                },
                Volumes = volumes,
                RestartPolicy = "Always"
            }
        };

        await client.CoreV1.CreateNamespacedPodAsync(pod, namespaceName).ConfigureAwait(false);
        return await client.WaitForPodRunning(namespaceName, podName, timeoutSeconds).ConfigureAwait(false);
    }

    /// <summary>
    /// Wait for a pod to reach Running state with all containers ready
    /// </summary>
    public static async Task<V1Pod> WaitForPodRunning(
        this IKubernetes client,
        string namespaceName,
        string podName,
        int timeoutSeconds = 60)
    {
        var timeout = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < timeout)
        {
            var pod = await client.CoreV1.ReadNamespacedPodAsync(podName, namespaceName).ConfigureAwait(false);

            if (pod.Status?.Phase == "Running" && !string.IsNullOrEmpty(pod.Status?.PodIP))
            {
                // Also check container ready status
                if (pod.Status.ContainerStatuses?.All(c => c.Ready) == true)
                {
                    return pod;
                }
            }

            if (pod.Status?.Phase == "Failed" || pod.Status?.Phase == "Unknown")
            {
                throw new Exception($"Pod {podName} failed: {pod.Status.Reason} - {pod.Status.Message}");
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException($"Pod {podName} did not reach Running state within {timeoutSeconds}s");
    }

    /// <summary>
    /// Extract a text file from a pod using cat command via exec API
    /// </summary>
    public static async Task<string> ExecAndReadTextFile(
        this IKubernetes client,
        string namespaceName,
        string podName,
        string containerName,
        string filePath)
    {
        var command = new[] { "cat", filePath };
        return await client.ExecInPod(namespaceName, podName, containerName, command).ConfigureAwait(false);
    }

    /// <summary>
    /// Extract a binary file from a pod using base64 encoding via exec API
    /// </summary>
    public static async Task<byte[]> ExecAndReadBinaryFile(
        this IKubernetes client,
        string namespaceName,
        string podName,
        string containerName,
        string filePath)
    {
        var command = new[] { "sh", "-c", $"base64 -w 0 {filePath}" };
        var base64Result = await client.ExecInPod(namespaceName, podName, containerName, command).ConfigureAwait(false);
        return Convert.FromBase64String(base64Result.Trim());
    }

    /// <summary>
    /// Execute a command in a pod and return the stdout
    /// </summary>
    private static async Task<string> ExecInPod(
        this IKubernetes client,
        string namespaceName,
        string podName,
        string containerName,
        string[] command)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();

        var handler = new ExecAsyncCallback(async (stdIn, stdOut, stdError) =>
        {
            var buffer = new byte[4096];

            // Read stdout
            var stdOutTask = Task.Run(async () =>
            {
                while (true)
                {
                    var bytesRead = await stdOut.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (bytesRead == 0) break;
                    output.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                }
            });

            // Read stderr
            var stdErrTask = Task.Run(async () =>
            {
                while (true)
                {
                    var bytesRead = await stdError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (bytesRead == 0) break;
                    error.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                }
            });

            await Task.WhenAll(stdOutTask, stdErrTask).ConfigureAwait(false);
        });

        await client.NamespacedPodExecAsync(
            podName,
            namespaceName,
            containerName,
            command,
            false,
            handler,
            CancellationToken.None).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(error.ToString()))
        {
            throw new Exception($"Error executing command in pod: {error}");
        }

        return output.ToString();
    }

    /// <summary>
    /// Create a test namespace with random suffix
    /// </summary>
    public static async Task<string> CreateTestNamespace(
        this IKubernetes client,
        string baseName = "unit-test")
    {
        var randomString = GetRandomHexString();
        var namespaceName = $"{baseName}-{randomString}";

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

        await client.CoreV1.CreateNamespaceAsync(ns).ConfigureAwait(false);
        return namespaceName;
    }

    /// <summary>
    /// Delete a namespace
    /// </summary>
    public static async Task DeleteNamespace(
        this IKubernetes client,
        string namespaceName,
        int gracePeriodSeconds = 0)
    {
        try
        {
            await client.CoreV1.DeleteNamespaceAsync(
                namespaceName,
                gracePeriodSeconds: gracePeriodSeconds).ConfigureAwait(false);

            // Optionally wait for deletion (can be slow)
            // await WaitForNamespaceDeletion(client, namespaceName, timeoutSeconds: 60);
        }
        catch (Exception)
        {
            // Ignore if already deleted
        }
    }

    /// <summary>
    /// Create a ClusterIP service for a pod
    /// </summary>
    public static async Task<V1Service> CreateService(
        this IKubernetes client,
        string namespaceName,
        string serviceName,
        string targetPodLabel,
        int port,
        int targetPort,
        string protocol = "TCP")
    {
        var service = new V1Service
        {
            Metadata = new V1ObjectMeta
            {
                Name = serviceName
            },
            Spec = new V1ServiceSpec
            {
                Selector = new Dictionary<string, string>
                {
                    { "app", targetPodLabel }
                },
                Ports = new List<V1ServicePort>
                {
                    new V1ServicePort
                    {
                        Port = port,
                        TargetPort = targetPort,
                        Protocol = protocol
                    }
                },
                Type = "ClusterIP"
            }
        };

        return await client.CoreV1.CreateNamespacedServiceAsync(service, namespaceName).ConfigureAwait(false);
    }

    /// <summary>
    /// Create a PersistentVolumeClaim
    /// </summary>
    public static async Task<V1PersistentVolumeClaim> CreatePVC(
        this IKubernetes client,
        string namespaceName,
        string pvcName,
        string storageSize = "1Gi")
    {
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
                        { "storage", new ResourceQuantity(storageSize) }
                    }
                }
            }
        };

        return await client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(pvc, namespaceName).ConfigureAwait(false);
    }

    /// <summary>
    /// Delete a pod
    /// </summary>
    public static async Task RemovePod(
        this IKubernetes client,
        string namespaceName,
        string podName,
        int gracePeriodSeconds = 0)
    {
        try
        {
            await client.CoreV1.DeleteNamespacedPodAsync(
                podName,
                namespaceName,
                gracePeriodSeconds: gracePeriodSeconds).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ignore if already deleted
        }
    }

    /// <summary>
    /// Get random hex string for naming (lowercase for K8s compatibility)
    /// </summary>
    private static string GetRandomHexString(int size = 8)
    {
        var b = new byte[size];
        Random.NextBytes(b);
        return Convert.ToHexString(b).ToLower();
    }

    /// <summary>
    /// Wait for a file to exist in a pod (poll with retry)
    /// </summary>
    public static async Task<string> WaitForFileAndRead(
        this IKubernetes client,
        string namespaceName,
        string podName,
        string containerName,
        string filePath,
        int timeoutSeconds = 30)
    {
        var timeout = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < timeout)
        {
            try
            {
                return await client.ExecAndReadTextFile(namespaceName, podName, containerName, filePath).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // File doesn't exist yet, wait and retry
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException($"File {filePath} did not appear in pod {podName} within {timeoutSeconds}s");
    }

    /// <summary>
    /// Wait for a binary file to exist in a pod (poll with retry)
    /// </summary>
    public static async Task<byte[]> WaitForBinaryFileAndRead(
        this IKubernetes client,
        string namespaceName,
        string podName,
        string containerName,
        string filePath,
        int timeoutSeconds = 30)
    {
        var timeout = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < timeout)
        {
            try
            {
                return await client.ExecAndReadBinaryFile(namespaceName, podName, containerName, filePath).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // File doesn't exist yet, wait and retry
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException($"File {filePath} did not appear in pod {podName} within {timeoutSeconds}s");
    }
}
