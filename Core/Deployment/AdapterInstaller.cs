using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ioBroker_NewGen.Core.Deployment
{
    /// <summary>
    /// Legt die Adapterstruktur an und installiert lokale Adapterpakete.
    /// Netzwerk-Download und Repository-Auswahl bleiben bewusst außerhalb
    /// dieser Dateisystemkomponente.
    /// </summary>
    public sealed class AdapterInstaller
    {
        public AdapterInstaller(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new ArgumentException("Adapter-Root darf nicht leer sein.", nameof(rootPath));
            }

            RootPath = Path.GetFullPath(rootPath);
            IoBrokerPath = Path.Combine(RootPath, "IOBroker");
            CustomPath = Path.Combine(RootPath, "custom");
        }

        public string RootPath { get; }

        public string IoBrokerPath { get; }

        public string CustomPath { get; }

        public void EnsureLayout()
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(IoBrokerPath);
            Directory.CreateDirectory(CustomPath);
        }

        public string InstallCustomAssembly(string assemblyPath)
        {
            var source = ValidateSourceFile(assemblyPath, ".dll");
            EnsureLayout();

            var destination = Path.Combine(CustomPath, Path.GetFileName(source));
            File.Copy(source, destination, overwrite: true);
            return destination;
        }

        public string InstallLegacyAdapterDirectory(string adapterName, string sourceDirectory)
        {
            var source = ValidateSourceDirectory(sourceDirectory);
            var safeName = ValidateAdapterName(adapterName);
            EnsureLayout();

            var destination = Path.Combine(IoBrokerPath, safeName);
            CopyDirectory(source, destination);
            return destination;
        }

        public IReadOnlyCollection<string> GetInstalledCustomAssemblies()
        {
            if (!Directory.Exists(CustomPath))
            {
                return Array.Empty<string>();
            }

            return Directory.GetFiles(CustomPath, "*.dll", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public IReadOnlyCollection<string> GetInstalledLegacyAdapters()
        {
            if (!Directory.Exists(IoBrokerPath))
            {
                return Array.Empty<string>();
            }

            return Directory.GetDirectories(IoBrokerPath, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public void RemoveAdapter(string adapterName, bool removeLegacyDirectory = true)
        {
            var safeName = ValidateAdapterName(adapterName);
            var customAssembly = Path.Combine(CustomPath, safeName + ".dll");
            if (File.Exists(customAssembly))
            {
                File.Delete(customAssembly);
            }

            if (removeLegacyDirectory)
            {
                var legacyDirectory = Path.Combine(IoBrokerPath, safeName);
                if (Directory.Exists(legacyDirectory))
                {
                    Directory.Delete(legacyDirectory, recursive: true);
                }
            }
        }

        private string ValidateSourceFile(string path, string requiredExtension)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("Adapterdatei nicht gefunden.", path);
            }

            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(fullPath), requiredExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Erwartet wird eine Datei mit Endung '{requiredExtension}'.", nameof(path));
            }

            return fullPath;
        }

        private static string ValidateSourceDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"Adapterverzeichnis nicht gefunden: '{path}'.");
            }

            return Path.GetFullPath(path);
        }

        private static string ValidateAdapterName(string adapterName)
        {
            if (string.IsNullOrWhiteSpace(adapterName) ||
                adapterName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                adapterName is "." or "..")
            {
                throw new ArgumentException("Ungültiger Adaptername.", nameof(adapterName));
            }

            return adapterName;
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.TopDirectoryOnly))
            {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }
}
