namespace LNUnit.Setup;

/// <summary>
/// Abstract interface for container orchestration that works with both Docker and Kubernetes
/// </summary>
public interface IContainerOrchestrator : IDisposable
{
    // Network/Namespace management
    Task<string> CreateNetworkAsync(string networkName);
    Task DeleteNetworkAsync(string networkId);

    // Container/Pod management
    Task<ContainerInfo> CreateContainerAsync(ContainerCreateOptions options);
    Task<bool> StartContainerAsync(string containerId);
    Task<bool> StopContainerAsync(string containerId, uint waitSeconds = 1);
    Task RemoveContainerAsync(string containerId, bool removeVolumes = true);
    Task<bool> RestartContainerAsync(string containerId, uint waitSeconds = 1);

    // Inspection
    Task<ContainerInfo> InspectContainerAsync(string containerId);
    Task<List<ContainerInfo>> ListContainersAsync();

    // File operations
    Task<byte[]> ExtractBinaryFileAsync(string containerId, string filePath);
    Task<string> ExtractTextFileAsync(string containerId, string filePath);
    Task PutFileAsync(string containerId, string targetPath, Stream content);

    // Image management
    Task PullImageAsync(string image, string tag);
    Task<bool> ImageExistsAsync(string image, string tag);
}
