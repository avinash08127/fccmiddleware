using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace FccDesktopAgent.Core.Security;

/// <summary>
/// Serilog destructuring policy that redacts properties marked with <see cref="SensitiveDataAttribute"/>.
/// Prevents accidental logging of credentials, tokens, and PII (architecture rule #9).
///
/// Register via <c>.Destructure.With&lt;SensitiveDataDestructuringPolicy&gt;()</c> on the Serilog config.
/// </summary>
public sealed class SensitiveDataDestructuringPolicy : IDestructuringPolicy
{
    private const string RedactedValue = "[REDACTED]";

    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        var type = value.GetType();

        // Only process types that have at least one [SensitiveData] property
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasSensitive = false;
        foreach (var prop in properties)
        {
            if (prop.GetCustomAttribute<SensitiveDataAttribute>() is not null)
            {
                hasSensitive = true;
                break;
            }
        }

        if (!hasSensitive)
        {
            result = null;
            return false;
        }

        var logProperties = new List<LogEventProperty>();

        foreach (var prop in properties)
        {
            if (prop.GetCustomAttribute<SensitiveDataAttribute>() is not null)
            {
                logProperties.Add(new LogEventProperty(
                    prop.Name,
                    propertyValueFactory.CreatePropertyValue(RedactedValue)));
            }
            else
            {
                try
                {
                    var propValue = prop.GetValue(value);
                    logProperties.Add(new LogEventProperty(
                        prop.Name,
                        propertyValueFactory.CreatePropertyValue(propValue, destructureObjects: true)));
                }
                catch
                {
                    logProperties.Add(new LogEventProperty(
                        prop.Name,
                        propertyValueFactory.CreatePropertyValue("<error reading property>")));
                }
            }
        }

        result = new StructureValue(logProperties, type.Name);
        return true;
    }
}
