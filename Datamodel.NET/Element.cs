using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace Datamodel
{
    /// <summary>
    /// A thread-safe collection of <see cref="Attribute"/>s. Declares a name, class name, and unique (to the owning <see cref="Datamodel"/>) ID.
    /// </summary>
    /// <remarks>Recursion is allowed, i.e. an <see cref="Attribute"/> can refer to an <see cref="Element"/> which is higher up the tree.</remarks>
    /// <seealso cref="Attribute"/>
    [TypeConverter(typeof(TypeConverters.ElementConverter))]
    [DebuggerTypeProxy(typeof(AttributeList.DebugView))]
    [DebuggerDisplay("{Name} {ID}", Type = "{ClassName,nq}")]
    public class Element : AttributeList, IEquatable<Element>
    {
        #region Constructors and Init

        /// <summary>
        /// Creates a new Element with a specified name, optionally specifying an ID and class name.
        /// </summary>
        /// <param name="owner">The owner of this Element. Cannot be null.</param>
        /// <param name="id">A GUID that must be unique within the owning Datamodel. Can be null, in which case a random GUID is generated.</param>
        /// <param name="name">An arbitrary string. Does not have to be unique, and can be null.</param>
        /// <param name="class_name">An arbitrary string which loosely defines the type of Element this is. Cannot be null.</param>
        /// <exception cref="IndexOutOfRangeException">Thrown when the owner already contains the maximum number of Elements allowed in a Datamodel.</exception>
        public Element(Datamodel owner, string name, Guid? id = null, string? classNameOverride = null)
            : base(owner)
        {
            ArgumentNullException.ThrowIfNull(owner);

            Name = name;
            ClassName = classNameOverride ?? ClassName;

            if (id.HasValue)
                ID = id.Value;
            else
            {
                if (!owner.AllowRandomIDs) throw new InvalidOperationException("Random IDs are not allowed in this Datamodel.");
                ID = Guid.NewGuid();
            }
            Owner = owner;
        }

        /// <summary>
        /// Creates a new stub Element to represent an Element in another Datamodel.
        /// </summary>
        /// <seealso cref="Element.Stub"/>
        /// <param name="owner">The owner of this Element. Cannot be null.</param>
        /// <param name="id">The ID of the remote Element that this stub represents.</param>
        public Element(Datamodel owner, Guid id)
            : base(owner)
        {
            ArgumentNullException.ThrowIfNull(owner);

            ID = id;
            Stub = true;
            Name = "Stub element";
            Owner = owner;
        }

        /// <summary>
        /// Creates a new Element with a random GUID.
        /// </summary>
        public Element()
            : base(null)
        {
            ID = Guid.NewGuid();

            // For subclasses get the actual classname
            if (GetType() != typeof(Element))
            {
                var type = GetType().Name;
                var index = type.IndexOf('`');
                if (index > 0)
                {
                    type = type[..index];
                }

                ClassName = type;
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the ID of this Element. This must be unique within the Element's <see cref="Datamodel"/>.
        /// </summary>
        /// <remarks>
        /// An Element's ID is its identity: it decides both <see cref="Equals(Element)"/> and
        /// <see cref="GetHashCode"/>. Assign it before the Element joins a <see cref="Datamodel"/> or any other
        /// hash-based collection, because changing it afterwards strands the Element in its old bucket.
        /// </remarks>
        public Guid ID { get; set; }

        /// <summary>
        /// Gets or sets the name of this Element.
        /// </summary>
        public string Name
        {
            get => _Name;
            set
            {
                _Name = value; OnPropertyChanged();
            }
        }
        string _Name = string.Empty;

        /// <summary>
        /// Gets or sets the class of this Element. This is a string which loosely defines what <see cref="Attribute"/>s the Element contains.
        /// </summary>
        public string ClassName
        {
            get => _ClassName;
            set
            {
                _ClassName = value; OnPropertyChanged();
            }
        }
        string _ClassName = "DmeElement";

        /// <summary>
        /// Gets or sets whether this Element is a stub.
        /// </summary>
        /// <remarks>A Stub element does (or did) exist, but is not defined in this Element's <see cref="Datamodel"/>. Only its <see cref="ID"/> is known.</remarks>
        public bool Stub
        {
            get => _Stub;
            set
            {
                if (value && Count > 0) throw new InvalidOperationException("An Element containing Attributes cannot be a Stub.");
                _Stub = value;
            }
        }
        bool _Stub;

        /// <summary>
        /// Gets the <see cref="Datamodel"/> that this Element is owned by.
        /// </summary>
        public new Datamodel? Owner
        {
            get => base.Owner;
            internal set
            {
                if (value != null && base.Owner != null && base.Owner.AllElements.Contains(this)) throw new InvalidOperationException("Element already has an owner.");
                base.Owner = value;
                if (value != null)
                {
                    value.AllElements.ChangeLock.EnterWriteLock();
                    try
                    {
                        value.AllElements.Add(this);
                        if (value.AllElements.Count == 1) value.Root = this;
                    }
                    finally { value.AllElements.ChangeLock.ExitWriteLock(); }
                }
            }
        }

        #endregion

        #region Properties

        /// <remarks>
        /// This is expensive enough to dominate Element construction, so it must only ever be called once per
        /// type. <see cref="AttributeList"/> caches the result; don't call it from a hot path.
        /// </remarks>
        protected override ICollection<(string Name, PropertyInfo Property)>? GetPropertyDerivedAttributeList()
        {
            var type = GetType();
            if (type == typeof(Element))
            {
                return null; // The base class has no auto-properties
            }

            var properties = new List<(string Name, PropertyInfo Property)>();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                // Check if the property is an auto-property and is declared by a subclass of Element
                var declaringType = property.DeclaringType!;

                if (declaringType.IsSubclassOf(typeof(Element)))
                {
                    var name = property.Name;
                    name = declaringType.GetCustomAttribute<Format.AttributeNamingConventionAttribute>()?.GetAttributeName(name, property.PropertyType) ?? name;
                    name = property.GetCustomAttribute<Format.DMProperty>()?.Name ?? name;
                    properties.Add((name, property));
                }
            }

            return properties;
        }

        #endregion Properties

        /// <summary>
        /// Returns the value of the <see cref="Attribute"/> with the specified type and name. An exception is thrown there is no Attribute of the given name and type.
        /// </summary>
        /// <seealso cref="GetArray&lt;T&gt;"/>
        /// <typeparam name="T">The expected Type of the Attribute.</typeparam>
        /// <param name="name">The Attribute name to search for.</param>
        /// <returns>The value of the Attribute with the given name.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the value of name is null.</exception>
        /// <exception cref="AttributeTypeException">Thrown when the value of the requested Attribute is not compatible with T.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when an attempt is made to get a name that is not present on this Element.</exception>
        public T? Get<T>(string name)
        {
            object? value = this[name];

            if (value is not T && !(typeof(T) == typeof(Element) && value == null))
                throw new AttributeTypeException(string.Format("Attribute \"{0}\" ({1}) does not implement {2}.", name, value?.GetType().Name, typeof(T).Name));

            return (T?)value;
        }

        /// <summary>
        /// Returns the value of the <see cref="Attribute"/> with the specified type and name, if it is an array. An exception is thrown there is no array Attribute of the given name and type.
        /// </summary>
        /// <remarks>This is a convenience function that calls <see cref="Get&lt;T&gt;"/>.</remarks>
        /// <typeparam name="T">The expected <see cref="Type"/> of the array's items.</typeparam>
        /// <param name="name">The name to search for.</param>
        /// <returns>The value of the Attribute with the given name.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the value of name is null.</exception>
        /// <exception cref="AttributeTypeException">Thrown when the value of the requested Attribute is not compatible with IList&lt;T&gt;.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when an attempt is made to get a name that is not present on this Element.</exception>
        public IList<T>? GetArray<T>(string name)
        {
            try
            {
                return Get<IList<T>>(name);
            }
            catch (AttributeTypeException)
            {
                throw new AttributeTypeException(string.Format("Attribute \"{0}\" ({1}) is not an array.", name, this[name]?.GetType().Name));
            }

        }

        /// <summary>
        /// Gets or sets the value of the <see cref="Attribute"/> with the given name.
        /// </summary>
        /// <param name="name">The name to search for. Cannot be null.</param>
        /// <returns>The value associated with the given name.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the value of <paramref name="name"/> is null.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when an attempt is made to get a name that is not present on this Element.</exception>
        /// <exception cref="InvalidOperationException">Thrown when an attempt is made to get or set an attribute on a <see cref="Stub"/> Element.</exception>
        /// <exception cref="ElementOwnershipException">Thrown when an attempt is made to set the value of the attribute to an Element from a different <see cref="Datamodel"/>.</exception>
        /// <exception cref="AttributeTypeException">Thrown when an attempt is made to set a value that is not of a valid Datamodel attribute type.</exception>
        /// <exception cref="IndexOutOfRangeException">Thrown when the maximum number of Attributes allowed in an Element has been reached.</exception>
        public override object? this[string name]
        {
            get
            {
                if (Stub) throw new InvalidOperationException("Cannot get attributes from a stub element.");
                return base[name];
            }
            set
            {
                if (Stub) throw new InvalidOperationException("Cannot set attributes on a stub element.");
                base[name] = value;
            }
        }

        [Obsolete("Use the ContainsKey method.")]
        public bool Contains(string name)
        {
            return ContainsKey(name);
        }

        public override string ToString()
        {
            return string.Format("{0}[{1}]", Name, ClassName);
        }

        #region IEqualityComparer
        /// <summary>
        /// Compares two <see cref="Element"/>s for equivalence by using their Names.
        /// </summary>
        public class NameComparer : IEqualityComparer, IEqualityComparer<Element>
        {
            /// <summary>
            /// Gets a default Element Name equality comparer.
            /// </summary>
            public static NameComparer Default { get; } = new NameComparer();

            public bool Equals(Element? x, Element? y)
            {
                return x?.Name == y?.Name;
            }

            public int GetHashCode(Element obj)
            {
                return obj.Name.GetHashCode();
            }

            bool IEqualityComparer.Equals(object? x, object? y)
            {
                return Equals((Element?)x, (Element?)y);
            }

            int IEqualityComparer.GetHashCode(object obj)
            {
                return GetHashCode((Element)obj);
            }
        }
        /// <summary>
        /// Compares two <see cref="Element"/>s for equivalence by using their ClassNames.
        /// </summary>
        public class ClassNameComparer : IEqualityComparer, IEqualityComparer<Element>
        {
            /// <summary>
            /// Gets a default Element ClassName equality comparer.
            /// </summary>
            public static ClassNameComparer Default { get; } = new ClassNameComparer();

            public bool Equals(Element? x, Element? y)
            {
                return x?.ClassName == y?.ClassName;
            }

            public int GetHashCode(Element obj)
            {
                return obj.ClassName.GetHashCode();
            }

            bool IEqualityComparer.Equals(object? x, object? y)
            {
                return Equals((Element?)x, (Element?)y);
            }

            int IEqualityComparer.GetHashCode(object obj)
            {
                return GetHashCode((Element)obj);
            }
        }
        /// <summary>
        /// Compares two <see cref="Element"/>s for equivalence by using their IDs.
        /// </summary>
        public class IDComparer : IEqualityComparer, IEqualityComparer<Element>
        {
            /// <summary>
            /// Gets a default Element ID equality comparer.
            /// </summary>
            public static IDComparer Default { get; } = new IDComparer();

            public bool Equals(Element? x, Element? y)
            {
                return x?.ID == y?.ID;
            }

            public int GetHashCode(Element obj)
            {
                return obj.ID.GetHashCode();
            }

            bool IEqualityComparer.Equals(object? x, object? y)
            {
                return Equals((Element?)x, (Element?)y);
            }

            int IEqualityComparer.GetHashCode(object obj)
            {
                return GetHashCode((Element)obj);
            }
        }
        #endregion

        /// <summary>
        /// Adds a new deferred attribute to this Element.
        /// </summary>
        /// <param name="key">The name of the attribute. Must be unique to this Element.</param>
        /// <param name="offset">The location of the attribute's value in the Datamodel's source stream.</param>
        internal void Add(string key, long offset)
        {
            lock (Attribute_ChangeLock)
                Inner[key] = new Attribute(key, this, offset);
        }

        public override bool ContainsKey(string key)
        {
            if (Stub) throw new InvalidOperationException("Cannot access attributes on a stub element.");
            return base.ContainsKey(key);
        }

        /// <summary>
        /// Determines whether this Element has the same <see cref="ID"/> as another.
        /// </summary>
        public bool Equals(Element? other)
        {
            return other != null && ID == other.ID;
        }

        /// <summary>
        /// Determines whether the given object is an <see cref="Element"/> with the same <see cref="ID"/> as this one.
        /// </summary>
        public override bool Equals(object? obj)
        {
            return Equals(obj as Element);
        }

        /// <summary>
        /// Returns a hash code derived from <see cref="ID"/>, to match <see cref="Equals(Element)"/>.
        /// </summary>
        public override int GetHashCode()
        {
            return ID.GetHashCode();
        }
    }

    namespace TypeConverters
    {
        public class ElementConverter : TypeConverter
        {
            public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
            {
                if (sourceType == typeof(string) || sourceType == typeof(Guid)) return true;
                return base.CanConvertFrom(context, sourceType);
            }

            public override object? ConvertFrom(ITypeDescriptorContext? context, System.Globalization.CultureInfo? culture, object value)
            {
                Guid guid_value;

                if (value is string str_value)
                    guid_value = Guid.Parse(str_value);
                else if (value is Guid guid)
                    guid_value = guid;
                else
                    return base.ConvertFrom(context, culture, value);

                var result = new Element();

                var ir = (ISupportInitialize)result;
                ir.BeginInit();
                result.Stub = true;
                result.ID = guid_value;
                ir.EndInit();

                return result;
            }

            public override bool IsValid(ITypeDescriptorContext? context, object? value)
            {
                if (value is null)
                    return false;
                if (value is Guid)
                    return true;
                if (value is string str_value && Guid.TryParse(str_value, out _))
                    return true;
                return false;
            }

            public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
            {
                if (destinationType == typeof(Guid) || destinationType == typeof(string))
                    return true;
                return base.CanConvertTo(context, destinationType);
            }

            public override object? ConvertTo(ITypeDescriptorContext? context, System.Globalization.CultureInfo? culture, object? value, Type destinationType)
            {
                if (value is null)
                    return null;
                var element = (Element)value;
                if (destinationType == typeof(Guid))
                    return element.ID;
                if (destinationType == typeof(string))
                    return element.ID.ToString();

                return base.ConvertTo(context, culture, value, destinationType);
            }
        }
    }
}
