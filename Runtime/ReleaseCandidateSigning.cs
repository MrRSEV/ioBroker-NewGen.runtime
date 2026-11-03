namespace ioBroker_NewGen.Runtime;

/// <summary>
/// Signaturoptionen für Release-Candidate-Manifeste. Der private Schlüssel
/// bleibt ausschließlich im Build-Prozess; nur der öffentliche Schlüssel wird
/// im Manifest als Hinweis für die nachgelagerte Verifikation abgelegt.
/// </summary>
public sealed class ReleaseCandidateSigningOptions
{
    public string? PrivateKeyPem { get; init; }

    public string? PublicKeyPem { get; init; }

    public bool RequireSignature { get; init; }
}
