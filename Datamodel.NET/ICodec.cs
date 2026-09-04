using System;
using System.Linq;
using System.IO;
using System.Numerics;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Datamodel.Codecs
{
    /// <summary>
    /// Defines methods for the encoding and decoding of <see cref="Datamodel"/> objects. Codecs are registered with <see cref="Datamodel.RegisterCodec"/>.
    /// </summary>
    /// <remarks>A new ICodec is instantiated for every encode/decode operation.</remarks>
    /// <seealso cref="CodecUtilities"/>
    public interface ICodec
    {
        /// <summary>
        /// Encodes a <see cref="Datamodel"/> to a <see cref="Stream"/>.
        /// </summary>
        /// <param name="dm">The Datamodel to encode.</param>
        /// <param name="encoding_version">The encoding version to use.</param>
        /// <param name="stream">The output stream.</param>
        void Encode(Datamodel dm, string encoding, int encoding_version, Stream stream);

        /// <summary>
        /// Decodes a <see cref="Datamodel"/> from a <see cref="Stream"/>.
        /// </summary>
        /// <param name="encoding_version">The encoding version that this stream uses.</param>
        /// <param name="format">The format of the Datamodel.</param>
        /// <param name="format_version">The format version of the Datamodel.</param>
        /// <param name="stream">The input stream. Its position will always be 0. Do not dispose.</param>
        /// <param name="defer_mode">The deferred loading mode specified by the caller. Only relevant to implementers of <see cref="IDeferredAttributeCodec"/></param>
        /// <param name="resolver">Constructs the <see cref="Element"/> subclass registered for a class name. Pass it to <see cref="CodecUtilities.TryConstructCustomElement"/> for every element.</param>
        /// <returns></returns>
        Datamodel Decode(string encoding, int encoding_version, string format, int format_version, Stream stream, DeferredMode defer_mode, ElementTypeResolver resolver);
    }

    /// <summary>
    /// Constructs <see cref="Element"/> subclasses by class name and describes their properties.
    /// </summary>
    /// <remarks>
    /// The KeyValues2.ElementFactoryGenerator source generator emits an implementation into every assembly that declares Element subclasses
    /// and registers it through <see cref="Datamodel.RegisterElementFactory"/> when the assembly is initialised.
    /// </remarks>
    public interface IElementFactory
    {
        /// <summary>
        /// Constructs a new, unowned instance of the class with the given name in the given namespace, or returns null when there is none.
        /// </summary>
        Element? Create(string nameSpace, string className);

        /// <summary>
        /// Gets the schemas of every class this factory constructs.
        /// </summary>
        IReadOnlyList<ElementSchema> Schemas { get; }
    }

    /// <summary>
    /// Options for loading a Datamodel through the <see cref="Element"/> subclasses of a namespace.
    /// </summary>
    public sealed class LoadOptions
    {
        /// <summary>
        /// Gets or sets the namespace whose classes are used. Defaults to the namespace of the root type passed to <see cref="Datamodel.Load{T}(Stream, DeferredMode, LoadOptions)"/>.
        /// </summary>
        public string? Namespace { get; set; }

        /// <summary>
        /// Gets or sets the factory asked first. Defaults to the factory generated into the assembly of the root type.
        /// The other registered factories are asked afterwards.
        /// </summary>
        public IElementFactory? Factory { get; set; }
    }

    /// <summary>
    /// Resolves element class names to <see cref="Element"/> subclasses while decoding, through the registered <see cref="IElementFactory"/> instances.
    /// </summary>
    /// <remarks>
    /// Every registered factory is consulted, the one generated into the root type's own assembly (or the one given in <see cref="LoadOptions.Factory"/>) first.
    /// </remarks>
    public sealed class ElementTypeResolver
    {
        /// <summary>
        /// A resolver that constructs no subclasses, so every element is loaded as a plain <see cref="Element"/>.
        /// </summary>
        public static ElementTypeResolver Untyped { get; } = new(string.Empty, []);

        readonly string Namespace;
        readonly IElementFactory[] Factories;

        ElementTypeResolver(string nameSpace, IElementFactory[] factories)
        {
            Namespace = nameSpace;
            Factories = factories;
        }

        /// <summary>
        /// Creates a resolver for the classes in the namespace and assembly of <paramref name="rootType"/>.
        /// </summary>
        public static ElementTypeResolver For(Type rootType, LoadOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(rootType);

            if (rootType == typeof(Element))
            {
                return Untyped;
            }

            // the generated factory registers itself when its module is initialised, which is guaranteed to have happened
            // for the caller's assembly but not for an assembly that only declares classes
            RuntimeHelpers.RunModuleConstructor(rootType.Module.ModuleHandle);

            var factories = new List<IElementFactory>();
            var registered = Datamodel.ElementFactories;

            var first = options?.Factory ?? registered.FirstOrDefault(factory => factory.GetType().Assembly == rootType.Assembly);
            if (first != null)
            {
                factories.Add(first);
            }

            foreach (var factory in registered)
            {
                if (!factories.Contains(factory))
                {
                    factories.Add(factory);
                }
            }

            return new ElementTypeResolver(options?.Namespace ?? rootType.Namespace ?? string.Empty, [.. factories]);
        }

        /// <summary>
        /// Constructs a new, unowned instance of the class registered for the given element class name, or null when no factory knows it.
        /// </summary>
        public Element? Construct(string className)
        {
            foreach (var factory in Factories)
            {
                if (factory.Create(Namespace, className) is Element element)
                {
                    return element;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Defines methods for the deferred loading of <see cref="Attribute"/> values.
    /// </summary>
    /// <remarks>
    /// <para>Implementers must still load all elements and Attribute names. Only Attribute values can be streamed.</para>
    /// <para>IDeferredAttributeCodec objects will be attached to their host Datamodel for the duration of its life.</para>
    /// </remarks>
    /// <seealso cref="CodecUtilities"/>
    public interface IDeferredAttributeCodec : ICodec
    {
        /// <summary>
        /// Called when an unloaded <see cref="Attribute"/> is accessed.
        /// </summary>
        /// <param name="dm">The <see cref="Datamodel"/> to which the Attribute belongs.</param>
        /// <param name="offset">The offset at which the Attribute begins in the source <see cref="Stream"/>.</param>
        /// <returns>The Attribute's value.</returns>
        object? DeferredDecodeAttribute(Datamodel dm, long offset);
    }

    /// <summary>
    /// Values which instruct <see cref="IDeferredAttributeCodec"/> implementers on how to use deferred Attribute reading.
    /// </summary>
    public enum DeferredMode
    {
        /// <summary>
        /// The codec decides whether to defer attribute loading.
        /// </summary>
        Automatic,
        /// <summary>
        /// The codec loads all attributes immediately.
        /// </summary>
        Disabled
    }

    /// <summary>
    /// Helper methods for <see cref="ICodec"/> implementers.
    /// </summary>
    public static class CodecUtilities
    {
        /// <summary>
        /// Standard DMX header with CLR-style variable tokens.
        /// </summary>
        public const string HeaderPattern = "<!-- dmx encoding {0} {1} format {2} {3} -->";
        /// <summary>
        /// Standard DMX header as a regular expression pattern.
        /// </summary>
        public const string HeaderPattern_Regex = "<!-- dmx encoding (\\S+) ([0-9]+) format (\\S+) ([0-9]+) -->";
        //public const string HeaderPattern_Proto2 = "<!-- DMXVersion binary_v{0} -->";

        /// <summary>
        /// Creates a <see cref="List{T}"/> for the given Type with the given starting size.
        /// </summary>
        public static System.Collections.IList MakeList(Type t, int count)
        {
            if (t == typeof(Element))
                return new ElementArray(count);
            if (t == typeof(int))
                return new IntArray(count);
            if (t == typeof(float))
                return new FloatArray(count);
            if (t == typeof(bool))
                return new BoolArray(count);
            if (t == typeof(string))
                return new StringArray(count);
            if (t == typeof(byte[]))
                return new BinaryArray(count);
            if (t == typeof(TimeSpan))
                return new TimeSpanArray(count);
            if (t == typeof(Color))
                return new ColorArray(count);
            if (t == typeof(Vector2))
                return new Vector2Array(count);
            if (t == typeof(Vector3))
                return new Vector3Array(count);
            if (t == typeof(Vector4))
                return new Vector4Array(count);
            if (t == typeof(Quaternion))
                return new QuaternionArray(count);
            if (t == typeof(Matrix4x4))
                return new MatrixArray(count);
            if (t == typeof(byte))
                return new ByteArray(count);
            if (t == typeof(ulong))
                return new UInt64Array(count);

            throw new ArgumentException($"Unhandled or invalid type: {t}");
        }

        /// <summary>
        /// Creates a <see cref="List{T}"/> for the given Type, copying items the given IEnumerable
        /// </summary>
        public static System.Collections.IList MakeList(Type t, System.Collections.IEnumerable source)
        {
            if (t == typeof(Element))
                return new ElementArray(source.Cast<Element>());
            if (t == typeof(int))
                return new IntArray(source.Cast<int>());
            if (t == typeof(float))
                return new FloatArray(source.Cast<float>());
            if (t == typeof(bool))
                return new BoolArray(source.Cast<bool>());
            if (t == typeof(string))
                return new StringArray(source.Cast<string>());
            if (t == typeof(byte[]))
                return new BinaryArray(source.Cast<byte[]>());
            if (t == typeof(TimeSpan))
                return new TimeSpanArray(source.Cast<TimeSpan>());
            if (t == typeof(Color))
                return new ColorArray(source.Cast<Color>());
            if (t == typeof(Vector2))
                return new Vector2Array(source.Cast<Vector2>());
            if (t == typeof(Vector3))
                return new Vector3Array(source.Cast<Vector3>());
            if (t == typeof(Vector4))
                return new Vector4Array(source.Cast<Vector4>());
            if (t == typeof(Quaternion))
                return new QuaternionArray(source.Cast<Quaternion>());
            if (t == typeof(Matrix4x4))
                return new MatrixArray(source.Cast<Matrix4x4>());
            if (t == typeof(byte))
                return new ByteArray(source.Cast<byte>());
            if (t == typeof(ulong))
                return new UInt64Array(source.Cast<ulong>());

            throw new ArgumentException("Unrecognised Type.");
        }

        /// <summary>
        /// Creates a new attribute on an <see cref="Element"/>. This method is intended for <see cref="ICodec"/> implementers and should not be directly called from any other code.
        /// </summary>
        /// <param name="elem">The Element to add to.</param>
        /// <param name="key">The name of the attribute. Must be unique on the Element.</param>
        /// <param name="defer_offset">The location in the encoded DMX stream at which this Attribute's value can be found.</param>
        public static void AddDeferredAttribute(Element elem, string key, long offset)
        {
            if (offset <= 0) throw new ArgumentOutOfRangeException(nameof(offset), "Address must be greater than 0.");
            elem.Add(key, offset);
        }

        /// <summary>
        /// Constructs an element of the subclass registered for <paramref name="elem_class"/> and adds it to the Datamodel.
        /// </summary>
        /// <returns>False when no subclass is registered for the class name, in which case a plain <see cref="Element"/> should be used.</returns>
        public static bool TryConstructCustomElement(ElementTypeResolver resolver, Datamodel dataModel, string elem_class, string elem_name, Guid elem_id, out Element? elem)
        {
            elem = resolver.Construct(elem_class);

            if (elem is null)
            {
                return false;
            }

            elem.ID = elem_id;
            elem.Name = elem_name;
            elem.ClassName = elem_class;
            elem.Owner = dataModel;

            return true;
        }
    }

    /// <summary>
    /// Caches that live for the duration of one encode.
    /// </summary>
    class SerializationContext
    {
        public ElementAttributeCache Attributes { get; } = new();

        readonly List<string> Indentation = ["\n"];

        /// <summary>
        /// A newline followed by <paramref name="level"/> levels of indentation.
        /// </summary>
        public string GetIndentation(int level)
        {
            while (Indentation.Count <= level)
                Indentation.Add(Indentation[^1] + "    ");

            return Indentation[level];
        }
    }

    /// <summary>
    /// Builds each Element's attribute list once per encode, instead of once per pass over the Element.
    /// </summary>
    class ElementAttributeCache
    {
        readonly Dictionary<Element, KeyValuePair<string, object?>[]> Cache = [];

        public KeyValuePair<string, object?>[] this[Element element]
        {
            get
            {
                if (!Cache.TryGetValue(element, out var attributes))
                {
                    attributes = [.. element.GetAllAttributesForSerialization()];
                    Cache[element] = attributes;
                }

                return attributes;
            }
        }
    }
}
