namespace LNUnit.LND;

public class LNDSettings
{
    /// <summary>
    ///     LND Host Grpc Endpoint (e.g. https://localhost:10009)
    /// </summary>
    public string GrpcEndpoint { get; set; }

    /// <summary>
    ///     TLS Cert as Base64 string, if provided will be perfered source
    /// </summary>
    public string? TLSCertBase64 { get; set; }

    /// <summary>
    ///     Macaroon as Base64 string, if provided will be perfered source
    /// </summary>
    public string? MacaroonBase64 { get; set; }
}

public class LoopSettings : LNDSettings;