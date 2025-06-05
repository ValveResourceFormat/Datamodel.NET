using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace Datamodel
{
    [DebuggerTypeProxy(typeof(Array<>.DebugView))]
    [DebuggerDisplay("Count = {Inner.Count}")]
    public abstract class Array<T> : IList<T>, IList
    {
        internal class DebugView(Array<T> arr)
        {
            readonly Array<T> Arr = arr;

            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public T[] Items { get { return [.. Arr.Inner]; } }
        }

        protected List<T> Inner;

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
            Inner = [];
        }

        internal Array(IEnumerable<T> enumerable)
        {
            if (enumerable != null)
                Inner = [.. enumerable];
            else
                Inner = [];
        }

        internal Array(int capacity)
        {
            Inner = new List<T>(capacity);
        }

        public int IndexOf(T item) => Inner.IndexOf(item);

        public void Insert(int index, T item) => Insert_Internal(index, item);
        protected virtual void Insert_Internal(int index, T item) => Inner.Insert(index, item);

        public void AddRange(IEnumerable<T> items) => Inner.AddRange(items);

        public void RemoveAt(int index) => Inner.RemoveAt(index);

        public virtual T this[int index]
        {
            get => Inner[index];
            set => Inner[index] = value;
        }

        public void Add(T item) => Insert(Inner.Count, item);

        public void Clear() => Inner.Clear();

        public bool Contains(T item) => Inner.Contains(item);

        public void CopyTo(T[] array, int offset)
        {
            CopyTo_Internal(array, offset);
        }

        protected virtual void CopyTo_Internal(T[] array, int offset) => Inner.CopyTo(array, offset);

        public int Count => Inner.Count;

        bool ICollection<T>.IsReadOnly { get { return false; } }

        public bool IsFixedSize => throw new NotImplementedException();

        public bool IsReadOnly => throw new NotImplementedException();

        public bool IsSynchronized => throw new NotImplementedException();

        public object SyncRoot => throw new NotImplementedException();

        object? IList.this[int index] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public bool Remove(T item) => Inner.Remove(item);

        public IEnumerator<T> GetEnumerator() => Inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => Inner.GetEnumerator();

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
        /// Gets the values in the list without attempting destubbing.
        /// </summary>
        internal IEnumerable<Element> RawList { get { foreach (var elem in Inner) yield return elem; } }

        public override AttributeList? Owner
        {
            get => base.Owner;
            internal set
            {
                base.Owner = value;

                if (OwnerDatamodel != null)
                {
                    for (int i = 0; i < Count; i++)
                    {
                        var elem = Inner[i];

                        if (elem == null) continue;
                        if (elem.Owner == null)
                        {
                            var importedElement = OwnerDatamodel.ImportElement(elem, Datamodel.ImportRecursionMode.Stubs, Datamodel.ImportOverwriteMode.Stubs);

                            if (importedElement is not null)
                            {
                                Inner[i] = importedElement;
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
                var elem = Inner[index];
                if (elem != null && elem.Stub && elem.Owner != null)
                {
                    try
                    {
                        elem = Inner[index] = elem.Owner.OnStubRequest(elem.ID)!;
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
    }
}
