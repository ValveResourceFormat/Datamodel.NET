using System;
using System.Linq;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Data;

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
        /// <returns></returns>
        Datamodel Decode(string encoding, int encoding_version, string format, int format_version, Stream stream, DeferredMode defer_mode, ReflectionParams reflectionParams);
    }

    /// <summary>
    /// Parameters for reflection based deserialisation
    /// By default it will look for types in the calling assembly (the one which made this class)
    /// </summary>
    /// <param name="attemptReflection">If to use reflection or not.</param>
    /// <param name="additionalTypes">Additional types to consider when matching.</param>
    /// <param name="assembliesToSearch">Additional assemblies to look for types in.</param>
    public class ReflectionParams(bool attemptReflection = true, List<Type>? additionalTypes = null, List<Assembly>? assembliesToSearch = null)
    {
        public bool AttemptReflection = attemptReflection;

        public string Assembly = string.Empty;
        public string Namespace = string.Empty;

        /// <summary>
        /// Assembly of the root type passed to Load. Its generated <see cref="IElementFactory"/> is asked first.
        /// </summary>
        public Assembly? RootAssembly;
    }

    /// <summary>
    /// Resolves element class names to <see cref="Element"/> subclasses while decoding, through the
    /// <see cref="IElementFactory"/> classes the ElementFactoryGenerator emits into every assembly that references this library.
    /// </summary>
    /// <remarks>
    /// Every factory in the process is consulted, the one generated into the root type's own assembly first. Each factory only knows
    /// the assemblies its compilation referenced, and this library's own factory knows nothing, so stopping at the first one found
    /// would depend on assembly load order.
    /// </remarks>
    public sealed class ElementTypeResolver
    {
        private const string GeneratedFactoryTypeName = "ElementFactory";

        private readonly ReflectionParams reflectionParams;
        private readonly List<IElementFactory> factories = [];

        public ElementTypeResolver(ReflectionParams reflectionParams)
        {
            this.reflectionParams = reflectionParams;

            if (!reflectionParams.AttemptReflection)
            {
                return;
            }

            var factoryTypes = new List<Type>();

            if (reflectionParams.RootAssembly?.GetType(GeneratedFactoryTypeName) is Type rootFactory)
            {
                factoryTypes.Add(rootFactory);
            }

            foreach (var factoryType in CodecUtilities.GetIElementFactoryClasses())
            {
                if (!factoryTypes.Contains(factoryType))
                {
                    factoryTypes.Add(factoryType);
                }
            }

            foreach (var factoryType in factoryTypes)
            {
                if (Activator.CreateInstance(factoryType) is IElementFactory factory)
                {
                    factories.Add(factory);
                }
            }
        }

        /// <summary>
        /// Constructs a new, unowned instance of the class registered for the given element class name, or null when no factory knows it.
        /// </summary>
        public Element? Construct(string className)
        {
            foreach (var factory in factories)
            {
                if (factory.GetClass(reflectionParams.Assembly, reflectionParams.Namespace, className) is Element element)
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
        /// Creates a <see cref="List&lt;T&gt;"/> for the given Type with the given starting size.
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
        /// Creates a <see cref="List&lt;T&gt;"/> for the given Type, copying items the given IEnumerable
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

        private static Type[]? elementFactoryClasses;

        /// <summary>
        /// Finds every <see cref="IElementFactory"/> implementation in the loaded assemblies. The result is cached after the first call.
        /// </summary>
        public static IEnumerable<Type> GetIElementFactoryClasses()
        {
            elementFactoryClasses ??= AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly =>
                {
                    try
                    {
                        return assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        return ex.Types.OfType<Type>();
                    }
                })
                .Where(type => type.IsClass &&
                      !type.IsAbstract &&
                      type.GetInterfaces().Contains(typeof(IElementFactory)))
                .ToArray();

            return elementFactoryClasses;
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

    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
    public sealed class CodecFormatAttribute : System.Attribute
    {
        /// <summary>
        /// Specifies a Datamodel encoding name and some versions that a class handles.
        /// </summary>
        /// <param name="name">The encoding name that the codec handles.</param>
        /// <param name="versions">The encoding version(s) that the codec handles.</param>
        public CodecFormatAttribute(string name, params int[] versions)
        {
            Name = name;
            Versions = versions;
        }

        /// <summary>
        /// Specifies a Datamodel encoding name and version that a class handles.
        /// </summary>
        /// <remarks>This constructor is CLS-compliant.</remarks>
        /// <param name="name">The encoding name that the codec handles.</param>
        /// <param name="version">An encoding version that the codec handles.</param>
        public CodecFormatAttribute(string name, int version)
        {
            Name = name;
            Versions = [version];
        }

        public string Name { get; private set; }
        public int[] Versions { get; private set; }
    }

    public interface IElementFactory
    {
        public object? GetClass(string assembly, string nameSpace, string classname);
    }
}
