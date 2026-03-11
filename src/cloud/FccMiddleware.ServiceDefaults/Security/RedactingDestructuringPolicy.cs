using System.Reflection;
using FccMiddleware.Domain.Common;
using Serilog.Core;
using Serilog.Events;

namespace FccMiddleware.ServiceDefaults.Security;

public sealed class RedactingDestructuringPolicy : IDestructuringPolicy
{
    private const string RedactedValue = "[REDACTED]";

    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue? result)
    {
        result = null;

        var type = value.GetType();
        if (!ShouldDestructure(type))
        {
            return false;
        }

        var properties = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Select(property =>
            {
                var propertyValue = IsSensitive(property)
                    ? new ScalarValue(RedactedValue)
                    : propertyValueFactory.CreatePropertyValue(property.GetValue(value), destructureObjects: true);

                return new LogEventProperty(property.Name, propertyValue);
            })
            .ToList();

        result = new StructureValue(properties, type.Name);
        return true;
    }

    private static bool ShouldDestructure(Type type) =>
        type.IsClass
        && type != typeof(string)
        && !type.FullName!.StartsWith("System.", StringComparison.Ordinal)
        && !typeof(IEnumerable<object>).IsAssignableFrom(type);

    private static bool IsSensitive(MemberInfo member) =>
        member.GetCustomAttribute<SensitiveAttribute>() is not null;
}
