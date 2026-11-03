using System;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Ein einzelner historischer Messpunkt eines States: Zeitstempel,
    /// Wert und semantisches Embedding. Wird persistiert, um Zeitreihen,
    /// Trends und Anomalien sp�ter auswerten zu k�nnen (z. B. durch einen
    /// history.0-kompatiblen Adapter).
    /// </summary>
    /// <param name="Id">ID des zugeh�rigen States.</param>
    /// <param name="Timestamp">Zeitpunkt der Messung (UTC).</param>
    /// <param name="Value">Wert zum Zeitpunkt der Messung.</param>
    /// <param name="Embedding">Semantisches Embedding des Payloads.</param>
    public sealed record HistorySample(string Id, DateTime Timestamp, object? Value, float[] Embedding);
}
