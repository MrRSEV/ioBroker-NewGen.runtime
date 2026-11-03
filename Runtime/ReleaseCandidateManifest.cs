using System;
using System.Collections.Generic;

namespace ioBroker_NewGen.Runtime
{
    public sealed class ReleaseCandidateManifest
    {
        public string Product { get; init; } = "ioBroker-NewGen";

        public string Version { get; init; } = "beta0.2.0";

        public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;

        public string UtilitiesVersion { get; init; } = "3.0.0.0";

        public string TargetFramework { get; init; } = "net9.0";

        public IReadOnlyCollection<string> RequiredArtifacts { get; init; } = new[]
        {
            "ioBroker-NewGen.dll",
            "RSEV.Utilities.dll",
            "config.toml",
            "snapshot.jsonl"
        };

        public IReadOnlyCollection<string> AdapterAssemblies { get; set; } = Array.Empty<string>();

        public IReadOnlyDictionary<string, string> ArtifactSha256 { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string? SignatureAlgorithm { get; set; }

        public string? SignatureBase64 { get; set; }

        public string? SignerPublicKeyPem { get; set; }
    }
}
