using System.Collections.Concurrent;
using System.Reflection;
using PayFlow.SharedKernel.Security;
using Serilog.Core;
using Serilog.Events;

namespace PayFlow.Api.Logging;

/// <summary>
/// Serilog destructuring policy: when an object is logged with <c>{@Value}</c>, properties marked
/// <see cref="SensitiveAttribute"/> or with sensitive names (password, secret, token, api key...)
/// are replaced with <see cref="Redaction.Mask"/>. Types without such members are left to Serilog.
/// </summary>
public sealed class SensitiveDataDestructuringPolicy : IDestructuringPolicy
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]?> SensitiveTypes = new();

    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, out LogEventPropertyValue result)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(propertyValueFactory);

        var properties = SensitiveTypes.GetOrAdd(value.GetType(), Inspect);
        if (properties is null)
        {
            result = null!;
            return false;
        }

        var logged = new List<LogEventProperty>(properties.Length);
        foreach (var property in properties)
        {
            LogEventPropertyValue propertyValue = IsSensitive(property)
                ? new ScalarValue(Redaction.Mask)
                : propertyValueFactory.CreatePropertyValue(SafeGet(property, value), destructureObjects: true);
            logged.Add(new LogEventProperty(property.Name, propertyValue));
        }

        result = new StructureValue(logged, value.GetType().Name);
        return true;
    }

    /// <summary>Returns the readable properties if the type has at least one sensitive member; otherwise null.</summary>
    private static PropertyInfo[]? Inspect(Type type)
    {
        if (type.IsPrimitive || type == typeof(string) || type.IsEnum)
        {
            return null;
        }

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();
        return properties.Any(IsSensitive) ? properties : null;
    }

    private static bool IsSensitive(PropertyInfo property) =>
        property.IsDefined(typeof(SensitiveAttribute), inherit: true) || Redaction.IsSensitiveName(property.Name);

    private static object? SafeGet(PropertyInfo property, object instance)
    {
        try
        {
            return property.GetValue(instance);
        }
        catch (TargetInvocationException)
        {
            return "(unavailable)";
        }
    }
}
