using System;
using System.Globalization;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Optionale fachliche Regeln für einen State.
    /// </summary>
    public sealed class StateDefinition
    {
        public string? ValueType { get; init; }

        public double? Min { get; init; }

        public double? Max { get; init; }

        public string? Role { get; init; }

        public string? Unit { get; init; }

        public bool ValidateDefinition(out string? error)
        {
            error = null;
            if (Min.HasValue && Max.HasValue && Min.Value > Max.Value)
            {
                error = $"Minimum {Min.Value} darf nicht größer als Maximum {Max.Value} sein.";
                return false;
            }

            if (Role is not null && string.IsNullOrWhiteSpace(Role))
            {
                error = "Eine gesetzte Rolle darf nicht leer sein.";
                return false;
            }

            if (Unit is not null && string.IsNullOrWhiteSpace(Unit))
            {
                error = "Eine gesetzte Einheit darf nicht leer sein.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(ValueType) && !IsSupportedType(ValueType.Trim()))
            {
                error = $"Nicht unterstützter ValueType: '{ValueType}'.";
                return false;
            }

            return true;
        }

        public bool Validate(object? value, out string? error)
        {
            error = null;
            var normalizedType = ValueType?.Trim().ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(normalizedType) && !MatchesType(value, normalizedType))
            {
                error = $"Werttyp '{value?.GetType().Name ?? "null"}' entspricht nicht '{ValueType}'.";
                return false;
            }

            if (value is IConvertible convertible && (Min.HasValue || Max.HasValue))
            {
                if (!double.TryParse(convertible.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    error = "Min-/Max-Grenzen sind nur für numerische Werte zulässig.";
                    return false;
                }

                if (Min.HasValue && number < Min.Value)
                {
                    error = $"Wert {number} liegt unter dem Minimum {Min.Value}.";
                    return false;
                }

                if (Max.HasValue && number > Max.Value)
                {
                    error = $"Wert {number} liegt über dem Maximum {Max.Value}.";
                    return false;
                }
            }

            return true;
        }

        private static bool IsSupportedType(string valueType)
        {
            return valueType.ToLowerInvariant() switch
            {
                "any" or "object" or "null" or "bool" or "boolean" or "string" or "text" or
                "int" or "int32" or "integer" or "long" or "int64" or "float" or "single" or
                "double" or "decimal" or "number" or "json" => true,
                _ => false
            };
        }

        private static bool MatchesType(object? value, string normalizedType)
        {
            return normalizedType switch
            {
                "any" or "object" => true,
                "null" => value is null,
                "bool" or "boolean" => value is bool,
                "string" or "text" => value is string,
                "int" or "int32" or "integer" => value is int,
                "long" or "int64" => value is long,
                "float" or "single" => value is float,
                "double" or "decimal" or "number" => value is double or decimal or float or int or long,
                "json" => value is System.Text.Json.JsonElement or string or System.Collections.IDictionary,
                _ => true
            };
        }
    }
}
