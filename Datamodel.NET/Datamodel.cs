using Datamodel.Codecs;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security;
using System.Numerics;
using System.Collections.Concurrent;
using CodecRegistration = System.Tuple<string, int>;

[assembly: System.CLSCompliant(true)]

namespace Datamodel
{
    /// <summary>
    /// Represents a thread-safe tree of <see cref="Element"/>s.
    /// </summary>
    [DebuggerDisplay("Format = {Format,nq} {FormatVersion}, Encoding = {Encoding,nq} {EncodingVersion}")]
    [DebuggerTypeProxy(typeof(DebugView))]
    public partial class Datamodel : INotifyPropertyChanged, IDisposable, ISupportInitialize
    {
        internal class DebugView
        {
            public DebugView(Datamodel dm)
            {
                DM = dm;
            }

            readonly Datamodel DM;

            public Element? Root => DM.Root;
            public ElementList AllElements => DM.AllElements;
            public AttributeList PrefixAttributes => DM.PrefixAttributes;
        }

        #region Attribute types

        private static readonly Type[] attributeTypes = [
            typeof(Element),
            typeof(int),
            typeof(float),
            typeof(bool),
            typeof(string),
            typeof(byte[]),
            typeof(TimeSpan),
            typeof(Color),
            typeof(Vector2),
            typeof(Vector3),
            typeof(Vector4),
            typeof(Quaternion),
            typeof(Matrix4x4),
            typeof(byte),
            typeof(ulong),
            typeof(QAngle),
        ];

        public static Type[] AttributeTypes => attributeTypes;

        /// <summary>
        /// The <see cref="IList{T}"/> interface of every attribute type, paired with T. A type implementing one of these is an array of T.
        /// </summary>
        private static readonly (Type List, Type Item)[] arrayInterfaces = [
            (typeof(IList<Element>), typeof(Element)),
            (typeof(IList<int>), typeof(int)),
            (typeof(IList<float>), typeof(float)),
            (typeof(IList<bool>), typeof(bool)),
            (typeof(IList<string>), typeof(string)),
            (typeof(IList<byte[]>), typeof(byte[])),
            (typeof(IList<TimeSpan>), typeof(TimeSpan)),
            (typeof(IList<Color>), typeof(Color)),
            (typeof(IList<Vector2>), typeof(Vector2)),
            (typeof(IList<Vector3>), typeof(Vector3)),
            (typeof(IList<Vector4>), typeof(Vector4)),
            (typeof(IList<Quaternion>), typeof(Quaternion)),
            (typeof(IList<Matrix4x4>), typeof(Matrix4x4)),
            (typeof(IList<byte>), typeof(byte)),
            (typeof(IList<ulong>), typeof(ulong)),
            (typeof(IList<QAngle>), typeof(QAngle)),
        ];

        /// <summary>
        /// The item type of every type that has been checked with <see cref="GetArrayInnerType"/>, or null for types that are not arrays.
        /// </summary>
        private static readonly ConcurrentDictionary<Type, Type?> arrayItemTypes = new()
        {
            [typeof(ElementArray)] = typeof(Element),
            [typeof(IntArray)] = typeof(int),
            [typeof(FloatArray)] = typeof(float),
            [typeof(BoolArray)] = typeof(bool),
            [typeof(StringArray)] = typeof(string),
            [typeof(BinaryArray)] = typeof(byte[]),
            [typeof(TimeSpanArray)] = typeof(TimeSpan),
            [typeof(ColorArray)] = typeof(Color),
            [typeof(Vector2Array)] = typeof(Vector2),
            [typeof(Vector3Array)] = typeof(Vector3),
            [typeof(Vector4Array)] = typeof(Vector4),
            [typeof(QuaternionArray)] = typeof(Quaternion),
            [typeof(MatrixArray)] = typeof(Matrix4x4),
            [typeof(ByteArray)] = typeof(byte),
            [typeof(UInt64Array)] = typeof(ulong),
            [typeof(Element)] = null,
            [typeof(string)] = null,
        };

