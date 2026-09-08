using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using AttrKVP = System.Collections.Generic.KeyValuePair<string, object?>;

namespace Datamodel
{
    /// <summary>
    /// The types an attribute value can have, named as Valve's DmAttributeType_t names them: what a slot stores, what a class property is read as
    /// and what the binary encoding writes. Values of the scalar and vector types live in the slot itself, see <see cref="AttributeList.IsInline"/>; the rest are held as references.
    /// </summary>
    enum AttributeType : byte
    {
        Element,
        Int,
        Float,
        Bool,
        String,
        Binary,
        Time,
        Color,
        Vector2,
        Vector3,
        Vector4,
        QAngle,
        Quaternion,
        Matrix,
        UInt64,
        Byte,
        /// <summary>An array of any of the above.</summary>
        Array,
        /// <summary>The value has not been read from the stream yet; <see cref="InlineValue.Offset"/> holds the position it starts at.</summary>
        Deferred,
    }

    /// <summary>
    /// Sixteen bytes that hold any scalar or vector attribute value without boxing it.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    struct InlineValue
    {
        [FieldOffset(0)] public int Int;
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public bool Bool;
        [FieldOffset(0)] public byte Byte;
        [FieldOffset(0)] public ulong UInt64;
        [FieldOffset(0)] public long Ticks;
        /// <summary>Alias of <see cref="Ticks"/>, used when the slot is <see cref="AttributeType.Deferred"/> and holds a stream offset rather than a duration.</summary>
        [FieldOffset(0)] public long Offset;
        [FieldOffset(0)] public Color Color;
        [FieldOffset(0)] public Vector2 Vector2;
        [FieldOffset(0)] public Vector3 Vector3;
        [FieldOffset(0)] public Vector4 Vector4;
        [FieldOffset(0)] public Quaternion Quaternion;
        [FieldOffset(0)] public QAngle QAngle;
    }

    /// <summary>
    /// One attribute of an <see cref="AttributeList"/>: its name and its value, stored inline for value types and as a reference otherwise.
    /// Modelled on Valve's fixed-size CDmAttribute, so that a plain element costs one slot per attribute and no further objects.
    /// </summary>
    struct AttributeSlot
    {
        public string Name;
        public object? Reference;
        public InlineValue Inline;
        public AttributeType Kind;
        public AttributeList.OverrideType? Override;
    }

    /// <summary>
    /// Receives the attributes of an <see cref="AttributeList"/> in the form their slots hold them, so that a codec writes them without boxing. See <see cref="AttributeList.VisitAttributes{TVisitor}"/>.
    /// </summary>
    interface IAttributeVisitor
    {
        /// <summary>Called once before the attributes, with how many follow.</summary>
        void Begin(int count);

        void Visit(string name, AttributeType kind, in InlineValue inline, object? reference);
    }

    /// <summary>
    /// A thread-safe collection of attributes.
    /// </summary>
    [DebuggerTypeProxy(typeof(DebugView))]
    [DebuggerDisplay("Count = {Count}")]
    public class AttributeList : IDictionary<string, object?>, IDictionary
    {
        AttributeSlot[]? slots;
        int count;

        /// <summary>
        /// The object locked while the list is changed. The list itself, which is also its <see cref="SyncRoot"/>.
        /// </summary>
        protected object Attribute_ChangeLock;

        /// <summary>
        /// Gets the properties of this class that are stored as attributes. Empty unless a schema is registered for the class.
        /// </summary>
        public ElementSchema Schema { get; }

