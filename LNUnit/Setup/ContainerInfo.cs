namespace LNUnit.Setup;

/// <summary>
/// Unified model for container/pod information across Docker and Kubernetes
/// </summary>
public class ContainerInfo
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Image { get; set; }
    public required string State { get; set; } // Running, Stopped, Pending, etc.
    public string? IpAddress { get; set; }
    public IDictionary<string, string>? Labels { get; set; }
    public bool IsRunning => State == "Running";
}