        /// <summary>
        /// Determines whether the given Type is valid as a Datamodel <see cref="Attribute"/>.
        /// </summary>
        /// <remarks><see cref="ICollection{T}"/> objects pass if their generic argument is valid.</remarks>
        /// <seealso cref="IsDatamodelArrayType"/>
        /// <param name="t">The Type to check.</param>
        public static bool IsDatamodelType(Type t)
        {
            return Datamodel.AttributeTypes.Contains(t) || IsDatamodelArrayType(t) || t.IsSubclassOf(typeof(Element));
        }

        /// <summary>
        /// Determines whether the given Type is valid as a Datamodel <see cref="Attribute"/> array.
        /// </summary>
        /// <seealso cref="IsDatamodelType"/>
        /// <seealso cref="GetArrayInnerType"/>
        /// <param name="t">The Type to check.</param>
        public static bool IsDatamodelArrayType(Type t)
        {
            return GetArrayInnerType(t) != null;
        }

        /// <summary>
        /// Returns the inner Type of an object which implements <see cref="IList{T}"/> for an attribute type T, or null if there is no inner Type.
        /// </summary>
        /// <param name="t">The Type to check.</param>
        public static Type? GetArrayInnerType(Type t)
        {
            if (arrayItemTypes.TryGetValue(t, out var inner))
            {
                return inner;
            }

            foreach (var (list, item) in arrayInterfaces)
            {
                if (list.IsAssignableFrom(t))
                {
                    inner = item;
                    break;
                }
            }

            arrayItemTypes[t] = inner;
            return inner;
        }
        #endregion

        static Datamodel()
        {
            RegisterCodec("binary", [1, 2, 3, 4, 5, 9], () => new Binary());
            RegisterCodec("keyvalues2", [1, 2, 3, 4], () => new KeyValues2());
            RegisterCodec("keyvalues2_noids", [1, 2, 3, 4], () => new KeyValues2());
            TextEncoding = new System.Text.UTF8Encoding(false);
        }

        #region Codecs
        private static readonly Dictionary<CodecRegistration, Func<ICodec>> Codecs = [];

        public static IEnumerable<CodecRegistration> CodecsRegistered => Codecs.Keys.OrderBy(reg => string.Join(null, reg.Item1, reg.Item2)).ToArray();

        /// <summary>
        /// Registers an <see cref="ICodec"/> for an encoding name and one or more encoding versions.
        /// </summary>
        /// <remarks>Existing codecs will be replaced.</remarks>
        /// <param name="encoding">The encoding name that the codec handles.</param>
        /// <param name="versions">The encoding version(s) that the codec handles.</param>
        /// <param name="create">Creates a new instance of the codec. Called once per encode or decode.</param>
        public static void RegisterCodec(string encoding, IEnumerable<int> versions, Func<ICodec> create)
        {
            ArgumentNullException.ThrowIfNull(encoding);
            ArgumentNullException.ThrowIfNull(versions);
            ArgumentNullException.ThrowIfNull(create);

            foreach (var version in versions)
            {
                var reg = new CodecRegistration(encoding, version);

                if (Codecs.ContainsKey(reg))
                {
                    Trace.TraceInformation("Datamodel.NET: Replacing existing codec for {0} {1}", encoding, version);
                }

                Codecs[reg] = create;
            }
        }

        private static ICodec GetCodec(string encoding, int encoding_version)
        {
            if (!Codecs.TryGetValue(new CodecRegistration(encoding, encoding_version), out var create))
            {
                throw new CodecException($"No codec found for {encoding} version {encoding_version}.");
            }

            return create();
        }

        /// <summary>
        /// Determines whether a codec has been registered for the given encoding name and version.
        /// </summary>
        /// <param name="encoding">The name of the encoding to check for.</param>
        /// <param name="encoding_version">The version of the encoding to check for.</param>
        public static bool HaveCodec(string encoding, int encoding_version)
        {
            return Codecs.ContainsKey(new CodecRegistration(encoding, encoding_version));
        }
        #endregion

        #region Element factories
        private static readonly object elementFactoryLock = new();
        private static IElementFactory[] elementFactories = [];