        public AttributeList(Datamodel? owner)
        {
            Attribute_ChangeLock = this;

            var type = GetType();
            Schema = type == typeof(AttributeList) || type == typeof(Element) ? ElementSchema.Empty : ElementSchema.For(type);

            Owner = owner;
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
                => Item.Schema.Properties.Select(binding => new DebugAttribute(binding.PropertyName, binding.GetValue(Item)))
                .Concat(Item.Select(attr => new DebugAttribute(attr.Key, attr.Value)))
                .ToArray();

            [DebuggerDisplay("{Value}", Name = "{Name,nq}")]
            public class DebugAttribute(string name, object? value)
            {
                [DebuggerBrowsable(DebuggerBrowsableState.Never)]
                public string Name { get; } = name;

                [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
                public object? Value { get; } = value;
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

        /// <summary>
        /// Gets the <see cref="Datamodel"/> that this AttributeList is owned by.
        /// </summary>
        public virtual Datamodel? Owner { get; internal set; }

        #region Slots

        /// <summary>
        /// Returns the index of the attribute with the given name, or -1. Names of attributes read from a file are usually the same string instance as the query, which the first comparison catches.
        /// </summary>
        int Find(string name)
        {
            var slots = this.slots;
            for (var i = 0; i < count; i++)
            {
                var candidate = slots![i].Name;
                if (ReferenceEquals(candidate, name) || candidate == name)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Adds an empty slot with the given name at the end. The caller holds the lock.
        /// </summary>
        /// <summary>
        /// Makes room for the given number of attributes, so that a codec that knows the count adds them without growing the slots.
        /// </summary>
        internal void EnsureCapacity(int capacity)
        {
            lock (Attribute_ChangeLock)
            {
                if (slots == null || slots.Length < capacity)
                    System.Array.Resize(ref slots, capacity);
            }
        }

        ref AttributeSlot Append(string name)
        {
            if (slots == null || count == slots.Length)
                System.Array.Resize(ref slots, Math.Max(4, count * 2));

            ref var slot = ref slots[count++];
            slot = default;
            slot.Name = name;
            return ref slot;
        }

        /// <summary>
        /// Adds an empty slot with the given name at the given index. The caller holds the lock.
        /// </summary>
        ref AttributeSlot InsertAt(int index, string name)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)count, nameof(index));

            Append(name);
            if (index < count - 1)
            {
                System.Array.Copy(slots!, index, slots!, index + 1, count - 1 - index);
                slots![index] = default;
                slots[index].Name = name;
            }

            return ref slots![index];
        }

        void RemoveSlot(int index)
        {
            count--;
            if (index < count)
                System.Array.Copy(slots!, index + 1, slots!, index, count - index);

            slots![count] = default;
        }

        /// <summary>Whether values of the type are stored in the slot itself rather than as a reference.</summary>
        internal static bool IsInline(AttributeType type) => type is AttributeType.Int or AttributeType.Float or AttributeType.Bool or AttributeType.Byte or AttributeType.UInt64
            or AttributeType.Time or AttributeType.Color or AttributeType.Vector2 or AttributeType.Vector3 or AttributeType.Vector4 or AttributeType.Quaternion or AttributeType.QAngle;

        /// <summary>The type a slot stores values of <typeparamref name="T"/> as, or null when it is not one of the types stored inline.</summary>
        static AttributeType? KindOf<T>() where T : unmanaged
        {
            if (typeof(T) == typeof(int)) return AttributeType.Int;
            if (typeof(T) == typeof(float)) return AttributeType.Float;
            if (typeof(T) == typeof(bool)) return AttributeType.Bool;
            if (typeof(T) == typeof(byte)) return AttributeType.Byte;
            if (typeof(T) == typeof(ulong)) return AttributeType.UInt64;
            if (typeof(T) == typeof(TimeSpan)) return AttributeType.Time;
            if (typeof(T) == typeof(Color)) return AttributeType.Color;
            if (typeof(T) == typeof(Vector2)) return AttributeType.Vector2;
            if (typeof(T) == typeof(Vector3)) return AttributeType.Vector3;
            if (typeof(T) == typeof(Vector4)) return AttributeType.Vector4;
            if (typeof(T) == typeof(Quaternion)) return AttributeType.Quaternion;
            if (typeof(T) == typeof(QAngle)) return AttributeType.QAngle;
            return null;
        }

        static void WriteInline<T>(ref AttributeSlot slot, AttributeType kind, T value) where T : unmanaged
        {
            slot.Kind = kind;
            slot.Reference = null;
            slot.Inline = default;
            Unsafe.As<InlineValue, T>(ref slot.Inline) = value;
        }

        /// <summary>
        /// Stores a boxed value in a slot, taking ownership of elements and element arrays the way Valve's datamodel does.
        /// </summary>
        void Store(ref AttributeSlot slot, object? value)
        {
            switch (value)
            {
                case null:
                    slot.Kind = AttributeType.Element;
                    slot.Reference = null;
                    return;
                case int v: WriteInline(ref slot, AttributeType.Int, v); return;
                case float v: WriteInline(ref slot, AttributeType.Float, v); return;
                case bool v: WriteInline(ref slot, AttributeType.Bool, v); return;
                case byte v: WriteInline(ref slot, AttributeType.Byte, v); return;
                case ulong v: WriteInline(ref slot, AttributeType.UInt64, v); return;
                case TimeSpan v: WriteInline(ref slot, AttributeType.Time, v); return;
                case Color v: WriteInline(ref slot, AttributeType.Color, v); return;
                case Vector2 v: WriteInline(ref slot, AttributeType.Vector2, v); return;
                case Vector3 v: WriteInline(ref slot, AttributeType.Vector3, v); return;
                case Vector4 v: WriteInline(ref slot, AttributeType.Vector4, v); return;
                case Quaternion v: WriteInline(ref slot, AttributeType.Quaternion, v); return;
                case QAngle v: WriteInline(ref slot, AttributeType.QAngle, v); return;
                case Element elem:
                    if (elem.Owner == null)
                        elem.Owner = Owner;
                    else if (elem.Owner != Owner)
                        throw new ElementOwnershipException();
                    slot.Kind = AttributeType.Element;
                    break;
                case ElementArray array:
                    if (array.Owner == null)
                        array.Owner = this;
                    else if (array.Owner != this)
                        throw new InvalidOperationException("ElementArray is already owned by a different Datamodel.");
                    slot.Kind = AttributeType.Array;
                    break;
                case IEnumerable<Element>:
                    throw new InvalidOperationException("Element array objects must derive from Datamodel.ElementArray");
                case string:
                    slot.Kind = AttributeType.String;
                    break;
                case byte[]:
                    slot.Kind = AttributeType.Binary;
                    break;
                case Matrix4x4:
                    slot.Kind = AttributeType.Matrix;
                    break;
                default:
                    if (!Datamodel.IsDatamodelType(value.GetType()))
                        throw new AttributeTypeException($"{value.GetType().FullName} is not a valid Datamodel attribute type. (If this is an array, it must implement IList<T>).");
                    slot.Kind = AttributeType.Array;
                    break;
            }

            slot.Reference = value;
        }

        /// <summary>
        /// The value as an object, without loading a deferred value or expanding a stub.
        /// </summary>
        static object? RawValue(in AttributeSlot slot)
        {
            return slot.Kind switch
            {
                AttributeType.Element or AttributeType.String or AttributeType.Binary or AttributeType.Matrix or AttributeType.Array => slot.Reference,
                AttributeType.Deferred => null,
                AttributeType.Int => slot.Inline.Int,
                AttributeType.Float => slot.Inline.Float,
                AttributeType.Bool => slot.Inline.Bool,
                AttributeType.Byte => slot.Inline.Byte,
                AttributeType.UInt64 => slot.Inline.UInt64,
                AttributeType.Time => TimeSpan.FromTicks(slot.Inline.Ticks),
                AttributeType.Color => slot.Inline.Color,
                AttributeType.Vector2 => slot.Inline.Vector2,
                AttributeType.Vector3 => slot.Inline.Vector3,
                AttributeType.Vector4 => slot.Inline.Vector4,
                AttributeType.Quaternion => slot.Inline.Quaternion,
                AttributeType.QAngle => slot.Inline.QAngle,
                _ => throw new InvalidOperationException("Unknown attribute kind."),
            };
        }

        /// <summary>
        /// The value as an object, loading it from the stream if it is deferred and expanding a stub element.
        /// </summary>
        /// <exception cref="CodecException">Thrown when deferred value loading fails.</exception>
        /// <exception cref="DestubException">Thrown when Element destubbing fails.</exception>
        object? GetValue(int index)
        {
            Resolve(index);
            return RawValue(in slots![index]);
        }

        void LoadDeferred(int index)
        {
            var codec = Owner?.Codec ?? throw new CodecException("Trying to load a deferred Attribute, but could not find codec.");
            var offset = slots![index].Inline.Offset;
            var name = slots[index].Name;
            object? value;

            try
            {
                lock (codec)
                {
                    value = codec.DeferredDecodeAttribute(Owner, offset);
                }
            }
            catch (Exception err)
            {
                throw new CodecException($"Deferred loading of attribute \"{name}\" on element {(this as Element)?.ID} using {codec} codec threw an exception.", err);
            }

            Store(ref slots[index], value);
        }

        /// <summary>
        /// Registers an attribute whose value is read from the stream on first access.
        /// </summary>
        internal void SetDeferred(string name, long offset)
        {
            lock (Attribute_ChangeLock)
            {
                var index = Find(name);
                ref var slot = ref (index < 0 ? ref Append(name) : ref slots![index]);
                slot.Kind = AttributeType.Deferred;
                slot.Reference = null;
                slot.Override = null;
                slot.Inline = default;
                slot.Inline.Offset = offset;
            }
        }

        /// <summary>
        /// The reference held by every attribute, without loading deferred values. Value types are skipped, since only elements and arrays matter to callers.
        /// </summary>
        internal IEnumerable<object?> EnumerateReferences()
        {
            for (var i = 0; i < count; i++)
                yield return slots![i].Reference;
        }

        bool HasListeners => CollectionChanged != null || PropertyChanged != null;

        #endregion

        /// <summary>
        /// Adds a new attribute to this AttributeList.
        /// </summary>
        /// <param name="key">The name of the attribute. Must be unique to this AttributeList.</param>
        /// <param name="value">The value of the Attribute. Must be of a valid Datamodel type.</param>
        public void Add(string key, object? value)
        {
            this[key] = value;
        }

        /// <summary>
        /// Sets a value type attribute without boxing it. Any other type is stored through the indexer.
        /// </summary>
        public void Set<T>(string name, T value) where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(name);

            if (Schema.Properties.Count > 0 && Schema.GetProperty(name) is PropertyBinding binding)
            {
                // the generated binding of a class property takes the value as it is, so nothing is boxed on the way in
                if (binding.CanWrite && binding is PropertyBinding<T> typed)
                    typed.Set(this, value);
                else
                    SetProperty(binding, name, value);
                return;
            }

            if (KindOf<T>() is not AttributeType kind)
            {
                this[name] = value;
                return;
            }

            if (this is Element { Stub: true })
                throw new InvalidOperationException("Cannot set attributes on a stub element.");

            lock (Attribute_ChangeLock)
            {
                var index = Find(name);
                if (index < 0)
                {
                    WriteInline(ref Append(name), kind, value);

                    if (HasListeners)
                        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, new AttrKVP(name, value), count - 1));
                }
                else
                {
                    ref var slot = ref slots![index];
                    var old = HasListeners ? RawValue(in slot) : null;
                    slot.Override = null;
                    WriteInline(ref slot, kind, value);

                    if (HasListeners)
                        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, new AttrKVP(name, value), new AttrKVP(name, old), index));
                }
            }
        }

        /// <summary>
        /// Gets the given atttribute's "override type". This applies when multiple Datamodel types map to the same CLR type.
        /// </summary>
        /// <param name="key">The name of the attribute.</param>
        /// <returns>The attribute's Datamodel type, if different from its CLR type.</returns>
        public OverrideType? GetOverrideType(string key)
        {
            lock (Attribute_ChangeLock)
            {
                var index = Find(key);
                return index < 0 ? null : slots![index].Override;
            }
        }

        /// <summary>
        /// Sets the given attribute's "override type". This applies when multiple Datamodel types map to the same CLR type.
        /// </summary>
        /// <param name="key">The name of the attribute.</param>
        /// <param name="type">The Datamodel type which the attribute should be stored as when written to DMX, or null.</param>
        /// <exception cref="AttributeTypeException">Thrown when the attribute's CLR type does not map to the value given in <paramref name="type"/>.</exception>
        public void SetOverrideType(string key, OverrideType? type)
        {
            lock (Attribute_ChangeLock)
            {
                var index = Find(key);
                if (index < 0)
                    return;

                ref var slot = ref slots![index];
                switch (type)
                {
                    case null:
                        break;
                    case OverrideType.Angle:
                        if (slot.Kind != AttributeType.Vector3)
                            throw new AttributeTypeException("OverrideType.Angle can only be applied to Vector3 attributes");
                        break;
                    case OverrideType.Binary:
                        if (slot.Reference is not byte[])
                            throw new AttributeTypeException("OverrideType.Binary can only be applied to byte[] attributes");
                        break;
                    default:
                        throw new NotImplementedException();
                }

                slot.Override = type;
            }
        }

        public bool Remove(string key)
        {
            lock (Attribute_ChangeLock)
            {
                var index = Find(key);
                if (index < 0) return false;

                var removed = new AttrKVP(key, RawValue(in slots![index]));
                RemoveSlot(index);
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
                return true;
            }
        }

        /// <summary>
        /// Gets the value of an attribute without loading it if it is deferred, in which case the value is null.
        /// </summary>
        public bool TryGetValue(string key, out object? value)
        {
            lock (Attribute_ChangeLock)
            {
                var index = Find(key);
                if (index < 0)
                {
                    value = null;
                    return false;
                }

                ref var slot = ref slots![index];
                value = RawValue(in slot);
                return true;
            }
        }

        public virtual bool ContainsKey(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            lock (Attribute_ChangeLock)
                return Find(key) >= 0;
        }

        public ICollection<string> Keys
        {
            get
            {
                lock (Attribute_ChangeLock)
                {
                    var keys = new string[count];
                    for (var i = 0; i < count; i++)
                        keys[i] = slots![i].Name;
                    return keys;
                }
            }
        }

        public ICollection<object?> Values
        {
            get
            {
                var values = new object?[Count];
                for (var i = 0; i < values.Length; i++)
                    values[i] = GetValue(i);
                return values;
            }
        }

        /// <summary>
        /// Gets or sets the value of the attribute with the given name.
        /// </summary>
        /// <param name="name">The name to search for. Cannot be null.</param>
        /// <returns>The value associated with the given name.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the value of name is null.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when an attempt is made to get a name that is not present in this AttributeList.</exception>
        /// <exception cref="ElementOwnershipException">Thrown when an attempt is made to set the value of the attribute to an Element from a different <see cref="Datamodel"/>.</exception>
        /// <exception cref="AttributeTypeException">Thrown when an attempt is made to set a value that is not of a valid Datamodel attribute type.</exception>
        public virtual object? this[string name]
        {
            get
            {
                ArgumentNullException.ThrowIfNull(name);

                int index;
                lock (Attribute_ChangeLock)
                    index = Find(name);

                if (index < 0)
                {
                    var binding = Schema.GetProperty(name);
                    if (binding != null)
                    {
                        return binding.GetValue(this);
                    }

                    throw new KeyNotFoundException($"{this} does not have an attribute called \"{name}\"");
                }

                return GetValue(index);
            }
            set
            {
                ArgumentNullException.ThrowIfNull(name);

                // a value that fits a class property is a valid attribute type by construction, so it skips the type table
                var binding = Schema.Properties.Count > 0 ? Schema.GetProperty(name) : null;

                if (binding != null)
                {
                    SetProperty(binding, name, value);
                    return;
                }

                if (Owner != null && this == Owner.PrefixAttributes && value?.GetType() == typeof(Element))
                    throw new AttributeTypeException("Elements are not supported as prefix attributes.");

                lock (Attribute_ChangeLock)
                {
                    var index = Find(name);
                    if (index < 0)
                    {
                        Store(ref Append(name), value);

                        if (HasListeners)
                            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, new AttrKVP(name, value), count - 1));
                    }
                    else
                    {
                        ref var slot = ref slots![index];
                        var old = HasListeners ? RawValue(in slot) : null;
                        slot.Override = null;
                        Store(ref slot, value);

                        if (HasListeners)
                            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, new AttrKVP(name, value), new AttrKVP(name, old), index));
                    }
                }
            }
        }

        /// <summary>
        /// Assigns a value to the class property that stores the attribute.
        /// </summary>
        void SetProperty(PropertyBinding binding, string name, object? value)
        {
            if (binding.CanWrite)
            {
                // null is fine, it will just set the value to null; an exact type match is the common case and avoids the runtime cast check
                if (value != null && binding.PropertyType != value.GetType() && !binding.PropertyType.IsInstanceOfType(value))
                {
                    value = ConvertScalar(value, binding.PropertyType)
                        ?? throw new InvalidDataException($"class property '{Schema.ElementType.Name}.{binding.PropertyName}' with type '{binding.PropertyType}' can not hold a value of type '{value.GetType()}' (attribute '{name}'), this is likely a mismatch between the real class and the class from the datamodel");
                }

                binding.SetValue(this, value);
                return;
            }

            // a read-only array property takes the items of an incoming array of the same type, so that a file can fill it once
            var existingArray = binding.GetValue(this) as IList;
            var incomingArray = value as IList;

            if (existingArray is not null && incomingArray is not null && existingArray.GetType() == incomingArray.GetType())
            {
                if (existingArray.Count == 0)
                {
                    foreach (var item in incomingArray)
                        existingArray.Add(item);
                }
                else
                {
                    throw new InvalidOperationException($"Attribute '{name}' modifies property {Schema.ElementType.Name}.{binding.PropertyName}, which is read-only and already has items.");
                }
            }
            else
            {
                throw new InvalidDataException($"Property '{Schema.ElementType.Name}.{binding.PropertyName}' of deserialisation class must be writeable, make sure it has a setter");
            }
        }

        /// <summary>
        /// Converts between the bool, int and float attribute types the way Valve's datamodel does when a value is assigned
        /// to an attribute of another of those types. Returns null for any other combination.
        /// </summary>
        private static object? ConvertScalar(object value, Type targetType)
        {
            if (targetType == typeof(int))
            {
                return value switch
                {
                    bool b => b ? 1 : 0,
                    float f => (int)f,
                    _ => null,
                };
            }

            if (targetType == typeof(float))
            {
                return value switch
                {
                    bool b => b ? 1f : 0f,
                    int i => (float)i,
                    _ => null,
                };
            }

            if (targetType == typeof(bool))
            {
                return value switch
                {
                    int i => i != 0,
                    float f => f != 0f,
                    _ => null,
                };
            }

            return null;
        }

        /// <summary>
        /// Gets or sets the attribute at the given index.
        /// </summary>
        public AttrKVP this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)count, nameof(index));
                return new AttrKVP(slots![index].Name, GetValue(index));
            }
            set
            {
                lock (Attribute_ChangeLock)
                {
                    RemoveAt(index);
                    Store(ref InsertAt(index, value.Key), value.Value);
                    OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, value, index));
                }
            }
        }

        /// <summary>
        /// Removes the attribute at the given index.
        /// </summary>
        public void RemoveAt(int index)
        {
            AttrKVP removed;
            lock (Attribute_ChangeLock)
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)count, nameof(index));
                removed = new AttrKVP(slots![index].Name, RawValue(in slots[index]));
                RemoveSlot(index);
            }
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
        }

        public int IndexOf(string key)
        {
            lock (Attribute_ChangeLock)
                return Find(key);
        }

        /// <summary>
        /// Removes all Attributes from the Collection.
        /// </summary>
        public void Clear()
        {
            lock (Attribute_ChangeLock)
            {
                if (slots != null)
                    System.Array.Clear(slots, 0, count);
                count = 0;
            }
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public int Count
        {
            get
            {
                lock (Attribute_ChangeLock)
                    return count;
            }
        }

        public bool IsFixedSize { get { return false; } }
        public bool IsReadOnly { get { return false; } }
        public bool IsSynchronized { get { return true; } }
        /// <summary>
        /// Gets an object which can be used to synchronise access to the items within this AttributeCollection.
        /// </summary>
        public object SyncRoot { get { return Attribute_ChangeLock; } }

        /// <summary>
        /// Enumerates every attribute to be written by a codec: class properties first, in declaration order, followed by the plain attributes in the order they were added.
        /// </summary>
        public IEnumerable<AttrKVP> GetAllAttributesForSerialization()
        {
            foreach (var binding in Schema.Properties)
                yield return new AttrKVP(binding.AttributeName, binding.GetValue(this));

            foreach (var attr in this)
                yield return attr;
        }

        public IEnumerator<AttrKVP> GetEnumerator()
        {
            var pairs = new AttrKVP[Count];
            for (var i = 0; i < pairs.Length; i++)
                pairs[i] = new AttrKVP(slots![i].Name, GetValue(i));

            return ((IEnumerable<AttrKVP>)pairs).GetEnumerator();
        }

        /// <summary>
        /// Loads the value of a deferred slot and expands a stub, so that the slot holds its final value.
        /// </summary>
        void Resolve(int index)
        {
            if (slots![index].Kind == AttributeType.Deferred)
                LoadDeferred(index);

            ref var slot = ref slots[index];
            if (slot.Kind == AttributeType.Element && slot.Reference is Element { Stub: true } stub && Owner != null)
            {
                try { slot.Reference = Owner.OnStubRequest(stub.ID) ?? stub; }
                catch (Exception err) { throw new DestubException(this, slot.Name, err); }
            }
        }

        /// <summary>
        /// Passes every attribute a codec writes to the visitor in the form its slot holds it: class properties first, in declaration order, then the plain attributes in the order they were added.
        /// Deferred values are loaded and stubs expanded first, as <see cref="GetAllAttributesForSerialization"/> does, but no value is boxed and the lock is released before the visitor runs.
        /// </summary>
        internal void VisitAttributes<TVisitor>(ref TVisitor visitor) where TVisitor : struct, IAttributeVisitor
        {
            var properties = Schema.Properties;
            AttributeSlot[] copy;
            int copied;

            lock (Attribute_ChangeLock)
            {
                copied = count;
                copy = ArrayPool<AttributeSlot>.Shared.Rent(copied);
                for (var i = 0; i < copied; i++)
                {
                    Resolve(i);
                    copy[i] = slots![i];
                }
            }

            try
            {
                visitor.Begin(properties.Count + copied);

                foreach (var binding in properties)
                {
                    binding.Read(this, out var kind, out var inline, out var reference);
                    visitor.Visit(binding.AttributeName, kind, in inline, reference);
                }

                for (var i = 0; i < copied; i++)
                {
                    ref var slot = ref copy[i];
                    visitor.Visit(slot.Name, slot.Kind, in slot.Inline, slot.Reference);
                }
            }
            finally
            {
                System.Array.Clear(copy, 0, copied);
                ArrayPool<AttributeSlot>.Shared.Return(copy);
            }
        }

        /// <summary>
        /// Splits a boxed value into the form a slot stores it in. Anything that is not a scalar, vector, element, string, blob or matrix counts as an array, whether or not it is a valid attribute value.
        /// </summary>
        internal static void Classify(object? value, out AttributeType kind, out InlineValue inline, out object? reference)
        {
            inline = default;
            reference = null;
            switch (value)
            {
                case int v: kind = AttributeType.Int; inline.Int = v; return;
                case float v: kind = AttributeType.Float; inline.Float = v; return;
                case bool v: kind = AttributeType.Bool; inline.Bool = v; return;
                case byte v: kind = AttributeType.Byte; inline.Byte = v; return;
                case ulong v: kind = AttributeType.UInt64; inline.UInt64 = v; return;
                case TimeSpan v: kind = AttributeType.Time; inline.Ticks = v.Ticks; return;
                case Color v: kind = AttributeType.Color; inline.Color = v; return;
                case Vector2 v: kind = AttributeType.Vector2; inline.Vector2 = v; return;
                case Vector3 v: kind = AttributeType.Vector3; inline.Vector3 = v; return;
                case Vector4 v: kind = AttributeType.Vector4; inline.Vector4 = v; return;
                case Quaternion v: kind = AttributeType.Quaternion; inline.Quaternion = v; return;
                case QAngle v: kind = AttributeType.QAngle; inline.QAngle = v; return;
                case null: kind = AttributeType.Element; return;
                case Element: kind = AttributeType.Element; reference = value; return;
                case string: kind = AttributeType.String; reference = value; return;
                case byte[]: kind = AttributeType.Binary; reference = value; return;
                case Matrix4x4: kind = AttributeType.Matrix; reference = value; return;
                default: kind = AttributeType.Array; reference = value; return;
            }
        }

        /// <summary>
        /// The kind a slot stores values of the given type as: inline for the scalar and vector types, a reference for everything else.
        /// </summary>
        internal static AttributeType? KindOf(Type type)
        {
            if (type == typeof(int)) return AttributeType.Int;
            if (type == typeof(float)) return AttributeType.Float;
            if (type == typeof(bool)) return AttributeType.Bool;
            if (type == typeof(byte)) return AttributeType.Byte;
            if (type == typeof(ulong)) return AttributeType.UInt64;
            if (type == typeof(TimeSpan)) return AttributeType.Time;
            if (type == typeof(Color)) return AttributeType.Color;
            if (type == typeof(Vector2)) return AttributeType.Vector2;
            if (type == typeof(Vector3)) return AttributeType.Vector3;
            if (type == typeof(Vector4)) return AttributeType.Vector4;
            if (type == typeof(Quaternion)) return AttributeType.Quaternion;
            if (type == typeof(QAngle)) return AttributeType.QAngle;
            return null;
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
        /// Raised when an attribute is added, removed, or replaced.
        /// </summary>
        /// <remarks>Only raised while a handler is attached to this event or to <see cref="PropertyChanged"/>.</remarks>
        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        protected virtual void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
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
                var index = Find(item.Key);
                if (index < 0 || !Equals(GetValue(index), item.Value)) return false;
                return Remove(item.Key);
            }
        }

        void ICollection<AttrKVP>.CopyTo(AttrKVP[] array, int arrayIndex)
        {
            ((ICollection)this).CopyTo(array, arrayIndex);
        }

        void ICollection.CopyTo(Array array, int index)
        {
            foreach (var pair in this)
            {
                array.SetValue(pair, index);
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
                var index = Find(item.Key);
                return index >= 0 && Equals(GetValue(index), item.Value);
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
