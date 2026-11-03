using System;
using RSEV.Utilities.Configuration;

namespace ioBroker_NewGen.Core.Adapters.Protocol
{
    /// <summary>
    /// Liest optionale Adapterkonfiguration mit stabilen Defaults. Einige
    /// Konfigurationsprovider liefern bei fehlenden Keys den Defaultwert des
    /// Typs statt einer Exception; leere Strings und numerische Nullwerte
    /// werden deshalb nur dann übernommen, wenn sie selbst der Fallback sind.
    /// </summary>
    public static class AdapterConfigReader
    {
        public static T Read<T>(IConfig config, string key, T fallback)
        {
            ArgumentNullException.ThrowIfNull(config);

            try
            {
                var value = config.Get<T>(key);
                if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
                {
                    return fallback;
                }

                if (IsNumericZero(value) && !IsNumericZero(fallback))
                {
                    return fallback;
                }

                return value;
            }
            catch
            {
                return fallback;
            }
        }

        private static bool IsNumericZero<T>(T value)
        {
            return value switch
            {
                byte number => number == 0,
                sbyte number => number == 0,
                short number => number == 0,
                ushort number => number == 0,
                int number => number == 0,
                uint number => number == 0,
                long number => number == 0,
                ulong number => number == 0,
                float number => number == 0,
                double number => number == 0,
                decimal number => number == 0,
                _ => false
            };
        }
    }
}
