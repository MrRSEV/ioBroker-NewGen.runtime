using System;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Ein einzelner historischer Messpunkt eines States: Zeitstempel und
    /// Wert. Semantische Embeddings werden am MatrixObject gehalten und nicht
    /// in jedem History-Eintrag dupliziert.
    /// </summary>
    public sealed record HistorySample(string Id, DateTime Timestamp, object? Value);
}
