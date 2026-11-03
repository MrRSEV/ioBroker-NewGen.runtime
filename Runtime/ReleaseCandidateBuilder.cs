using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Runtime
{
    /// <summary>
    /// Baut aus einem bereits veröffentlichten .NET-Ausgabeverzeichnis ein
    /// reproduzierbares Release-Candidate-Verzeichnis inklusive Manifest.
    /// </summary>
    public sealed class ReleaseCandidateBuilder
    {
        private const string SignatureAlgorithm = "RSA-SHA256-PKCS1";

        private static readonly JsonSerializerOptions ManifestJsonOptions = new()
        {
            WriteIndented = true
        };

        public async Task<string> BuildAsync(
            string publishDirectory,
            string outputDirectory,
            ReleaseCandidateManifest? manifest = null,
            CancellationToken cancellationToken = default,
            IEnumerable<string>? adapterAssemblyDirectories = null,
            ReleaseCandidateSigningOptions? signingOptions = null)
        {
            if (string.IsNullOrWhiteSpace(publishDirectory) || !Directory.Exists(publishDirectory))
            {
                throw new DirectoryNotFoundException($"Publish-Verzeichnis nicht gefunden: '{publishDirectory}'.");
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("Ausgabeverzeichnis darf nicht leer sein.", nameof(outputDirectory));
            }

            var source = Path.GetFullPath(publishDirectory);
            var target = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(target);

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(target, relative);
                var destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                File.Copy(file, destination, overwrite: true);
            }

            if (adapterAssemblyDirectories is not null)
            {
                var adapterTarget = Path.Combine(target, "Adapters", "custom");
                Directory.CreateDirectory(adapterTarget);
                foreach (var adapterDirectory in adapterAssemblyDirectories)
                {
                    if (string.IsNullOrWhiteSpace(adapterDirectory) || !Directory.Exists(adapterDirectory))
                    {
                        continue;
                    }

                    foreach (var adapter in Directory.EnumerateFiles(adapterDirectory, "*.dll", SearchOption.TopDirectoryOnly))
                    {
                        File.Copy(adapter, Path.Combine(adapterTarget, Path.GetFileName(adapter)), overwrite: true);
                    }
                }
            }

            var releaseManifest = manifest ?? new ReleaseCandidateManifest();
            foreach (var requiredArtifact in releaseManifest.RequiredArtifacts)
            {
                if (!File.Exists(Path.Combine(target, requiredArtifact)))
                {
                    throw new InvalidDataException($"Pflichtartefakt fehlt im Release: '{requiredArtifact}'.");
                }
            }

            releaseManifest.AdapterAssemblies = Directory.Exists(Path.Combine(target, "Adapters", "custom"))
                ? Directory.EnumerateFiles(Path.Combine(target, "Adapters", "custom"), "*.dll", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();
            releaseManifest.ArtifactSha256 = await ComputeHashesAsync(target, cancellationToken);
            ApplySignature(releaseManifest, signingOptions);
            var manifestPath = Path.Combine(target, "release-candidate.json");
            await using var stream = File.Create(manifestPath);
            await JsonSerializer.SerializeAsync(stream, releaseManifest, ManifestJsonOptions, cancellationToken);
            return target;
        }

        public async Task<bool> VerifyAsync(
            string releaseDirectory,
            CancellationToken cancellationToken = default,
            string? trustedPublicKeyPem = null,
            bool requireSignature = false)
        {
            if (string.IsNullOrWhiteSpace(releaseDirectory) || !Directory.Exists(releaseDirectory))
            {
                return false;
            }

            var root = Path.GetFullPath(releaseDirectory);
            var manifestPath = ResolveContainedPath(root, "release-candidate.json");
            if (manifestPath is null || !File.Exists(manifestPath))
            {
                return false;
            }

            try
            {
                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<ReleaseCandidateManifest>(stream, cancellationToken: cancellationToken);
                if (manifest is null)
                {
                    return false;
                }

                foreach (var requiredArtifact in manifest.RequiredArtifacts)
                {
                    var artifactPath = ResolveContainedPath(root, requiredArtifact);
                    if (artifactPath is null || !File.Exists(artifactPath))
                    {
                        return false;
                    }
                }

                foreach (var entry in manifest.ArtifactSha256)
                {
                    var file = ResolveContainedPath(root, entry.Key);
                    if (file is null || !File.Exists(file) ||
                        !string.Equals(entry.Value, await ComputeSha256Async(file, cancellationToken), StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return VerifySignature(manifest, trustedPublicKeyPem, requireSignature);
            }
            catch (JsonException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static async Task<Dictionary<string, string>> ComputeHashesAsync(string root, CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .Where(file => !string.Equals(Path.GetFileName(file), "release-candidate.json", StringComparison.OrdinalIgnoreCase)))
            {
                result[Path.GetRelativePath(root, file)] = await ComputeSha256Async(file, cancellationToken);
            }

            return result;
        }

        private static void ApplySignature(
            ReleaseCandidateManifest manifest,
            ReleaseCandidateSigningOptions? signingOptions)
        {
            manifest.SignatureAlgorithm = null;
            manifest.SignatureBase64 = null;
            manifest.SignerPublicKeyPem = null;

            if (signingOptions is null || string.IsNullOrWhiteSpace(signingOptions.PrivateKeyPem))
            {
                if (signingOptions?.RequireSignature == true)
                {
                    throw new InvalidOperationException("Für einen signierten Release-Candidate muss ein privater RSA-Schlüssel angegeben werden.");
                }

                return;
            }

            using var rsa = RSA.Create();
            rsa.ImportFromPem(signingOptions.PrivateKeyPem);
            var publicKeyPem = string.IsNullOrWhiteSpace(signingOptions.PublicKeyPem)
                ? rsa.ExportSubjectPublicKeyInfoPem()
                : signingOptions.PublicKeyPem;
            var canonicalManifest = SerializeCanonicalManifest(manifest);
            var signature = rsa.SignData(
                canonicalManifest,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            manifest.SignatureAlgorithm = SignatureAlgorithm;
            manifest.SignatureBase64 = Convert.ToBase64String(signature);
            manifest.SignerPublicKeyPem = publicKeyPem;
        }

        private static bool VerifySignature(
            ReleaseCandidateManifest manifest,
            string? trustedPublicKeyPem,
            bool requireSignature)
        {
            var hasSignature = !string.IsNullOrWhiteSpace(manifest.SignatureAlgorithm) ||
                               !string.IsNullOrWhiteSpace(manifest.SignatureBase64) ||
                               !string.IsNullOrWhiteSpace(manifest.SignerPublicKeyPem);
            if (!hasSignature)
            {
                return !requireSignature;
            }

            if (!string.Equals(manifest.SignatureAlgorithm, SignatureAlgorithm, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(manifest.SignatureBase64) ||
                string.IsNullOrWhiteSpace(trustedPublicKeyPem) && requireSignature)
            {
                return false;
            }

            var publicKeyPem = string.IsNullOrWhiteSpace(trustedPublicKeyPem)
                ? manifest.SignerPublicKeyPem
                : trustedPublicKeyPem;
            if (string.IsNullOrWhiteSpace(publicKeyPem))
            {
                return false;
            }

            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(manifest.SignatureBase64);
            }
            catch (FormatException)
            {
                return false;
            }

            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(publicKeyPem);
                var originalSignature = manifest.SignatureBase64;
                var originalAlgorithm = manifest.SignatureAlgorithm;
                var originalPublicKey = manifest.SignerPublicKeyPem;
                manifest.SignatureBase64 = null;
                manifest.SignatureAlgorithm = null;
                manifest.SignerPublicKeyPem = null;
                var canonicalManifest = SerializeCanonicalManifest(manifest);
                manifest.SignatureBase64 = originalSignature;
                manifest.SignatureAlgorithm = originalAlgorithm;
                manifest.SignerPublicKeyPem = originalPublicKey;
                return rsa.VerifyData(
                    canonicalManifest,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static byte[] SerializeCanonicalManifest(ReleaseCandidateManifest manifest)
        {
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, ManifestJsonOptions));
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            await using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(await sha256.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
        }

        private static string? ResolveContainedPath(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
    }
}
