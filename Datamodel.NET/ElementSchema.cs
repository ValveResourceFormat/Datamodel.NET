using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Datamodel
{
    /// <summary>
    /// A public property of an <see cref="Element"/> subclass that is read and written as an attribute.
    /// </summary>
    /// <remarks>
    /// Instances are emitted by the ElementFactory that the KeyValues2.ElementFactoryGenerator generates into the assembly declaring the class,
    /// so no reflection is needed to move values between properties and attributes.
    /// </remarks>
    public class PropertyBinding
    {
        readonly Func<AttributeList, object?>? getter;
        readonly Action<AttributeList, object?>? setter;

        /// <param name="propertyName">The name of the property in the class.</param>
        /// <param name="attributeName">The name of the attribute in the file.</param>
        /// <param name="propertyType">The type of the property.</param>
        /// <param name="getter">Reads the property of the given element.</param>
        /// <param name="setter">Writes the property of the given element, or null when the property has no setter.</param>
        public PropertyBinding(string propertyName, string attributeName, Type propertyType, Func<AttributeList, object?> getter, Action<AttributeList, object?>? setter)
            : this(propertyName, attributeName, propertyType, setter != null)
        {
            ArgumentNullException.ThrowIfNull(getter);

            this.getter = getter;
            this.setter = setter;
        }

        private protected PropertyBinding(string propertyName, string attributeName, Type propertyType, bool canWrite)
        {
            ArgumentNullException.ThrowIfNull(propertyName);
            ArgumentNullException.ThrowIfNull(attributeName);
            ArgumentNullException.ThrowIfNull(propertyType);

            PropertyName = propertyName;
            AttributeName = attributeName;
            PropertyType = propertyType;
            CanWrite = canWrite;
        }

        /// <summary>
        /// Creates a binding from typed accessors, so that generated code needs no casts and values move without boxing.
        /// </summary>
        /// <typeparam name="TElement">The class declaring the property.</typeparam>
        /// <typeparam name="TValue">The type of the property.</typeparam>
        /// <param name="propertyName">The name of the property in the class.</param>
        /// <param name="attributeName">The name of the attribute in the file.</param>
        /// <param name="getter">Reads the property.</param>
        /// <param name="setter">Writes the property, or null when it has no setter.</param>
        public static PropertyBinding<TValue> Create<TElement, TValue>(string propertyName, string attributeName, Func<TElement, TValue> getter, Action<TElement, TValue>? setter)
            where TElement : AttributeList
        {
            ArgumentNullException.ThrowIfNull(getter);

            return new PropertyBinding<TElement, TValue>(propertyName, attributeName, getter, setter);
        }

        /// <summary>
        /// Gets the name of the property in the class.
        /// </summary>
        public string PropertyName { get; }

        /// <summary>
        /// Gets the name of the attribute in the file, after any naming convention or <see cref="Format.DMProperty"/> is applied.
        /// </summary>
        public string AttributeName { get; }

        /// <summary>
        /// Gets the type of the property.
        /// </summary>
        public Type PropertyType { get; }

        /// <summary>
        /// Gets whether the property can be assigned.
        /// </summary>
        public bool CanWrite { get; }

        /// <summary>
        /// Reads the property of the given element.
        /// </summary>
        public virtual object? GetValue(AttributeList owner) => getter!(owner);

        /// <summary>
        /// Writes the property of the given element.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the property has no setter.</exception>
        public virtual void SetValue(AttributeList owner, object? value)
        {
            if (setter == null)
            {
                throw new InvalidOperationException($"Property '{PropertyName}' is read-only.");
            }

            setter(owner, value);
        }

        /// <summary>
        /// Reads the property in the form an attribute slot stores it, so that a codec writes it without boxing when the binding is typed.
        /// </summary>
        internal virtual void Read(AttributeList owner, out AttributeKind kind, out InlineValue inline, out object? reference)
        {
            AttributeList.Classify(GetValue(owner), out kind, out inline, out reference);
        }

        public override string ToString() => $"{PropertyName} <{PropertyType.Name}> as \"{AttributeName}\"";
    }

    /// <summary>
    /// A <see cref="PropertyBinding"/> whose value type is known, so that codecs and the attribute indexer move values without boxing them.
    /// </summary>
    /// <typeparam name="TValue">The type of the property.</typeparam>
    public abstract class PropertyBinding<TValue> : PropertyBinding
    {
        private protected PropertyBinding(string propertyName, string attributeName, bool canWrite)
            : base(propertyName, attributeName, typeof(TValue), canWrite)
        {
        }

        /// <summary>
        /// Reads the property of the given element.
        /// </summary>
        public abstract TValue Get(AttributeList owner);

        /// <summary>
        /// Writes the property of the given element.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the property has no setter.</exception>
        public abstract void Set(AttributeList owner, TValue value);

        public override object? GetValue(AttributeList owner) => Get(owner);

        public override void SetValue(AttributeList owner, object? value) => Set(owner, (TValue)value!);
    }

    /// <summary>
    /// The binding <see cref="PropertyBinding.Create{TElement, TValue}"/> makes: the generated accessors of one property, called without any cast of the value.
    /// </summary>
    sealed class PropertyBinding<TElement, TValue> : PropertyBinding<TValue>
        where TElement : AttributeList
    {
        readonly Func<TElement, TValue> getter;
        readonly Action<TElement, TValue>? setter;

        public PropertyBinding(string propertyName, string attributeName, Func<TElement, TValue> getter, Action<TElement, TValue>? setter)
            : base(propertyName, attributeName, setter != null)
        {
            this.getter = getter;
            this.setter = setter;
        }

        public override TValue Get(AttributeList owner) => getter((TElement)owner);

        /// <summary>The kind a slot stores values of <typeparamref name="TValue"/> as, decided once per type.</summary>
        static readonly AttributeKind ValueKind = AttributeList.KindOf(typeof(TValue));

        internal override void Read(AttributeList owner, out AttributeKind kind, out InlineValue inline, out object? reference)
        {
            var value = getter((TElement)owner);
            kind = ValueKind;
            inline = default;
            if (ValueKind == AttributeKind.Reference)
            {
                reference = value;
            }
            else
            {
                reference = null;
                Unsafe.As<InlineValue, TValue>(ref inline) = value;
            }
        }

        public override void Set(AttributeList owner, TValue value)
        {
            if (setter == null)
            {
                throw new InvalidOperationException($"Property '{PropertyName}' is read-only.");
            }

            setter((TElement)owner, value);
        }
    }

    /// <summary>
    /// Describes how an <see cref="Element"/> subclass maps onto a file: its class name and the properties that are stored as attributes.
    /// </summary>
    /// <remarks>
    /// Schemas are registered by the generated ElementFactory of each assembly through <see cref="Datamodel.RegisterElementFactory"/>.
    /// An Element subclass without a registered schema stores every attribute in its attribute list, like a plain Element.
    /// </remarks>
    public sealed class ElementSchema
    {
        /// <summary>
        /// The schema of a class that declares no properties.
        /// </summary>
        public static ElementSchema Empty { get; } = new(typeof(Element), null);

        static readonly ConcurrentDictionary<Type, ElementSchema> Registry = new();

        readonly Dictionary<string, PropertyBinding> ByAttributeName;

        /// <param name="elementType">The Element subclass described.</param>
        /// <param name="className">The class name written to the file, or null to use the type name.</param>
        /// <param name="propertyGroups">The properties of every class in the inheritance chain, base class first, each in declaration order.</param>
        public ElementSchema(Type elementType, string? className, params PropertyBinding[][] propertyGroups)
        {
            ArgumentNullException.ThrowIfNull(elementType);
            ArgumentNullException.ThrowIfNull(propertyGroups);

            ElementType = elementType;
            ClassName = className;

            var properties = new List<PropertyBinding>();
            ByAttributeName = [];

            foreach (var group in propertyGroups)
            {
                foreach (var binding in group)
                {
                    // a derived class hiding a base property replaces it in place
                    if (ByAttributeName.TryGetValue(binding.AttributeName, out var existing))
                    {
                        properties[properties.IndexOf(existing)] = binding;
                    }
                    else
                    {
                        properties.Add(binding);
                    }

                    ByAttributeName[binding.AttributeName] = binding;
                }
            }

            Properties = properties;
        }

        /// <summary>
        /// Gets the Element subclass described by this schema.
        /// </summary>
        public Type ElementType { get; }

        /// <summary>
        /// Gets the class name written to the file, or null when the type name is used.
        /// </summary>
        public string? ClassName { get; }

        /// <summary>
        /// Gets the properties stored as attributes, base class first, each class in declaration order.
        /// </summary>
        public IReadOnlyList<PropertyBinding> Properties { get; }

        /// <summary>
        /// Gets the property stored under the given attribute name, or null when no property claims it.
        /// </summary>
        public PropertyBinding? GetProperty(string attributeName)
        {
            return ByAttributeName.TryGetValue(attributeName, out var binding) ? binding : null;
        }

        /// <summary>
        /// Registers the schema of an Element subclass. A schema registered earlier for the same type is replaced.
        /// </summary>
        public static void Register(ElementSchema schema)
        {
            ArgumentNullException.ThrowIfNull(schema);
            Registry[schema.ElementType] = schema;
        }

        /// <summary>
        /// Gets the registered schema of the given type, or <see cref="Empty"/> when none is registered.
        /// </summary>
        public static ElementSchema For(Type elementType)
        {
            return Registry.TryGetValue(elementType, out var schema) ? schema : Empty;
        }

        public override string ToString() => $"{ElementType.Name} ({Properties.Count} properties)";
    }
}