        /// <summary>
        /// Registers a factory whose classes <see cref="Load{T}(Stream, DeferredMode, LoadOptions)"/> constructs, and the schemas of those classes.
        /// The ElementFactory generated by KeyValues2.ElementFactoryGenerator calls this when its assembly is initialised.
        /// </summary>
        public static void RegisterElementFactory(IElementFactory factory)
        {
            ArgumentNullException.ThrowIfNull(factory);

            lock (elementFactoryLock)
            {
                if (Array.IndexOf(elementFactories, factory) >= 0)
                {
                    return;
                }

                foreach (var schema in factory.Schemas)
                {
                    ElementSchema.Register(schema);
                }

                elementFactories = [.. elementFactories, factory];
            }
        }

        /// <summary>
        /// Gets the registered factories, in registration order.
        /// </summary>
        public static IReadOnlyList<IElementFactory> ElementFactories => elementFactories;
        #endregion

        #region Save / Load

        /// <summary>
        /// Gets or sets the assumed encoding of text in DMX files. Defaults to UTF8.
        /// </summary>
        /// <remarks>Changing this value does not alter Datamodels which are already in memory.</remarks>
        public static System.Text.Encoding TextEncoding
        {
            get; set;
        }

        private const string FormatBlankError = "Cannot save while Datamodel.Format is blank";

        /// <summary>
        /// Writes this Datamodel to a <see cref="Stream"/> with the given encoding and encoding version.
        /// </summary>
        /// <param name="stream">The output Stream.</param>
        /// <param name="encoding">The desired encoding.</param>
        /// <param name="encoding_version">The desired encoding version.</param>
        /// <exception cref="InvalidOperationException">Thrown when the value of <see cref="Datamodel.Format"/> is null or whitespace.</exception>
        public void Save(Stream stream, string encoding, int encoding_version)
        {
            if (string.IsNullOrWhiteSpace(Format))
            {
                throw new InvalidOperationException(FormatBlankError);
            }

            GetCodec(encoding, encoding_version).Encode(this, encoding, encoding_version, stream);
        }

        /// <summary>
        /// Writes this Datamodel to a file path with the given encoding and encoding version.
        /// </summary>
        /// <param name="path">The destination file path.</param>
        /// <param name="encoding">The desired encoding.</param>
        /// <param name="encoding_version">The desired encoding version.</param>
        /// <exception cref="InvalidOperationException">Thrown when the value of <see cref="Datamodel.Format"/> is null or whitespace.</exception>
        public void Save(string path, string encoding, int encoding_version)
        {
            if (string.IsNullOrWhiteSpace(Format))
            {
                throw new InvalidOperationException(FormatBlankError);
            }

            using var stream = System.IO.File.Create(path);
            Save(stream, encoding, encoding_version);
        }

        /// <summary>
        /// Loads a Datamodel from a <see cref="Stream"/>.
        /// </summary>
        /// <param name="stream">The input Stream.</param>
        /// <param name="defer_mode">How to handle deferred loading.</param>
        public static Datamodel Load(Stream stream, DeferredMode defer_mode = DeferredMode.Automatic)
        {
            return Load_Internal<Element>(stream, defer_mode, null);
        }
        /// <summary>
        /// Loads a Datamodel from a <see cref="Stream"/>, constructing every element whose class name matches an <see cref="Element"/> subclass in the namespace of <typeparamref name="T"/>.
        /// </summary>
        /// <param name="stream">The input Stream.</param>
        /// <param name="defer_mode">How to handle deferred loading.</param>
        /// <param name="options">Which namespace and factory to use. Defaults to those of <typeparamref name="T"/>.</param>
        /// <typeparam name="T">The class of the Root element.</typeparam>
        public static Datamodel Load<T>(Stream stream, DeferredMode defer_mode = DeferredMode.Automatic, LoadOptions? options = null)
            where T : Element
        {
            return Load_Internal<T>(stream, defer_mode, options);
        }

