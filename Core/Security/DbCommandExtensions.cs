using System.Data.Common;

namespace ioBroker.Core.Security
{
    internal static class DbCommandExtensions
    {
        public static void AddParameter(this DbCommand command, string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
