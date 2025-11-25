using Docker.DotNet;
using Docker.DotNet.Models;
using SharpCompress.Readers;
using System.Text;

namespace LNUnit.Setup;

/// <summary>
/// Docker implementation of IContainerOrchestrator
/// </summary>
public class DockerOrchestrator : IContainerOrchestrator
{
    private readonly DockerClient _client;

    /// <summary>
    /// Exposes the underlying Docker client for Docker-specific operations
    /// </summary>
    public DockerClient Client => _client;

    public DockerOrchestrator()
    {
        _client = new DockerClientConfiguration().CreateClient();
    }

    public async Task<string> CreateNetworkAsync(string networkName)
    {
        var response = await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = networkName,
            Driver = "bridge",
            CheckDuplicate = true
        }).ConfigureAwait(false);
        return response.ID;
    }

    public async Task DeleteNetworkAsync(string networkId)
    {
        try
        {
            await _client.Networks.DeleteNetworkAsync(networkId).ConfigureAwait(false);
        }
        catch
        {
            // Ignore if already deleted
        }
    }

    public async Task<ContainerInfo> CreateContainerAsync(ContainerCreateOptions options)
    {
        var createParams = new CreateContainerParameters
        {
            Image = $"{options.Image}:{options.Tag}",
            Name = options.Name,
            Hostname = options.Name,
            Cmd = options.Command,
            HostConfig = new HostConfig
            {
                NetworkMode = options.NetworkId,
                Binds = options.Binds,
                Links = options.Links
            },
            Env = options.Environment?.Select(kvp => $"{kvp.Key}={kvp.Value}").ToList(),
            Labels = options.Labels
        };

        if (options.ExposedPorts?.Any() == true)
        {
            createParams.ExposedPorts = options.ExposedPorts.ToDictionary(
                p => $"{p}/tcp",
                _ => new EmptyStruct()
            );
        }

        var response = await _client.Containers.CreateContainerAsync(createParams).ConfigureAwait(false);

        return new ContainerInfo
        {
            Id = response.ID,
            Name = options.Name,
            Image = $"{options.Image}:{options.Tag}",
            State = "Created"
        };
    }

    public async Task<bool> StartContainerAsync(string containerId)
    {
        return await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters()).ConfigureAwait(false);
    }

    public async Task<bool> StopContainerAsync(string containerId, uint waitSeconds = 1)
    {
        try
        {
            return await _client.Containers.StopContainerAsync(containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = waitSeconds }).ConfigureAwait(false);
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
            await _client.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = removeVolumes }).ConfigureAwait(false);
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
            await _client.Containers.RestartContainerAsync(containerId,
                new ContainerRestartParameters { WaitBeforeKillSeconds = waitSeconds }).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ContainerInfo> InspectContainerAsync(string containerId)
    {
        var inspectionResponse = await _client.Containers.InspectContainerAsync(containerId).ConfigureAwait(false);

        return new ContainerInfo
        {
            Id = inspectionResponse.ID,
            Name = inspectionResponse.Name.TrimStart('/'),
            Image = inspectionResponse.Config.Image,
            State = inspectionResponse.State.Running ? "Running" : "Stopped",
            IpAddress = inspectionResponse.NetworkSettings.Networks.FirstOrDefault().Value?.IPAddress,
            Labels = inspectionResponse.Config.Labels
        };
    }

    public async Task<List<ContainerInfo>> ListContainersAsync()
    {
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters()).ConfigureAwait(false);

        return containers.Select(c => new ContainerInfo
        {
            Id = c.ID,
            Name = c.Names.FirstOrDefault()?.TrimStart('/') ?? c.ID,
            Image = c.Image,
            State = c.State,
            IpAddress = c.NetworkSettings?.Networks?.FirstOrDefault().Value?.IPAddress,
            Labels = c.Labels
        }).ToList();
    }

    public async Task<byte[]> ExtractBinaryFileAsync(string containerId, string filePath)
    {
        var tarResponse = await _client.Containers.GetArchiveFromContainerAsync(
            containerId,
            new GetArchiveFromContainerParameters { Path = filePath },
            false
        ).ConfigureAwait(false);

        return GetBytesFromTar(tarResponse);
    }

    public async Task<string> ExtractTextFileAsync(string containerId, string filePath)
    {
        var tarResponse = await _client.Containers.GetArchiveFromContainerAsync(
            containerId,
            new GetArchiveFromContainerParameters { Path = filePath },
            false
        ).ConfigureAwait(false);

        return GetStringFromTar(tarResponse);
    }

    public async Task PutFileAsync(string containerId, string targetPath, Stream content)
    {
        await _client.Containers.ExtractArchiveToContainerAsync(containerId,
            new ContainerPathStatParameters { Path = targetPath },
            content).ConfigureAwait(false);
    }

    public async Task PullImageAsync(string image, string tag)
    {
        await _client.PullImageAndWaitForCompleted(image, tag).ConfigureAwait(false);
    }

    public async Task<bool> ImageExistsAsync(string image, string tag)
    {
        try
        {
            var images = await _client.Images.ListImagesAsync(new ImagesListParameters
            {
                All = true
            }).ConfigureAwait(false);

            var searchFor = $"{image}:{tag}";
            return images.Any(i => i.RepoTags?.Contains(searchFor) == true);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
    }

    // Helper methods from existing LNUnitBuilder patterns
    private static string GetStringFromTar(GetArchiveFromContainerResponse tarResponse)
    {
        using var stream = tarResponse.Stream;
        var reader = ReaderFactory.Open(stream, new ReaderOptions { LookForHeader = true });
        while (reader.MoveToNextEntry())
        {
            if (!reader.Entry.IsDirectory)
            {
                using var entryStream = reader.OpenEntryStream();
                using var streamReader = new StreamReader(entryStream);
                return streamReader.ReadToEnd();
            }
        }
        return string.Empty;
    }

    private static byte[] GetBytesFromTar(GetArchiveFromContainerResponse tarResponse)
    {
        using var memStream = new MemoryStream();
        tarResponse.Stream.CopyTo(memStream);
        memStream.Position = 0;

        var reader = ReaderFactory.Open(memStream, new ReaderOptions { LookForHeader = true });
        while (reader.MoveToNextEntry())
        {
            if (!reader.Entry.IsDirectory)
            {
                using var entryStream = reader.OpenEntryStream();
                using var resultStream = new MemoryStream();
                entryStream.CopyTo(resultStream);
                return resultStream.ToArray();
            }
        }
        return Array.Empty<byte>();
    }
}