        /// <summary>
        /// Loads a Datamodel from a byte array.
        /// </summary>
        /// <param name="data">The input byte array.</param>
        /// <param name="defer_mode">How to handle deferred loading.</param>
        public static Datamodel Load(byte[] data, DeferredMode defer_mode = DeferredMode.Automatic)
        {
            return Load_Internal<Element>(new MemoryStream(data, true), defer_mode);
        }
        /// <summary>
        /// Loads a Datamodel from a byte array, constructing every element whose class name matches an <see cref="Element"/> subclass in the namespace of <typeparamref name="T"/>.
        /// </summary>
        /// <param name="data">The input byte array.</param>
        /// <param name="options">Which namespace and factory to use. Defaults to those of <typeparamref name="T"/>.</param>
        /// <typeparam name="T">The class of the Root element.</typeparam>
        public static Datamodel Load<T>(byte[] data, LoadOptions? options = null)
             where T : Element
        {
            return Load_Internal<T>(new MemoryStream(data, true), DeferredMode.Disabled, options);
        }

        /// <summary>
        /// Loads a Datamodel from a file path.
        /// </summary>
        /// <param name="path">The source file path.</param>
        /// <param name="defer_mode">How to handle deferred loading.</param>
        public static Datamodel Load(string path, DeferredMode defer_mode = DeferredMode.Automatic)
        {
            var stream = File.OpenRead(path);
            Datamodel? dm = null;
            try
            {
                dm = Load_Internal<Element>(stream, defer_mode);
                return dm;
            }
            finally
            {
                if (dm == null || defer_mode == DeferredMode.Disabled || dm.Codec == null) stream.Dispose();
            }
        }
        /// <summary>
        /// Loads a Datamodel from a file path, constructing every element whose class name matches an <see cref="Element"/> subclass in the namespace of <typeparamref name="T"/>.
        /// </summary>
        /// <param name="path">The source file path.</param>
        /// <param name="options">Which namespace and factory to use. Defaults to those of <typeparamref name="T"/>.</param>
        /// <typeparam name="T">The class of the Root element.</typeparam>
        public static Datamodel Load<T>(string path, LoadOptions? options = null)
            where T : Element
        {
            using var stream = File.OpenRead(path);
            return Load_Internal<T>(stream, DeferredMode.Disabled, options);
        }

        private static Datamodel Load_Internal<T>(Stream stream, DeferredMode defer_mode = DeferredMode.Automatic, LoadOptions? options = null)
            where T : Element
        {
            var resolver = ElementTypeResolver.For(typeof(T), options);

            stream.Seek(0, SeekOrigin.Begin);
            var header = string.Empty;
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                header += (char)b;
                if (b == '>')
                    break;
                if (header.Length > 128) // probably not a DMX at this point
                    break;
            }

            var match = System.Text.RegularExpressions.Regex.Match(header, CodecUtilities.HeaderPattern_Regex);

            if (!match.Success || match.Groups.Count != 5)
                throw new InvalidOperationException("Could not read file header.");

            string encoding = match.Groups[1].Value;
            int encoding_version = int.Parse(match.Groups[2].Value);

            string format = match.Groups[3].Value;
            int format_version = int.Parse(match.Groups[4].Value);

            ICodec codec = GetCodec(encoding, encoding_version);

            var dm = codec.Decode(encoding, encoding_version, format, format_version, stream, defer_mode, resolver);
            if (defer_mode == DeferredMode.Automatic && codec is IDeferredAttributeCodec deferredCodec)
            {
                dm.Stream = stream;
                dm.Codec = deferredCodec;
            }

            dm.Format = format;
            dm.FormatVersion = format_version;

            dm.Encoding = encoding;
            dm.EncodingVersion = encoding_version;

            if (dm.Root is not null and not T)
            {
                throw new InvalidDataException($"The root element is a '{dm.Root.ClassName}' loaded as {dm.Root.GetType().Name}, not {typeof(T).Name}. Check that the class exists in the namespace used to load the file.");
            }

            return dm;
        }

