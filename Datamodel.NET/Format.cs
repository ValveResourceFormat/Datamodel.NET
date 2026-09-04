// This file is also compiled into the ElementFactoryGenerator, which applies the naming conventions at build time, so it must stay netstandard2.0 compatible.
using System;

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
        => NamingConventions.Lowercase(propertyName);
}

/// <summary>
/// This class' property names are mostly camelCase.
/// </summary>
public class CamelCasePropertiesAttribute : AttributeNamingConventionAttribute
{
    public override string GetAttributeName(string propertyName, Type _)
        => NamingConventions.CamelCase(propertyName);
}

/// <summary>
/// This class' property names are mostly m_hungarian.
/// </summary>
public class HungarianPropertiesAttribute : CamelCasePropertiesAttribute
{
    public override string GetAttributeName(string propertyName, Type propertyType)
        => NamingConventions.Hungarian(propertyName, propertyType.FullName);
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

/// <summary>
/// The rules behind the naming convention attributes, in one place for the library and the generator.
/// </summary>
internal static class NamingConventions
{
    /// <summary>
    /// The conventions the generator applies at build time. Any other <see cref="AttributeNamingConventionAttribute"/> runs when the assembly is loaded.
    /// </summary>
    public static readonly Type[] BuiltIn =
    [
        typeof(LowercasePropertiesAttribute),
        typeof(CamelCasePropertiesAttribute),
        typeof(HungarianPropertiesAttribute),
    ];

    /// <summary>
    /// Applies the rule of one of the <see cref="BuiltIn"/> attributes.
    /// </summary>
    /// <param name="attributeType">The attribute class, one of <see cref="BuiltIn"/>.</param>
    /// <param name="propertyTypeName">The full name of the property type, as <see cref="Type.FullName"/> gives it.</param>
    public static string Apply(Type attributeType, string propertyName, string? propertyTypeName)
    {
        if (attributeType == typeof(LowercasePropertiesAttribute))
            return Lowercase(propertyName);
        if (attributeType == typeof(CamelCasePropertiesAttribute))
            return CamelCase(propertyName);
        if (attributeType == typeof(HungarianPropertiesAttribute))
            return Hungarian(propertyName, propertyTypeName);

        throw new ArgumentException($"{attributeType} is not a built-in naming convention.", nameof(attributeType));
    }

    /// <summary>The rule of <see cref="LowercasePropertiesAttribute"/>.</summary>
    public static string Lowercase(string propertyName)
        => propertyName.ToLowerInvariant();

    /// <summary>The rule of <see cref="CamelCasePropertiesAttribute"/>.</summary>
    public static string CamelCase(string propertyName)
        => char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);

    /// <summary>The rule of <see cref="HungarianPropertiesAttribute"/>.</summary>
    /// <param name="propertyTypeName">The full name of the property type, as <see cref="Type.FullName"/> gives it.</param>
    public static string Hungarian(string propertyName, string? propertyTypeName)
    {
        var typeAnnotation = propertyTypeName switch
        {
            "System.Int32" => "n",
            "System.Single" => "fl",
            "System.Boolean" => "b",
            "System.Numerics.Vector2" or "System.Numerics.Vector3" or "System.Numerics.Vector4" => "v",
            "System.Numerics.Matrix4x4" => "mat",
            _ => string.Empty,
        };

        if (typeAnnotation == string.Empty)
        {
            return "m_" + CamelCase(propertyName);
        }

        return "m_" + typeAnnotation + propertyName;
    }
}
