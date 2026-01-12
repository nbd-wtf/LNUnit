namespace LNUnit.Setup;

/// <summary>
/// Unified options for creating containers/pods across Docker and Kubernetes
/// </summary>
public class ContainerCreateOptions
{
    public required string Name { get; set; }
    public required string Image { get; set; }
    public string Tag { get; set; } = "latest";
    public string? NetworkId { get; set; }
    public List<string>? Command { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
    public List<string>? Binds { get; set; } // Volume mounts (Docker style: "/host:/container")
    public List<string>? Links { get; set; } // Container links (Docker) or Services (K8s)
    public Dictionary<string, string>? Labels { get; set; }
    public List<int>? ExposedPorts { get; set; }
}