        internal Element? OnStubRequest(Guid id)
        {
            Element? result = null;
            if (StubRequest != null)
            {
                result = StubRequest(id);

                if (result is null)
                {
                    throw new InvalidDataException("Stub request failed, result was null");
                }

                if (result.ID != id)
                    throw new InvalidOperationException("Datamodel.StubRequest returned an Element with a an ID different from the one requested.");
                if (result.Owner != this)
                    result = ImportElement(result, ImportRecursionMode.Stubs, ImportOverwriteMode.Stubs);
            }

            return result ?? AllElements[id];
        }

        /// <summary>
        /// Occurs when an attempt is made to access a stub elment.
        /// </summary>
        public event StubRequestHandler? StubRequest;

        #endregion

        /// <summary>
        /// Creates a new Datamodel with a specified Format and FormatVersion.
        /// </summary>
        /// <param name="format">The format of the Datamodel. This is not the same as the encoding used to save or load the Datamodel.</param>
        /// <param name="format_version">The version of the format in use.</param>
        public Datamodel(string format, int format_version)
            : this()
        {
            Format = format;
            FormatVersion = format_version;
        }

        /// <summary>
        /// Creates a new Datamodel.
        /// </summary>
        public Datamodel()
        {
            AllElements = new ElementList(this);
            PrefixAttributes = new AttributeList(this);
        }

        protected bool Initialising
        {
            get; private set;
        }
        void ISupportInitialize.BeginInit()
        {
            if (Initialising) throw new InvalidOperationException("Datamodel is already initializing.");
            Initialising = true;
        }

        void ISupportInitialize.EndInit()
        {
            if (!Initialising) throw new InvalidOperationException("Datamodel is not initializing.");

            if (Format == null)
                throw new InvalidOperationException("A Format name must be defined.");

            Initialising = false;
        }

        /// <summary>
        /// Releases any <see cref="Stream"/> being used to deferred load the Datamodel's <see cref="Attribute"/>s.
        /// </summary>
        public void Dispose()
        {
            Stream?.Dispose();
            AllElements.Dispose();
        }

        #region Properties

        /// <summary>
        /// Gets or sets whether new Elements with random IDs can be created by this Datamodel.
        /// </summary>
        public bool AllowRandomIDs
        {
            get => _AllowRandomIDs;
            set
            {
                _AllowRandomIDs = value; OnPropertyChanged();
            }
        }
        bool _AllowRandomIDs = true;

        /// <summary>
        /// Gets or sets the format of the Datamodel.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when an attempt is made to set a value containing the space character.</exception>
        public string Format
        {
            get => _Format;
            set
            {
                if (value is null)
                {
                    throw new InvalidDataException("Format can not be null");
                }

                if (value.Contains(' '))
                    throw new ArgumentException("Format name cannot contain spaces.");
                _Format = value;
                OnPropertyChanged();
            }
        }
        string _Format = "";

        /// <summary>
        /// Gets or sets the version of the <see cref="Format"/> in use.
        /// </summary>
        public int FormatVersion
        {
            get => _FormatVersion;
            set
            {
                _FormatVersion = value; OnPropertyChanged();
            }
        }
        int _FormatVersion;

        /// <summary>
        /// Gets or sets the encoding with which this Datamodel should be stored.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when an attempt is made to set a value containing the space character.</exception>
        public string Encoding
        {
            get => _Encoding;
            set
            {
                if (value is null)
                {
                    throw new InvalidDataException("Encoding can not be null");
                }

                if (value.Contains(' '))
                    throw new ArgumentException("Encoding name cannot contain spaces.");
                _Encoding = value;
                OnPropertyChanged();
            }
        }
        string _Encoding = "";

        /// <summary>
        /// Gets or sets the version of the <see cref="Encoding"/> in use.
        /// </summary>
        public int EncodingVersion
        {
            get => _EncodingVersion;
            set
            {
                _EncodingVersion = value; OnPropertyChanged();
            }
        }
        int _EncodingVersion;

        Stream? Stream;
        internal IDeferredAttributeCodec? Codec;

