using System;
using System.Numerics;

namespace Datamodel.Format;

/// <summary>
/// Subclass this attribute to define a custom attribute name convention.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public abstract class AttributeNamingConventionAttribute : System.Attribute
{
    public abstract string GetAttributeName(string propertyName, Type propertyType);
}

/// <summary>
/// This class' property names are mostly lowercase.
/// </summary>
public class LowercasePropertiesAttribute : AttributeNamingConventionAttribute
{
    public override string GetAttributeName(string propertyName, Type _)
        => propertyName.ToLowerInvariant();
}

/// <summary>
/// This class' property names are mostly camelCase.
/// </summary>
public class CamelCasePropertiesAttribute : AttributeNamingConventionAttribute
{
    public override string GetAttributeName(string propertyName, Type _)
        => char.ToLowerInvariant(propertyName.AsSpan()[0]) + propertyName[1..];
}

/// <summary>
/// This class' property names are mostly m_hungarian.
/// </summary>
public class HungarianPropertiesAttribute : CamelCasePropertiesAttribute
{
    public override string GetAttributeName(string propertyName, Type propertyType)
    {
        var typeAnnotation = propertyType switch
        {
            _ when propertyType == typeof(int) => "n",
            _ when propertyType == typeof(float) => "fl",
            _ when propertyType == typeof(bool) => "b",
            _ when propertyType == typeof(Vector2) => "v",
            _ when propertyType == typeof(Vector3) => "v",
            _ when propertyType == typeof(Vector4) => "v",
            _ when propertyType == typeof(Matrix4x4) => "mat",
            _ => string.Empty,
        };

        if (typeAnnotation == string.Empty)
        {
            return "m_" + base.GetAttributeName(propertyName, propertyType);
        }

        return "m_" + typeAnnotation + propertyName;
    }
}

[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class DMProperty : System.Attribute
{
    /// <param name="name">The name to use for serialization.</param>
    /// <param name="optional">Ignore serialization if property is on the default value.</param>
    public DMProperty(string? name = null, bool optional = false)
    {
        Name = name;
        Optional = optional;
    }

    public string? Name { get; }
    public bool Optional { get; }
}
