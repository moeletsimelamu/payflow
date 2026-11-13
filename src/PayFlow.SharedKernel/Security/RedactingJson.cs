using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace PayFlow.SharedKernel.Security;

/// <summary>
/// JSON serialisation that never emits secrets. A value is masked when its property carries
/// <see cref="SensitiveAttribute"/> or when its name looks sensitive (see <see cref="Redaction.IsSensitiveName"/>),
/// at any depth, including inside dictionaries and anonymous objects.
/// </summary>
public static class RedactingJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { MaskAttributedProperties } },
    };

    public static string Serialize(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), Options);
        MaskByName(node);
        return node?.ToJsonString() ?? "null";
    }

    private static void MaskAttributedProperties(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.AttributeProvider is MemberInfo member && member.IsDefined(typeof(SensitiveAttribute), inherit: true))
            {
                property.CustomConverter = null;
                var originalType = property.PropertyType;
                if (originalType == typeof(string))
                {
                    property.Get = _ => Redaction.Mask;
                }
                else
                {
                    // Non-string sensitive members are dropped rather than masked to keep the JSON type-correct.
                    property.ShouldSerialize = (_, _) => false;
                }
            }
        }
    }

    private static void MaskByName(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (Redaction.IsSensitiveName(key))
                    {
                        obj[key] = Redaction.Mask;
                    }
                    else
                    {
                        MaskByName(obj[key]);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    MaskByName(item);
                }

                break;
        }
    }
}
