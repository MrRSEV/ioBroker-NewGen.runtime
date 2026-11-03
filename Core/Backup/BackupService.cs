using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Core.Backup
{
    /// <summary>
    /// Erstellt nachvollziehbare, nicht-destruktive Backups aus Snapshot,
    /// Konfiguration und installierten Adaptern.
    /// </summary>
    public sealed class BackupService
    {
        private readonly string _applicationDirectory;

        public BackupService(string applicationDirectory)
        {
            if (string.IsNullOrWhiteSpace(applicationDirectory))
            {
                throw new ArgumentException("Anwendungsverzeichnis darf nicht leer sein.", nameof(applicationDirectory));
            }

            _applicationDirectory = Path.GetFullPath(applicationDirectory);
        }

        public async Task<string> CreateBackupAsync(
            string backupRoot,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(backupRoot))
            {
                throw new ArgumentException("Backup-Verzeichnis darf nicht leer sein.", nameof(backupRoot));
            }

            var root = Path.GetFullPath(backupRoot);
            Directory.CreateDirectory(root);

            var backupDirectory = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
            Directory.CreateDirectory(backupDirectory);

            var sourceFiles = EnumerateSources().ToList();
            var manifest = new BackupManifest();
            foreach (var sourceFile in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(_applicationDirectory, sourceFile);
                var targetFile = Path.Combine(backupDirectory, relativePath);
                var targetDirectory = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                File.Copy(sourceFile, targetFile, overwrite: false);
                var info = new FileInfo(targetFile);
                manifest.Files.Add(new BackupFileEntry
                {
                    RelativePath = relativePath,
                    Length = info.Length,
                    Sha256 = await ComputeSha256Async(targetFile, cancellationToken)
                });
            }

            var manifestPath = Path.Combine(backupDirectory, "manifest.json");
            await using var stream = File.Create(manifestPath);
            await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: cancellationToken);
            return backupDirectory;
        }

        public async Task<bool> VerifyBackupAsync(string backupDirectory, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(backupDirectory) || !Directory.Exists(backupDirectory))
            {
                return false;
            }

            var root = Path.GetFullPath(backupDirectory);
            var manifestPath = ResolveContainedPath(root, "manifest.json");
            if (manifestPath is null || !File.Exists(manifestPath))
            {
                return false;
            }

            try
            {
                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, cancellationToken: cancellationToken);
                if (manifest is null)
                {
                    return false;
                }

                foreach (var entry in manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = ResolveContainedPath(root, entry.RelativePath);
                    if (file is null || !File.Exists(file))
                    {
                        return false;
                    }

                    var info = new FileInfo(file);
                    if (info.Length != entry.Length ||
                        !string.Equals(await ComputeSha256Async(file, cancellationToken), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
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

        private IEnumerable<string> EnumerateSources()
        {
            foreach (var fileName in new[] { "config.toml", "snapshot.jsonl", "roles.json" })
            {
                var path = Path.Combine(_applicationDirectory, fileName);
                if (File.Exists(path))
                {
                    yield return path;
                }
            }

            var adaptersPath = Path.Combine(_applicationDirectory, "Adapters");
            if (Directory.Exists(adaptersPath))
            {
                foreach (var file in Directory.EnumerateFiles(adaptersPath, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            await using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
            return Convert.ToHexString(hash);
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
