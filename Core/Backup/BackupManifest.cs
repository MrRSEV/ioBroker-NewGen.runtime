using System;
using System.Collections.Generic;

namespace ioBroker_NewGen.Core.Backup
{
    public sealed class BackupManifest
    {
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

        public string Product { get; init; } = "ioBroker-NewGen";

        public string Version { get; init; } = "beta0.2.0";

        public List<BackupFileEntry> Files { get; init; } = new();
    }

    public sealed class BackupFileEntry
    {
        public required string RelativePath { get; init; }

        public long Length { get; init; }

        public required string Sha256 { get; init; }
    }
}
