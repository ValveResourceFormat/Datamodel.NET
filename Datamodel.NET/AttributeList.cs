using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.ComponentModel;
using System.Linq;
using System.Numerics;

using AttrKVP = System.Collections.Generic.KeyValuePair<string, object?>;
using System.Reflection;
using System.IO;

namespace Datamodel
{
    /// <summary>
    /// A thread-safe collection of <see cref="Attribute"/>s.
    /// </summary>
    [DebuggerTypeProxy(typeof(DebugView))]
    [DebuggerDisplay("Count = {Count}")]
    public class AttributeList : IDictionary<string, object?>, IDictionary
    {
        internal OrderedDictionary PropertyInfos;
        internal OrderedDictionary Inner;
        protected object Attribute_ChangeLock = new();

        private ICollection<Attribute> GetPropertyBasedAttributes(bool useSerializationName)
        {
            var result = new List<Attribute>();
            foreach (DictionaryEntry entry in PropertyInfos)
            {
                if (entry.Value is null)
                {
                    throw new InvalidDataException("Property value can not be null");
                }

                var prop = (PropertyInfo)entry.Value;
                var name = useSerializationName ? (string)entry.Key : prop.Name;
                var attr = new Attribute(name, this, prop.GetValue(this));
                result.Add(attr);
            }
            return result;
        }

        internal class DebugView
        {
            public DebugView(AttributeList item)
            {
                Item = item;
            }
            [DebuggerBrowsable(DebuggerBrowsableState.Never)]
            protected AttributeList Item;

            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public DebugAttribute[] Attributes
                => Item.GetPropertyBasedAttributes(useSerializationName: false).Select(attr => new DebugAttribute(attr))
                .Concat(Item.Inner.Values.Cast<Attribute>().Select(attr => new DebugAttribute(attr)))
                .ToArray();

            [DebuggerDisplay("{Value}", Name = "{Attr.Name,nq}", Type = "{Attr.ValueType.FullName,nq}")]
            public class DebugAttribute
            {
                public DebugAttribute(Attribute attr)
                {
                    Attr = attr;
                }

                [DebuggerBrowsable(DebuggerBrowsableState.Never)]
                readonly Attribute Attr;

                [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
                object? Value { get { return Attr.Value; } }
            }
        }

        /// <summary>
        /// Contains the names of Datamodel types which are functionally identical to other types and don't have their own CLR representation.
        /// </summary>
        public enum OverrideType
        {
            /// <summary>
            /// Maps to <see cref="Vector3"/>.
            /// </summary>
            Angle,
            /// <summary>
            /// Maps to <see cref="byte[]"/>.
            /// </summary>
            Binary,
        }

        public AttributeList(Datamodel? owner)
        {
            var propertyAttributes = GetPropertyDerivedAttributeList();
            PropertyInfos = new OrderedDictionary(propertyAttributes?.Count ?? 0);
            if (propertyAttributes != null)
            {
                foreach (var attr in propertyAttributes)
                {
                    PropertyInfos.Add(attr.Name, attr.Property);
                }
            }

            Inner = [];
            Owner = owner;
        }

        /// <summary>
        /// Gets the <see cref="Datamodel"/> that this AttributeList is owned by.
        /// </summary>
        public virtual Datamodel? Owner { get; internal set; }

        /// <summary>
        /// Adds a new attribute to this AttributeList.
        /// </summary>
        /// <param name="key">The name of the attribute. Must be unique to this AttributeList.</param>
        /// <param name="value">The value of the Attribute. Must be of a valid Datamodel type.</param>
        public void Add(string key, object? value)
        {
            this[key] = value;
        }


        protected virtual ICollection<(string Name, PropertyInfo Property)>? GetPropertyDerivedAttributeList()
        {
            return null;
        }

        /// <summary>
        /// Gets the given atttribute's "override type". This applies when multiple Datamodel types map to the same CLR type.
        /// </summary>
        /// <param name="key">The name of the attribute.</param>
        /// <returns>The attribute's Datamodel type, if different from its CLR type.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when the given attribute is not present in the list.</exception>
        public OverrideType? GetOverrideType(string key)
        {
            var attrib = Inner[key];

            if (attrib is null)
            {
                return null;
            }

            return ((Attribute)attrib).OverrideType;
        }

