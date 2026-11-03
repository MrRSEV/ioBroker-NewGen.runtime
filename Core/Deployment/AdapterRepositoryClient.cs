using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Core.Deployment
{
    public sealed class AdapterRepositorySecurityOptions
    {
        public bool RequireSignature { get; init; }

        public string? TrustedPublicKeyPem { get; init; }
    }

    public sealed class AdapterRepositoryIndex
    {
        public IReadOnlyCollection<AdapterRepositoryPackage> Packages { get; init; } = Array.Empty<AdapterRepositoryPackage>();
    }

    public sealed class AdapterRepositoryPackage
    {
        public string Name { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        public string Url { get; init; } = string.Empty;

        public string Sha256 { get; init; } = string.Empty;

        public string? SignatureBase64 { get; init; }

        public string? PublicKeyPem { get; init; }
    }

    /// <summary>
    /// Lädt Adapter-ZIP-Pakete aus einem HTTPS-Repository, prüft Hash und
    /// optional eine RSA-Signatur und übergibt sie an den lokalen Paketinstaller.
    /// </summary>
    public sealed class AdapterRepositoryClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
        private readonly HttpClient _httpClient;
        private readonly AdapterPackageInstaller _packageInstaller;
        private readonly AdapterRepositorySecurityOptions _securityOptions;

        public AdapterRepositoryClient(
            AdapterPackageInstaller packageInstaller,
            HttpClient? httpClient = null,
            AdapterRepositorySecurityOptions? securityOptions = null)
        {
            _packageInstaller = packageInstaller ?? throw new ArgumentNullException(nameof(packageInstaller));
            _httpClient = httpClient ?? new HttpClient();
            _securityOptions = securityOptions ?? new AdapterRepositorySecurityOptions();
            if (_securityOptions.RequireSignature && string.IsNullOrWhiteSpace(_securityOptions.TrustedPublicKeyPem))
            {
                throw new ArgumentException(
                    "Für verpflichtende Paketsignaturen muss ein vertrauenswürdiger Public Key konfiguriert sein.",
                    nameof(securityOptions));
            }
        }

        public async Task<AdapterPackageManifest> DownloadAndInstallAsync(
            Uri indexUri,
            string packageName,
            string version,
            string stagingDirectory,
            CancellationToken cancellationToken = default)
        {
            if (indexUri is null || !indexUri.IsAbsoluteUri || indexUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException("Repository-URL muss absolut sein.", nameof(indexUri));
            }

            var indexJson = await _httpClient.GetStringAsync(indexUri, cancellationToken);
            var index = JsonSerializer.Deserialize<AdapterRepositoryIndex>(indexJson, JsonOptions)
                ?? throw new InvalidDataException("Repository-Index ist ungültig.");
            var package = index.Packages.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, packageName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Version, version, StringComparison.OrdinalIgnoreCase));
            if (package is null)
            {
                throw new KeyNotFoundException($"Adapterpaket '{packageName}@{version}' wurde nicht gefunden.");
            }

            if (!Uri.TryCreate(indexUri, package.Url, out var packageUri) || packageUri is null)
            {
                throw new InvalidDataException($"Ungültige Paket-URL: '{package.Url}'.");
            }

            if (packageUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("Adapter-Repository darf nur HTTPS-Pakete liefern.");
            }

            var archive = await _httpClient.GetByteArrayAsync(packageUri, cancellationToken);
            var actualHash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            if (!string.Equals(NormalizeHash(package.Sha256), actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SHA-256-Prüfung des Remote-Adapterpakets fehlgeschlagen.");
            }

            VerifySignature(package, archive);

            var stagingRoot = Path.GetFullPath(stagingDirectory);
            Directory.CreateDirectory(stagingRoot);
            // Name und Version stammen aus dem Repository/API und dürfen nie
            // Teil eines Dateipfades werden (Path-Traversal-Schutz).
            var packageDirectory = Path.Combine(stagingRoot, $"package-{Guid.NewGuid():N}");
            Directory.CreateDirectory(packageDirectory);
            try
            {
                using var archiveStream = new MemoryStream(archive, writable: false);
                ZipFile.ExtractToDirectory(archiveStream, packageDirectory);
                return await _packageInstaller.InstallAsync(packageDirectory, cancellationToken);
            }
            finally
            {
                if (Directory.Exists(packageDirectory))
                {
                    Directory.Delete(packageDirectory, recursive: true);
                }
            }
        }

        private void VerifySignature(AdapterRepositoryPackage package, byte[] archive)
        {
            if (string.IsNullOrWhiteSpace(package.SignatureBase64))
            {
                if (_securityOptions.RequireSignature)
                {
                    throw new InvalidDataException("Remote-Adapterpaket ist nicht signiert.");
                }

                return;
            }

            var publicKeyPem = _securityOptions.TrustedPublicKeyPem ?? package.PublicKeyPem;
            if (string.IsNullOrWhiteSpace(publicKeyPem))
            {
                throw new InvalidDataException("Remote-Paket enthält eine Signatur, aber keinen Public Key.");
            }

            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(package.SignatureBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("RSA-Signatur des Remote-Adapterpakets ist kein gültiges Base64.", exception);
            }

            if (!rsa.VerifyData(archive, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                throw new InvalidDataException("RSA-Signatur des Remote-Adapterpakets ist ungültig.");
            }
        }

        private static string NormalizeHash(string value)
        {
            return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }
    }
}
