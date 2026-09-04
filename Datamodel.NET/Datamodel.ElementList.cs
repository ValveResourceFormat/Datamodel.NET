using Datamodel.Codecs;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;

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
            internal readonly object ChangeLock = new();

            internal class DebugView
            {
                public DebugView(ElementList item)
                {
                    Item = item;
                }

                private readonly ElementList Item;

                [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
                public Element[] Elements => [.. Item.order];
            }

            // elements by ID, and in the order they were added, which codecs rely on
            private readonly Dictionary<Guid, Element> byId = [];
            private readonly List<Element> order = [];
            private readonly Datamodel Owner;

            internal ElementList(Datamodel owner)
            {
                Owner = owner;
            }

            /// <summary>
            /// Adds an Element owned by this list's Datamodel. The first Element added becomes the <see cref="Datamodel.Root"/>.
            /// </summary>
            internal void Add(Element item)
            {
                bool first;

                lock (ChangeLock)
                {
                    if (byId.TryGetValue(item.ID, out var existing) && !existing.Stub)
                    {
                        throw new ElementIdException($"Element ID {item.ID} already in use in this Datamodel.");
                    }

                    Debug.Assert(item.Owner != null);
                    if (item.Owner != this.Owner)
                        throw new ElementOwnershipException("Cannot add an element from a different Datamodel. Use ImportElement() to create a local copy instead.");

                    if (existing != null)
                        RemoveFromStore(existing);

                    byId.Add(item.ID, item);
                    order.Add(item);
                    first = order.Count == 1;
                }

                if (first)
                    Owner.Root = item;

                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item));
            }

            /// <summary>
            /// Removes an Element from both stores. The caller holds <see cref="ChangeLock"/>.
            /// </summary>
            void RemoveFromStore(Element item)
            {
                byId.Remove(item.ID);
                order.Remove(item);
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
                    lock (ChangeLock)
                        return order[index];
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
                    lock (ChangeLock)
                        return byId.TryGetValue(id, out var element) ? element : null;
                }
            }

            /// <summary>
            /// Gets the number of <see cref="Element"/>s in this collection.
            /// </summary>
            public int Count
            {
                get
                {
                    lock (ChangeLock)
                        return order.Count;
                }
            }

            /// <summary>Determines whether the given <see cref="Element"/> is registered in this collection.</summary>
            internal bool Contains(Element elem)
            {
                return this[elem.ID] == elem;
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

                lock (ChangeLock)
                {
                    if (!byId.ContainsKey(item.ID))
                        return false;

                    RemoveFromStore(item);
                    Element? replacement = (mode == RemoveMode.MakeStubs) ? new Element(Owner, item.ID) : null;

                    foreach (var elem in order)
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

                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item));
                return true;
            }

            /// <summary>
            /// Removes an <see cref="Element"/> which the caller knows is not referenced by any other Element, without scanning the Datamodel.
            /// </summary>
            internal void RemoveUnreferenced(Element item)
            {
                lock (ChangeLock)
                {
                    if (!byId.ContainsKey(item.ID))
                    {
                        return;
                    }

                    RemoveFromStore(item);
                    item.Owner = null;
                }

                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item));
            }

            /// <summary>
            /// Removes unreferenced Elements from the Datamodel.
            /// </summary>
            public void Trim()
            {
                lock (ChangeLock)
                {
                    var used = new HashSet<Element?>();
                    WalkElemTree(Owner.Root, used);
                    if (used.Count == order.Count) return;

                    var removed = order.Where(elem => !used.Contains(elem)).ToArray();
                    foreach (var elem in removed)
                    {
                        RemoveFromStore(elem);
                        elem.Owner = null;
                    }

                    CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed));
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
                return GetEnumerator();
            }
            /// <summary>
            /// Returns an Enumerator that iterates through the Elements in collection, in the order they were added.
            /// </summary>
            public IEnumerator<Element> GetEnumerator()
            {
                return order.GetEnumerator();
            }
            /// <summary>
            /// Raised when an <see cref="Element"/> is added, removed, or replaced.
            /// </summary>
            public event NotifyCollectionChangedEventHandler? CollectionChanged;
            #endregion

            public void Dispose()
            {
            }
        }
    }
}