        /// <summary>
        /// Gets or sets the first Element of the Datamodel. Only Elements referenced by the Root element or one of its children are considered a part of the Datamodel.
        /// </summary>
        /// <exception cref="ElementOwnershipException">Thown when an attempt is made to assign an Element from another Datamodel to this property.</exception>
        public Element? Root
        {
            get => _Root;
            set
            {
                if (value != null)
                {
                    if (value.Owner == null)
                        value = ImportElement(value, ImportRecursionMode.Recursive, ImportOverwriteMode.All);
                    else if (value.Owner != this)
                        throw new ElementOwnershipException();
                }
                _Root = value;
                OnPropertyChanged();
            }
        }
        Element? _Root;

        public AttributeList PrefixAttributes
        {
            get; protected set;
        }

        /// <summary>
        /// Gets or sets the ID under which <see cref="PrefixAttributes"/> are stored when the encoding also writes them as an element,
        /// as "keyvalues2" and "binary" version 9 do. Preserved across a load and save cycle.
        /// </summary>
        public Guid PrefixElementId { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Gets all Elements owned by this Datamodel. Only Elements which are referenced by the Root element or one of its children are actually considered part of the Datamodel.
        /// </summary>
        public ElementList AllElements
        {
            get; protected set;
        }
        #endregion

        #region Element handling
        /// <summary>
        /// Copies another Datamodel's <see cref="Element"/> into this one.
        /// </summary>
        /// <remarks>The return value will be owned by this Datamodel. It can be:
        /// 1. foreign_element. This is the case when foreign_element has no owner and no Element with the same ID exists in this Datamodel.
        /// 2. An existing Element owned by this Datamodel with the same ID as foreign_element.
        /// 3. A copy of foreign_element. This is the case when foreign_element already had an owner and a corresponding Element was not found in this Datamodel.</remarks>
        /// <param name="foreign_element">The Element to import. Must be owned by a different Datamodel.</param>
        /// <param name="import_mode">How to respond when foreign_element references other foreign Elements.</param>
        /// <param name="overwrite_mode">How to respond when the ID of a foreign Element is already in use in this Datamodel.</param>
        /// <returns>foreign_element, a local Element, or a new copy of foreign_element. See Remarks for more details.</returns>
        /// <exception cref="ArgumentNullException">Thrown if foreign_element is null.</exception>
        /// <exception cref="ElementOwnershipException">Thrown if foreign_element is already owned by this Datamodel.</exception>
        /// <exception cref="IndexOutOfRangeException">Thrown when the maximum number of Elements allowed in a Datamodel has been reached.</exception>
        /// <seealso cref="Element.Stub"/>
        /// <seealso cref="Element.ID"/>
        public Element? ImportElement(Element foreign_element, ImportRecursionMode import_mode, ImportOverwriteMode overwrite_mode)
        {
            ArgumentNullException.ThrowIfNull(foreign_element);

            if (foreign_element.Owner == this)
            {
                throw new ElementOwnershipException("Element is already a part of this Datamodel.");
            }

            return ImportElement_internal(foreign_element, new ImportJob(import_mode, overwrite_mode));
        }

        public enum ImportRecursionMode
        {
            /// <summary>
            /// Import the given Element only. Any Element reference will become null.
            /// </summary>
            Nulls,
            /// <summary>
            /// Import the given Element only. Any Element references will be represented with stubs.
            /// </summary>
            Stubs,
            /// <summary>
            /// Recursively import all referenced Elements.
            /// </summary>
            Recursive,
        }

        public enum ImportOverwriteMode
        {
            /// <summary>
            /// If a local Element has the same ID as a foreign Element, ignore the foreign Element.
            /// </summary>
            None,
            /// <summary>
            /// If a local stub Element has the same ID as a non-stub foreign Element, replace it.
            /// </summary>
            Stubs,
            /// <summary>
            /// If a local Element has the same ID as a non-stub foreign Element, replace it.
            /// </summary>
            All,
        }

        private struct ImportJob
        {
            public ImportRecursionMode ImportMode
            {
                get; private set;
            }
            public ImportOverwriteMode OverwriteMode
            {
                get; private set;
            }
            public Dictionary<Element, Element> ImportMap
            {
                get; private set;
            }
            public int Depth
            {
                get; set;
            }

            public ImportJob(ImportRecursionMode import_mode, ImportOverwriteMode overwrite_mode)
                : this()
            {
                ImportMode = import_mode;
                OverwriteMode = overwrite_mode;
                ImportMap = [];
            }
        }

        private object? CopyValue(object value, ImportJob job)
        {
            if (value == null) return null;
            var attr_type = value.GetType();

            // do nothing for a value type or string...
            if (attr_type.IsValueType || attr_type == typeof(string))
                return value;

            // ...copy a reference type
            else if (attr_type == typeof(Element))
            {
                var foreign_element = (Element)value;
                var local_element = AllElements[foreign_element.ID];
                Element? best_element;

                if (local_element != null && !local_element.Stub)
                    best_element = local_element;
                else if (!foreign_element.Stub && job.ImportMode == ImportRecursionMode.Recursive)
                {
                    job.Depth++;
                    best_element = ImportElement_internal(foreign_element, job);
                    job.Depth--;
                }
                else
                    best_element = local_element ?? (job.ImportMode == ImportRecursionMode.Stubs ? new Element(this, foreign_element.ID) : null);

                return best_element;
            }
            else if (attr_type == typeof(byte[]))
            {
                var inbytes = (byte[])value;
                var outbytes = new byte[inbytes.Length];
                inbytes.CopyTo(outbytes, 0);
                return outbytes;
            }

            else throw new ArgumentException("CopyValue: unhandled type.");
        }

        private Element? ImportElement_internal(Element? foreign_element, ImportJob job)
        {
            if (foreign_element == null) return null;
            if (foreign_element.Owner == this) return foreign_element;

            Element? local_element;

            // don't import the same Element twice
            if (job.ImportMap.TryGetValue(foreign_element, out local_element))
                return local_element;

            lock (foreign_element.SyncRoot)
            {
                // Claim an unowned Element
                if (foreign_element.Owner == null && AllElements[foreign_element.ID] == null)
                {
                    foreign_element.Owner = this;
                    foreach (var attr in foreign_element)
                    {
                        if (attr.Value is Element elem)
                            foreign_element[attr.Key] = ImportElement_internal(elem, job);

                        if (attr.Value is ElementArray elem_array)
                        {
                            elem_array.Owner = foreign_element;
                            for (int i = 0; i < elem_array.Count; i++)
                            {
                                var item = elem_array[i];
                                if (item != null && item.Owner != null)
                                {
                                    var importedElement = ImportElement_internal(item, job);
                                    if (importedElement is not null)
                                    {
                                        elem_array[i] = importedElement;
                                    }
                                }
                            }
                        }
                    }
                    return foreign_element;
                }

                // find a local element with the same ID and either return or stub it
                local_element = AllElements[foreign_element.ID];
                if (local_element != null)
                {
                    if (!foreign_element.Stub && (job.OverwriteMode == ImportOverwriteMode.All || (job.OverwriteMode == ImportOverwriteMode.Stubs && local_element.Stub)))
                    {
                        local_element.Name = foreign_element.Name;
                        local_element.ClassName = foreign_element.ClassName;
                        local_element.Stub = false;
                    }
                    else
                        return local_element;
                }
                else
                {
                    // Create a new local Element
                    if (foreign_element.Stub || (job.ImportMode == ImportRecursionMode.Stubs && job.Depth > 0))
                        local_element = new Element(this, foreign_element.ID);
                    else if (job.ImportMode == ImportRecursionMode.Recursive || job.Depth == 0)
                        local_element = new Element(this, foreign_element.Name, foreign_element.ID, foreign_element.ClassName);
                    else
                        local_element = null;
                }

                if (local_element is null)
                {
                    return null;
                }

                job.ImportMap.Add(foreign_element, local_element);

                // Copy attributes
                if (local_element != null && !local_element.Stub)
                {
                    local_element.Clear();
                    foreach (var attr in foreign_element)
                    {
                        if (attr.Value == null)
                            local_element[attr.Key] = null;
                        else if (IsDatamodelArrayType(attr.Value.GetType()))
                        {
                            var list = (System.Collections.ICollection)attr.Value;
                            var inner_type = GetArrayInnerType(list.GetType());

                            if (inner_type is null)
                            {
                                throw new InvalidOperationException("Failed to get inner_type while importing element");
                            }

                            var copied_array = CodecUtilities.MakeList(inner_type, list.Count);
                            foreach (var item in list)
                                copied_array.Add(CopyValue(item, job));

                            local_element[attr.Key] = copied_array;
                        }
                        else
                            local_element[attr.Key] = CopyValue(attr.Value, job);
                    }
                }
                return local_element;
            }
        }

        #endregion

        #region Events
        /// <summary>
        /// Raised when the Datamodel's <see cref="Format"/>, <see cref="FormatVersion"/>, or <see cref="Root"/> changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName()] string property = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
        #endregion
    }

