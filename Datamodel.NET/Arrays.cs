using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Datamodel
{
    /// <summary>
    /// A typed attribute array. Items are stored contiguously, either in a private buffer or, for arrays read from a file,
    /// in a slice of a chunk shared with the other arrays of that file; the first change that needs more room moves the array to a private buffer.
    /// </summary>
    [DebuggerTypeProxy(typeof(Array<>.DebugView))]
    [DebuggerDisplay("Count = {Count}")]
    public abstract class Array<T> : IList<T>, IList
    {
        internal class DebugView(Array<T> arr)
        {
            readonly Array<T> Arr = arr;

            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public T[] Items { get { return Arr.AsSpan().ToArray(); } }
        }

        T[] buffer;
        int offset;
        int count;
        int capacity;
        bool shared;

        public virtual AttributeList? Owner
        {
            get => _Owner;
            internal set
            {
                _Owner = value;
            }
        }
        AttributeList? _Owner;

        protected Datamodel? OwnerDatamodel => Owner?.Owner;

        internal Array()
        {
            buffer = [];
        }

        internal Array(IEnumerable<T> enumerable)
        {
            buffer = enumerable is null ? [] : [.. enumerable];
            count = capacity = buffer.Length;
        }

        internal Array(int capacity)
        {
            buffer = capacity > 0 ? new T[capacity] : [];
            this.capacity = capacity;
        }

        /// <summary>
        /// Creates an array over a slice of a chunk shared with other arrays. The slice belongs to this array alone, but cannot grow in place.
        /// </summary>
        internal Array(T[] buffer, int offset, int count)
        {
            this.buffer = buffer;
            this.offset = offset;
            this.count = count;
            capacity = count;
            shared = true;
        }

        /// <summary>
        /// Gets the items as a span. The span is invalidated by any change to the array.
        /// </summary>
        public ReadOnlySpan<T> AsSpan() => new(buffer, offset, count);

        /// <summary>
        /// The items, writable. Invalidated by any change to the array.
        /// </summary>
        protected Span<T> Items => new(buffer, offset, count);

        /// <summary>
        /// Moves the items to a private buffer with room for at least <paramref name="minimum"/> items.
        /// </summary>
        void Grow(int minimum)
        {
            var newCapacity = Math.Max(minimum, Math.Max(capacity * 2, 4));
            var newBuffer = new T[newCapacity];
            Items.CopyTo(newBuffer);
            buffer = newBuffer;
            offset = 0;
            capacity = newCapacity;
            shared = false;
        }

        public int IndexOf(T item)
        {
            var index = System.Array.IndexOf(buffer, item, offset, count);
            return index < 0 ? -1 : index - offset;
        }

        public void Insert(int index, T item) => Insert_Internal(index, item);

        protected virtual void Insert_Internal(int index, T item)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)count, nameof(index));

            if (count == capacity)
                Grow(count + 1);

            if (index < count)
                System.Array.Copy(buffer, offset + index, buffer, offset + index + 1, count - index);

            buffer[offset + index] = item;
            count++;
        }

        public void AddRange(IEnumerable<T> items)
        {
            ArgumentNullException.ThrowIfNull(items);

            if (items is ICollection<T> collection)
            {
                if (count + collection.Count > capacity)
                    Grow(count + collection.Count);

                collection.CopyTo(buffer, offset + count);
                count += collection.Count;
                return;
            }

            foreach (var item in items)
                Add(item);
        }

        public void RemoveAt(int index)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)count, nameof(index));

            count--;
            if (index < count)
                System.Array.Copy(buffer, offset + index + 1, buffer, offset + index, count - index);

            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                buffer[offset + count] = default!;
        }

        public virtual T this[int index]
        {
            get => Items[index];
            set => Items[index] = value;
        }

        public void Add(T item) => Insert(count, item);

        public void Clear()
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                Items.Clear();

            count = 0;
        }

        public bool Contains(T item) => IndexOf(item) >= 0;

        public void CopyTo(T[] array, int offset)
        {
            CopyTo_Internal(array, offset);
        }

        protected virtual void CopyTo_Internal(T[] array, int offset) => Items.CopyTo(array.AsSpan(offset));

        public int Count => count;

        bool ICollection<T>.IsReadOnly { get { return false; } }

        public bool IsFixedSize => false;

        public bool IsReadOnly => false;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        object? IList.this[int index]
        {
            get => this[index];
            set => this[index] = value is null ? throw new InvalidOperationException("Trying to set a null object") : (T)value;
        }

        public bool Remove(T item)
        {
            var index = IndexOf(item);
            if (index < 0)
                return false;

            RemoveAt(index);
            return true;
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < count; i++)
                yield return buffer[offset + i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        #region IList
        int IList.Add(object? value)
        {
            if (value is not null)
                Add((T)value);
            return Count;
        }

        bool IList.Contains(object? value)
        {
            if (value is null)
                return false;
            return Contains((T)value);
        }

        int IList.IndexOf(object? value)
        {
            if (value is null)
                throw new InvalidOperationException("Trying to get the index of a null object");

            return IndexOf((T)value);
        }

        void IList.Insert(int index, object? value)
        {
            if (value is null)
                throw new InvalidOperationException("Trying to insert a null object");

            Insert(index, (T)value);
        }

        bool IList.IsFixedSize { get { return false; } }
        bool IList.IsReadOnly { get { return false; } }

        void IList.Remove(object? value)
        {
            if (value is null)
                throw new InvalidOperationException("Trying to remove a null object");

            Remove((T)value);
        }

        void ICollection.CopyTo(Array array, int index)
        {
            CopyTo((T[])array, index);
        }

        #endregion IList
    }

    /// <summary>
    /// Hands out slices of large shared arrays to the arrays read from one file, so that the collector sees a few large objects
    /// that it never moves, instead of hundreds of thousands of small ones that it copies through every generation.
    /// </summary>
    sealed class ArrayChunks
    {
        /// <summary>Chunk size in bytes. Above the large object threshold, so that chunks are never compacted.</summary>
        const int ChunkBytes = 1 << 20;

        /// <summary>Arrays at least this large get their own allocation, which lands on the large object heap anyway.</summary>
        const int LargeObjectBytes = 85_000;

        sealed class Chunk<T>
        {
            public T[] Buffer = [];
            public int Used;
        }

        readonly Dictionary<Type, object> chunks = [];

        /// <summary>
        /// Returns storage for <paramref name="count"/> items. The contents are not zeroed.
        /// </summary>
        public (T[] Buffer, int Offset) Rent<T>(int count) where T : unmanaged
        {
            var itemSize = Unsafe.SizeOf<T>();

            if ((long)count * itemSize >= LargeObjectBytes)
                return (GC.AllocateUninitializedArray<T>(count), 0);

            if (!chunks.TryGetValue(typeof(T), out var untyped))
            {
                untyped = new Chunk<T>();
                chunks[typeof(T)] = untyped;
            }

            var chunk = (Chunk<T>)untyped;
            if (chunk.Buffer.Length - chunk.Used < count)
            {
                chunk.Buffer = GC.AllocateUninitializedArray<T>(Math.Max(ChunkBytes / itemSize, count));
                chunk.Used = 0;
            }

            var offset = chunk.Used;
            chunk.Used += count;
            return (chunk.Buffer, offset);
        }
    }

    public class ElementArray : Array<Element>
    {
        public ElementArray() { }

        public ElementArray(IEnumerable<Element> enumerable)
            : base(enumerable)
        { }

        public ElementArray(int capacity)
            : base(capacity)
        { }

        /// <summary>
        /// Gets the items without attempting destubbing.
        /// </summary>
        internal ReadOnlySpan<Element> RawItems => AsSpan();

        public override AttributeList? Owner
        {
            get => base.Owner;
            internal set
            {
                base.Owner = value;

                if (OwnerDatamodel != null)
                {
                    var items = Items;
                    for (int i = 0; i < items.Length; i++)
                    {
                        var elem = items[i];

                        if (elem == null) continue;
                        if (elem.Owner == null)
                        {
                            var importedElement = OwnerDatamodel.ImportElement(elem, Datamodel.ImportRecursionMode.Stubs, Datamodel.ImportOverwriteMode.Stubs);

                            if (importedElement is not null)
                            {
                                items[i] = importedElement;
                            }
                        }
                        else if (elem.Owner != OwnerDatamodel)
                            throw new ElementOwnershipException();
                    }
                }
            }
        }

        protected override void Insert_Internal(int index, Element item)
        {
            if (item != null && OwnerDatamodel != null)
            {
                if (item.Owner == null)
                {
                    var importedElement = OwnerDatamodel.ImportElement(item, Datamodel.ImportRecursionMode.Recursive, Datamodel.ImportOverwriteMode.Stubs);

                    if (importedElement is not null)
                    {
                        item = importedElement;
                    }
                }
                else if (item.Owner != OwnerDatamodel)
                {
                    throw new ElementOwnershipException();
                }
            }

            base.Insert_Internal(index, item!);
        }

        public override Element this[int index]
        {
            get
            {
                var items = Items;
                var elem = items[index];
                if (elem != null && elem.Stub && elem.Owner != null)
                {
                    try
                    {
                        elem = items[index] = elem.Owner.OnStubRequest(elem.ID)!;
                    }
                    catch (Exception err)
                    {
                        throw new DestubException(this, index, err);
                    }
                }

                if (elem is null)
                {
                    throw new InvalidOperationException("Element at specified index is null");
                }

                return elem;
            }
            set => base[index] = value;
        }
    }

    public class IntArray : Array<int>
    {
        public IntArray() { }
        public IntArray(IEnumerable<int> enumerable)
            : base(enumerable)
        { }
        public IntArray(int capacity)
            : base(capacity)
        { }
        internal IntArray(int[] buffer, int offset, int count)
            : base(buffer, offset, count)
        { }
    }

    public class FloatArray : Array<float>
    {
        public FloatArray() { }
        public FloatArray(IEnumerable<float> enumerable)
            : base(enumerable)
        { }
        public FloatArray(int capacity)
            : base(capacity)
        { }
        internal FloatArray(float[] buffer, int offset, int count)
            : base(buffer, offset, count)
        { }
    }

    public class BoolArray : Array<bool>
    {
        public BoolArray() { }
        public BoolArray(IEnumerable<bool> enumerable)
            : base(enumerable)
        { }
        public BoolArray(int capacity)
            : base(capacity)
        { }
        internal BoolArray(bool[] buffer, int offset, int count)
            : base(buffer, offset, count)
        { }
    }

    public class StringArray : Array<string>
    {
        public StringArray() { }
        public StringArray(IEnumerable<string> enumerable)
            : base(enumerable)
        { }
        public StringArray(int capacity)
            : base(capacity)
        { }
    }

    public class BinaryArray : Array<byte[]>
    {
        public BinaryArray() { }
        public BinaryArray(IEnumerable<byte[]> enumerable)
            : base(enumerable)
        { }
        public BinaryArray(int capacity)
            : base(capacity)
        { }
    }

    public class TimeSpanArray : Array<TimeSpan>
    {
        public TimeSpanArray() { }
        public TimeSpanArray(IEnumerable<TimeSpan> enumerable)
            : base(enumerable)
        { }
        public TimeSpanArray(int capacity)
            : base(capacity)
        { }
    }

    public class ColorArray : Array<Color>
    {
        public ColorArray() { }
        public ColorArray(IEnumerable<Color> enumerable)
            : base(enumerable)
        { }
        public ColorArray(int capacity)
            : base(capacity)
        { }
        internal ColorArray(Color[] buffer, int offset, int count)
            : base(buffer, offset, count)
        { }
    }

    public class Vector2Array : Array<Vector2>
    {
        public Vector2Array() { }
        public Vector2Array(IEnumerable<Vector2> enumerable)
            : base(enumerable)
        { }
        public Vector2Array(int capacity)
            : base(capacity)
        { }
        internal Vector2Array(Vector2[] buffer, int offset, int count)
            : base(buffer, offset, count)
        { }
    }

    public class Vector3Array : Array<Vector3>
    {
        public Vector3Array() { }
        public Vector3Array(IEnumerable<Vector3> enumerable)
            : base(enumerable)
        { }
        public Vector3Array(int capacity)
            : base(capacity)
        { }
        internal Vector3Array(Vector3[] buffer, int offset, int count)
            : base(buffer, offset, count)
        { }
    }

    public class Vector4Array : Array<Vector4>
    {
        public Vector4Array() { }
        public Vector4Array(IEnumerable<Vector4> enumerable)
            : base(enumerable)
        { }
        public Vector4Array(int capacity)
            : base(capacity)
        { }
        internal Vector4Array(Vector4[] buffer, int offset, int count)
            : base(buffer, offset, count)
        { }
    }

    public class QuaternionArray : Array<Quaternion>
    {
        public QuaternionArray() { }
        public QuaternionArray(IEnumerable<Quaternion> enumerable)
            : base(enumerable)
        { }
        public QuaternionArray(int capacity)
            : base(capacity)
        { }
        internal QuaternionArray(Quaternion[] buffer, int offset, int count)
            : base(buffer, offset, count)
        { }
    }

    public class MatrixArray : Array<Matrix4x4>
    {
        public MatrixArray() { }
        public MatrixArray(IEnumerable<Matrix4x4> enumerable)
            : base(enumerable)
        { }
        public MatrixArray(int capacity)
            : base(capacity)
        { }
        internal MatrixArray(Matrix4x4[] buffer, int offset, int count)
            : base(buffer, offset, count)
        { }
    }

    public class ByteArray : Array<byte>
    {
        public ByteArray() { }
        public ByteArray(IEnumerable<byte> enumerable)
            : base(enumerable)
        { }
        public ByteArray(int capacity)
            : base(capacity)
        { }
        internal ByteArray(byte[] buffer, int offset, int count)
            : base(buffer, offset, count)
        { }
    }

    [CLSCompliant(false)]
    public class UInt64Array : Array<ulong>
    {
        public UInt64Array() { }
        public UInt64Array(IEnumerable<ulong> enumerable)
            : base(enumerable)
        { }
        public UInt64Array(int capacity)
            : base(capacity)
        { }
        internal UInt64Array(ulong[] buffer, int offset, int count)
            : base(buffer, offset, count)
        { }
    }
}
