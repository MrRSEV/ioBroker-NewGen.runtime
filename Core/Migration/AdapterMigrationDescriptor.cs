using System;
using System.Collections.Generic;

namespace ioBroker_NewGen.Core.Migration
{
    public enum MigrationPhase
    {
        Phase1Critical,
        Phase2Medium,
        Phase3Small,
        LegacyFallback
    }

    public sealed class AdapterMigrationDescriptor
    {
        public required string Name { get; init; }

        public required MigrationPhase Phase { get; init; }

        public required string Transport { get; init; }

        public IReadOnlyCollection<string> RequiredConfiguration { get; init; } = Array.Empty<string>();

        public bool NativeImplementationAvailable { get; init; }

        public bool ProtocolFixtureAvailable { get; init; }

        public string? ImplementationAssembly { get; init; }

        public string Status => NativeImplementationAvailable ? "native" : "legacy-or-pending";

        public string VerificationStatus => !NativeImplementationAvailable
            ? "legacy-or-pending"
            : ProtocolFixtureAvailable
                ? "native-verified"
                : "native-fixture-pending";
    }
}