    /// <summary>
    /// Represents the method that will handle the <see cref="Datamodel.StubRequest"/> event.
    /// </summary>
    /// <param name="id">The Element ID to search for.</param>
    /// <returns>An Element with the requested ID.</returns>
    public delegate Element StubRequestHandler(Guid id);

    #region Exceptions
    /// <summary>
    /// The exception that is thrown when the value of an <see cref="Attribute"/> is an unsupported <see cref="Type"/>.
    /// </summary>
    [Serializable]
    public class AttributeTypeException : ArgumentException
    {
        public AttributeTypeException(string message)
            : base(message)
        {
        }

        [SecuritySafeCritical]
        protected AttributeTypeException(SerializationInfo info, StreamingContext context)
        {
        }
    }

    /// <summary>
    /// The exception that is thrown when an <see cref="Element.ID"/> collision occurrs.
    /// </summary>
    [Serializable]
    public class ElementIdException : InvalidOperationException
    {
        internal ElementIdException(string message)
            : base(message)
        {
        }

        [SecuritySafeCritical]
        protected ElementIdException(SerializationInfo info, StreamingContext context)
        {
        }
    }

    /// <summary>
    /// The exception that is thrown when a Datamodel tries to manipulate an <see cref="Element"/> with an innapropriate owner.
    /// </summary>
    [Serializable]
    public class ElementOwnershipException : InvalidOperationException
    {
        internal ElementOwnershipException(string message)
            : base(message)
        {
        }

