using Datamodel.Codecs;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Datamodel
{
    public partial class Datamodel
    {
        /// <summary>
        /// A collection of <see cref="Element"/>s owned by a single <see cref="Datamodel"/>.
        /// </summary>
        [DebuggerDisplay("Count = {Count}")]
        [DebuggerTypeProxy(typeof(DebugView))]
        public class ElementList : IEnumerable<Element>, INotifyCollectionChanged, IDisposable
        {
            internal ReaderWriterLockSlim ChangeLock = new(LockRecursionPolicy.SupportsRecursion);

            internal class DebugView
            {
                public DebugView(ElementList item)
                {
                    Item = item;
                }

                private readonly ElementList Item;

                [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
                public Element[] Elements => Item.store.Values.Cast<Element>().ToArray();
            }

            private readonly OrderedDictionary store = [];
            private readonly Datamodel Owner;

            internal ElementList(Datamodel owner)
            {
                Owner = owner;
            }

            internal void Add(Element item)
            {
                ChangeLock.EnterUpgradeableReadLock();
                try
                {
                    Element? existing = (Element?)store[item.ID];
                    if (existing != null && !existing.Stub)
                    {
                        throw new ElementIdException($"Element ID {item.ID} already in use in this Datamodel.");
                    }

                    Debug.Assert(item.Owner != null);
                    if (item.Owner != this.Owner)
                        throw new ElementOwnershipException("Cannot add an element from a different Datamodel. Use ImportElement() to create a local copy instead.");

                    ChangeLock.EnterWriteLock();
                    try
                    {
                        if (existing != null)
                            store.Remove(existing.ID);

                        store.Add(item.ID, item);
                    }
                    finally
                    {
                        ChangeLock.ExitWriteLock();
                    }
                }
                finally
                {
                    ChangeLock.ExitUpgradeableReadLock();
                }

                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item));
            }

            /// <summary>
            /// Returns the <see cref="Element"/> at the specified index.
            /// </summary>
            /// <remarks>The order of this list has no meaning to a Datamodel. This accessor is intended for <see cref="ICodec"/> implementers.</remarks>
            /// <param name="index">The index to look up.</param>
            /// <returns>The Element found at the index.</returns>
            public Element? this[int index]
            {
                get
                {
                    ChangeLock.EnterReadLock();
                    try
                    {
                        return (Element?)store[index];
                    }
                    finally { ChangeLock.ExitReadLock(); }
                }
            }

            /// <summary>
            /// Searches the collection for an <see cref="Element"/> with the specified <see cref="Element.ID"/>.
            /// </summary>
            /// <param name="id">The ID to search for.</param>
            /// <returns>The Element with the given ID, or null if none is found.</returns>
            public Element? this[Guid id]
            {
                get
                {
                    ChangeLock.EnterReadLock();
                    try
                    {
                        return (Element?)store[id];
                    }
                    finally { ChangeLock.ExitReadLock(); }
                }
            }

            /// <summary>
            /// Gets the number of <see cref="Element"/>s in this collection.
            /// </summary>
            public int Count
            {
                get
                {
                    ChangeLock.EnterReadLock();
                    try
                    {
                        return store.Count;
                    }
                    finally { ChangeLock.ExitReadLock(); }
                }
            }

            internal bool Contains(Element elem)
            {
                return store.Contains(elem);
            }

            /// <summary>
            /// Specifies a behaviour for removing references to an <see cref="Element"/> from other Elements in a <see cref="Datamodel"/>.
            /// </summary>
            public enum RemoveMode
            {
                /// <summary>
                /// Attribute values pointing to the removed Element become stubs.
                /// </summary>
                MakeStubs,
                /// <summary>
                /// Attribute values pointing to the removed Element become null.
                /// </summary>
                MakeNulls
            }
            /// <summary>
            /// Removes an <see cref="Element"/> from the Datamodel.
            /// </summary>
            /// <remarks>This method will access all values in the Datamodel, potentially triggering deferred loading and destubbing.</remarks>
            /// <param name="item">The Element to remove.</param>
            /// <param name="mode">The action to take if a reference to this Element is found in another Element.</param>
            /// <returns>true if item is successfully removed; otherwise, false.</returns>
            public bool Remove(Element item, RemoveMode mode)
            {
                ArgumentNullException.ThrowIfNull(item);

                ChangeLock.EnterUpgradeableReadLock();
                try
                {
                    if (store.Contains(item.ID))
                    {
                        ChangeLock.EnterWriteLock();
                        try
                        {
                            store.Remove(item.ID);
                            Element? replacement = (mode == RemoveMode.MakeStubs) ? new Element(Owner, item.ID) : null;

                            foreach (Element elem in store.Values)
                            {
                                lock (elem.SyncRoot)
                                {
                                    foreach (var attr in elem.Where(a => a.Value == item).ToArray())
                                    {
                                        elem[attr.Key] = replacement;
                                    }

                                    foreach (var array in elem.Select(a => a.Value).OfType<IList<Element?>>())
                                        for (int i = 0; i < array.Count; i++)
                                            if (array[i] == item)
                                                array[i] = replacement;
                                }
                            }
                            if (Owner.Root == item) Owner.Root = replacement;

                            item.Owner = null;
                        }
                        finally
                        {
                            ChangeLock.ExitWriteLock();
                        }

                        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item));
                        return true;
                    }
                    else return false;
                }
                finally { ChangeLock.ExitUpgradeableReadLock(); }
            }

            /// <summary>
            /// Removes unreferenced Elements from the Datamodel.
            /// </summary>
            public void Trim()
            {
                ChangeLock.EnterUpgradeableReadLock();
                try
                {
                    var used = new HashSet<Element?>();
                    WalkElemTree(Owner.Root, used);
                    if (used.Count == Count) return;

                    ChangeLock.EnterWriteLock();
                    try
                    {
                        foreach (var elem in this.Except(used).ToArray())
                        {
                            if (elem != null)
                            {
                                store.Remove(elem.ID);
                                elem.Owner = null;
                            }
                        }

                        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, (System.Collections.IList)used));
                    }
                    finally
                    {
                        ChangeLock.ExitWriteLock();
                    }
                }
                finally
                {
                    ChangeLock.ExitUpgradeableReadLock();
                }
            }

            protected void WalkElemTree(Element? elem, HashSet<Element?> found)
            {
                if (elem is null)
                {
                    return;
                }

                found.Add(elem);
                foreach (var value in elem.Inner.Values.Cast<Attribute>().Select(a => a.RawValue))
                {
                    if (value is Element value_elem)
                    {
                        if (found.Add(value_elem))
                        {
                            WalkElemTree(value_elem, found);
                        }

                        continue;
                    }
                    if (value is ElementArray elem_array)
                    {
                        foreach (var item in elem_array.RawList)
                        {
                            if (item != null && found.Add(item))
                            {
                                WalkElemTree(item, found);
                            }
                        }
                    }
                }
            }

            #region Interfaces
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return store.GetEnumerator();
            }
            /// <summary>
            /// Returns an Enumerator that iterates through the Elements in collection.
            /// </summary>
            public IEnumerator<Element> GetEnumerator()
            {
                foreach (Element elem in store.Values)
                    yield return elem;
            }
            /// <summary>
            /// Raised when an <see cref="Element"/> is added, removed, or replaced.
            /// </summary>
            public event NotifyCollectionChangedEventHandler? CollectionChanged;
            #endregion

            public void Dispose()
            {
                ChangeLock.Dispose();
            }
        }
    }
}