        /// <summary>
        /// Sets the given attribute's "override type". This applies when multiple Datamodel types map to the same CLR type.
        /// </summary>
        /// <param name="key">The name of the attribute.</param>
        /// <param name="type">The Datamodel type which the attribute should be stored as when written to DMX, or null.</param>
        /// <exception cref="AttributeTypeException">Thrown when the attribute's CLR type does not map to the value given in <paramref name="type"/>.</exception>
        public void SetOverrideType(string key, OverrideType? type)
        {
            var attrib = Inner[key];

            if (attrib is not null)
            {
                ((Attribute)attrib).OverrideType = type;
            }

        }

        /// <summary>
        /// Inserts an Attribute at the given index.
        /// </summary>
        private void Insert(int index, Attribute item, bool notify = true)
        {
            lock (Attribute_ChangeLock)
            {
                Inner.Remove(item.Name);
                Inner.Insert(index, item.Name, item);
            }
            item.Owner = this;

            if (notify)
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item.ToKeyValuePair(), index));
        }

        public bool Remove(string key)
        {
            lock (Attribute_ChangeLock)
            {
                var attr = (Attribute?)Inner[key];
                if (attr == null) return false;

                var index = IndexOf(key);
                Inner.Remove(key);
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, attr.ToKeyValuePair(), index));
                return true;
            }
        }

        public bool TryGetValue(string key, out object? value)
        {
            Attribute? result;
            lock (Attribute_ChangeLock)
                result = (Attribute?)Inner[key];

            if (result != null)
            {
                value = result.RawValue;
                return true;
            }
            else
            {
                value = null;
                return false;
            }
        }

        public virtual bool ContainsKey(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            lock (Attribute_ChangeLock)
                return Inner[key] != null;
        }
        public ICollection<string> Keys
        {
            get { lock (Attribute_ChangeLock) return Inner.Keys.Cast<string>().ToArray(); }
        }
        public ICollection<object?> Values
        {
            get { lock (Attribute_ChangeLock) return Inner.Values.Cast<Attribute>().Select(attr => attr.Value).ToArray(); }
        }

        /// <summary>
        /// Gets or sets the value of the <see cref="Attribute"/> with the given name.
        /// </summary>
        /// <param name="name">The name to search for. Cannot be null.</param>
        /// <returns>The value associated with the given name.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the value of name is null.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when an attempt is made to get a name that is not present in this AttributeList.</exception>
        /// <exception cref="ElementOwnershipException">Thrown when an attempt is made to set the value of the attribute to an Element from a different <see cref="Datamodel"/>.</exception>
        /// <exception cref="AttributeTypeException">Thrown when an attempt is made to set a value that is not of a valid Datamodel attribute type.</exception>
        /// <exception cref="IndexOutOfRangeException">Thrown when the maximum number of Attributes allowed in an AttributeList has been reached.</exception>        
        public virtual object? this[string name]
        {
            get
            {
                ArgumentNullException.ThrowIfNull(name);
                var attr = (Attribute?)Inner[name];
                if (attr == null)
                {
                    var prop_attr = (PropertyInfo?)PropertyInfos[name];
                    if (prop_attr != null)
                    {
                        return prop_attr.GetValue(this);
                    }

                    throw new KeyNotFoundException($"{this} does not have an attribute called \"{name}\"");
                }

                return attr.Value;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(name);
                if (value != null && !Datamodel.IsDatamodelType(value.GetType()))
                    throw new AttributeTypeException($"{value.GetType().FullName} is not a valid Datamodel attribute type. (If this is an array, it must implement IList<T>).");

                if (Owner != null && this == Owner.PrefixAttributes && value?.GetType() == typeof(Element))
                    throw new AttributeTypeException("Elements are not supported as prefix attributes.");

                var prop = (PropertyInfo?)PropertyInfos[name];

                if (prop != null)
                {
                    if (prop.CanWrite)
                    {
                        // were actually fine with this being null, it will just set the value to null
                        // but need to check so the type check doesn't fail if it is null
                        if (value != null)
                        {
                            var valueType = value.GetType();

                            // types must be equal, or a superclass
                            if (prop.PropertyType != typeof(Element) && valueType.IsSubclassOf(prop.PropertyType))
                            {
                                throw new InvalidDataException($"class property '{prop.Name}' with type '{prop.PropertyType}' does not match the type '{valueType}' of the value being set, this is likely a mismatch between the real class and the class from the datamodel");
                            }
                        }

                        prop.SetValue(this, value);
                    }
                    else
                    {
                        var existingArray = prop.GetValue(this) as Array<Element>;
                        var incomingArray = value as Array<Element>;

                        if (existingArray is not null && incomingArray is not null)
                        {
                            // special case for reflection based deserialization
                            if (existingArray.Count == 0)
                            {
                                existingArray.AddRange(incomingArray);
                                return;
                            }
                            else
                            {
                                throw new InvalidOperationException($"Attribute '{name}' modifies property {prop.DeclaringType!.Name}.{prop.Name}, which is write only and can't be replaced.");
                            }
                        }


                        throw new InvalidDataException("Property of deserialisation class must be writeable, make sure it's public and has a public setter");
                    }

                    return;
                }

                Attribute? old_attr;
                Attribute? new_attr;
                int old_index = -1;
                lock (Attribute_ChangeLock)
                {
                    old_attr = (Attribute?)Inner[name];
                    new_attr = new Attribute(name, this, value);

                    if (old_attr != null)
                    {
                        old_index = IndexOf(old_attr.Name);
                        Inner.Remove(old_attr);
                    }
                    Insert(old_index == -1 ? Count : old_index, new Attribute(name, this, value), notify: false);
                }

                NotifyCollectionChangedEventArgs change_args;
                if (old_attr != null)
                    change_args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, new_attr.ToKeyValuePair(), old_attr.ToKeyValuePair(), old_index);
                else
                    change_args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, new_attr.ToKeyValuePair(), Count);

                OnCollectionChanged(change_args);
            }
        }

        /// <summary>
        /// Gets or sets the attribute at the given index.
        /// </summary>
        public AttrKVP this[int index]
        {
            get
            {
                var attr = (Attribute?)Inner[index];

                if (attr is null)
                {
                    throw new InvalidOperationException($"attribute at index {index} doesn't exist");
                }

                return attr.ToKeyValuePair();
            }
            set
            {
                RemoveAt(index);
                Insert(index, new Attribute(value.Key, this, value.Value));
            }
        }

        /// <summary>
        /// Removes the attribute at the given index.
        /// </summary>
        public void RemoveAt(int index)
        {
            Attribute? attr;
            lock (Attribute_ChangeLock)
            {
                attr = (Attribute?)Inner[index];

                if (attr is not null)
                {
                    attr.Owner = null;
                    Inner.RemoveAt(index);
                }
            }
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, attr, index));
        }

        public int IndexOf(string key)
        {
            lock (Attribute_ChangeLock)
            {
                int i = 0;
                foreach (string name in Inner.Keys)
                {
                    if (name == key) return i;
                    i++;
                }
            }
            return -1;
        }

        /// <summary>
        /// Removes all Attributes from the Collection.
        /// </summary>
        public void Clear()
        {
            lock (Attribute_ChangeLock)
                Inner.Clear();
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public int Count
        {
            get
            {
                lock (Attribute_ChangeLock)
                    return Inner.Count;
            }
        }

        public bool IsFixedSize { get { return false; } }
        public bool IsReadOnly { get { return false; } }
        public bool IsSynchronized { get { return true; } }
        /// <summary>
        /// Gets an object which can be used to synchronise access to the items within this AttributeCollection.
        /// </summary>
        public object SyncRoot { get { return Attribute_ChangeLock; } }

        public IEnumerable<AttrKVP> GetAllAttributesForSerialization()
        {
            foreach (var attr in GetPropertyBasedAttributes(useSerializationName: true))
                yield return attr.ToKeyValuePair();

            foreach (var attr in this)
                yield return attr;
        }

        public IEnumerator<AttrKVP> GetEnumerator()
        {
            foreach (var attr in Inner.Values.Cast<Attribute>().ToArray())
                yield return attr.ToKeyValuePair();
        }

        #region Interfaces

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }


        /// <summary>
        /// Raised when <see cref="Element.Name"/>, <see cref="Element.ClassName"/>, <see cref="Element.ID"/> or
        /// a custom Element property has changed.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName()] string property = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }

        /// <summary>
        /// Raised when an <see cref="Attribute"/> is added, removed, or replaced.
        /// </summary>
        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        protected virtual void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            Debug.Assert(!(e.NewItems != null && e.NewItems.OfType<Attribute>().Any()) && !(e.OldItems != null && e.OldItems.OfType<Attribute>().Any()));

            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                case NotifyCollectionChangedAction.Remove:
                case NotifyCollectionChangedAction.Reset:
                    OnPropertyChanged("Count");
                    break;
            }

            OnPropertyChanged("Item[]"); // this is the magic value of System.Windows.Data.Binding.IndexerName that tells the binding engine an indexer has changed

            CollectionChanged?.Invoke(this, e);
        }


        IDictionaryEnumerator IDictionary.GetEnumerator()
        {
            throw new NotImplementedException();
        }

        void IDictionary.Remove(object key)
        {
            Remove((string)key);
        }

        void IDictionary.Add(object key, object? value)
        {
            Add((string)key, value);
        }

        object? IDictionary.this[object key]
        {
            get
            {
                return this[(string)key];
            }
            set
            {
                this[(string)key] = value;
            }
        }

        bool IDictionary.Contains(object key)
        {
            return ContainsKey((string)key);
        }

        ICollection IDictionary.Keys { get { return (ICollection)Keys; } }
        ICollection IDictionary.Values { get { return (ICollection)Values; } }

        bool ICollection<AttrKVP>.Remove(AttrKVP item)
        {
            lock (Attribute_ChangeLock)
            {
                var attr = (Attribute?)Inner[item.Key];
                if (attr == null || attr.Value != item.Value) return false;
                Remove(attr.Name);
                return true;
            }
        }

        void ICollection<AttrKVP>.CopyTo(AttrKVP[] array, int arrayIndex)
        {
            ((ICollection)this).CopyTo(array, arrayIndex);
        }

        void ICollection.CopyTo(Array array, int index)
        {
            lock (Attribute_ChangeLock)
                foreach (Attribute attr in Inner.Values)
                {
                    array.SetValue(attr.ToKeyValuePair(), index);
                    index++;
                }
        }

        void ICollection<AttrKVP>.Add(AttrKVP item)
        {
            this[item.Key] = item.Value;
        }

        bool ICollection<AttrKVP>.Contains(AttrKVP item)
        {
            lock (Attribute_ChangeLock)
            {
                var attr = (Attribute?)Inner[item.Key];
                return attr != null && attr.Value == item.Value;
            }
        }

        #endregion
    }

    /// <summary>
    /// Compares two Attribute values, using <see cref="Element.IDComparer"/> for AttributeList comparisons.
    /// </summary>
    public class ValueComparer : IEqualityComparer
    {
        /// <summary>
        /// Gets a default Attribute value equality comparer.
        /// </summary>
        public static ValueComparer Default
        {
            get
            {
                _Default ??= new ValueComparer();
                return _Default;
            }
        }
        static ValueComparer? _Default;

        public new bool Equals(object? x, object? y)
        {
            if (x is null || y is null)
            {
                return false;
            }

            var type_x = x.GetType();
            var type_y = y.GetType();

            if (type_x == null && type_y == null)
                return true;

            if (type_x != type_y)
                return false;

            var inner = Datamodel.GetArrayInnerType(type_x);
            if (inner != null)
            {
                var array_left = (IList)x;
                var array_right = (IList)y;

                if (array_left.Count != array_right.Count) return false;

                return !Enumerable.Range(0, array_left.Count).Any(i => !Equals(array_left[i], array_right[i]));
            }
            else if (type_x == typeof(Element))
                return Element.IDComparer.Default.Equals((Element)x, (Element)y);
            else
                return EqualityComparer<object>.Default.Equals(x, y);
        }

        public int GetHashCode(object obj)
        {
            if (obj is Element elem)
                return elem.ID.GetHashCode();

            var inner = Datamodel.GetArrayInnerType(obj.GetType());
            if (inner != null)
            {
                int hash = 0;
                foreach (var item in (IList)obj)
                    hash ^= item.GetHashCode();
                return hash;
            }

            return obj.GetHashCode();
        }
    }
}