        internal ElementOwnershipException()
            : base("Cannot add an Element from a different Datamodel. Use ImportElement() first.")
        {
        }

        [SecuritySafeCritical]
        protected ElementOwnershipException(SerializationInfo info, StreamingContext context)
        {
        }
    }

    /// <summary>
    /// The exception that is thrown when an error occurs in an <see cref="ICodec"/>.
    /// </summary>
    [Serializable]
    public class CodecException : Exception
    {
        public CodecException(string message)
            : base(message)
        {
        }

        public CodecException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        [SecuritySafeCritical]
        protected CodecException(SerializationInfo info, StreamingContext context)
        {
        }
    }

    /// <summary>
    /// The exception that is thrown when an error occurs while destubbing an attribute value.
    /// </summary>
    [Serializable]
    public class DestubException : Exception
    {
        internal DestubException(Attribute attr, Exception innerException)
            : base("An exception occured while destubbing the value of an attribute.", innerException)
        {
            Data.Add("Element", ((Element?)attr.Owner)?.ID);
            Data.Add("Attribute", attr.Name);
        }

        internal DestubException(ElementArray array, int index, Exception innerException)
            : base("An exception occured while destubbing an array item.", innerException)
        {
            var arrayOwner = array.Owner;
            if (arrayOwner is not null)
            {
                Data.Add("Element", ((Element)arrayOwner).ID);
            }
            else
            {
                Data.Add("Element", null);
            }
            Data.Add("Index", index);
        }

        [SecuritySafeCritical]
        protected DestubException(SerializationInfo info, StreamingContext context)
        {
        }
    }

    #endregion
}
