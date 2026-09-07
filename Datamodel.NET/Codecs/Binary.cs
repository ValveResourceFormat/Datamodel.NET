using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Numerics;
using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Datamodel.Codecs
{
    [CodecFormat("binary", 1)]
    [CodecFormat("binary", 2)]
    [CodecFormat("binary", 3)]
    [CodecFormat("binary", 4)]
    [CodecFormat("binary", 5)]
    [CodecFormat("binary", 9)]
    class Binary : IDeferredAttributeCodec
    {
        static readonly Dictionary<int, Type?[]> SupportedAttributes = [];
        BinaryReader? Reader;

        /// <summary>
        /// Elements in the order the stream declares them. Element references are indices into this list, which must not change for deferred loading.
        /// </summary>
        readonly List<Element> ElementIndex = [];

        /// <summary>
        /// The number of Datamodel binary ticks in one second. Used to store TimeSpan values.
        /// </summary>
        const uint DatamodelTicksPerSecond = 10000;

        static Binary()
        {
            SupportedAttributes[1] =
            SupportedAttributes[2] = [
                typeof(Element), typeof(int), typeof(float), typeof(bool), typeof(string), typeof(byte[]),
                null /* ObjectID */, typeof(Color), typeof(Vector2), typeof(Vector3), typeof(Vector4), typeof(Vector3) /* angle*/, typeof(Quaternion), typeof(Matrix4x4)
            ];
            SupportedAttributes[3] =
            SupportedAttributes[4] =
            SupportedAttributes[5] = [
                typeof(Element), typeof(int), typeof(float), typeof(bool), typeof(string), typeof(byte[]),
                typeof(TimeSpan), typeof(Color), typeof(Vector2), typeof(Vector3), typeof(Vector4), typeof(Vector3) /* angle*/, typeof(Quaternion), typeof(Matrix4x4)
            ];
            SupportedAttributes[9] = [
                typeof(Element), typeof(int), typeof(float), typeof(bool), typeof(string), typeof(byte[]),
                typeof(TimeSpan), typeof(Color), typeof(Vector2), typeof(Vector3), typeof(Vector4), typeof(QAngle), typeof(Quaternion), typeof(Matrix4x4),
                typeof(ulong), typeof(byte)
            ];
        }

        static byte TypeToId(Type type, int version)
        {
            // a byte[] is a "binary" blob, distinct from a "uint8_array" (Array<byte>) in encoding version 9
            bool array = type != typeof(byte[]) && Datamodel.IsDatamodelArrayType(type);
            var search_type = array ? Datamodel.GetArrayInnerType(type) : type;

            if (array && search_type == typeof(byte) && !SupportedAttributes[version].Contains(typeof(byte)))
            {
                search_type = typeof(byte[]); // Recent version of DMX support both "binary" and "uint8_array" attributes. These are the same thing!
                array = false;
            }
            var type_list = SupportedAttributes[version];
            byte i = 0;
            foreach (var list_type in type_list)
            {
                if (list_type == typeof(Element) && type.IsSubclassOf(typeof(Element)))
                    break;

                if (list_type == search_type)
                    break;
                i++;
            }
            if (i == type_list.Length)
                throw new CodecException(String.Format("\"{0}\" is not supported in encoding binary {1}", type.Name, version));
            if (array) i += (byte)(type_list.Length * (version >= 9 ? 2 : 1));
            return ++i;
        }

        Tuple<Type?, Type?> IdToType(byte id)
        {
            var type_list = SupportedAttributes[EncodingVersion];
            bool array = false;

            id--;

            if (EncodingVersion >= 9 && id >= type_list.Length * 2)
            {
                array = true;
                id -= (byte)(type_list.Length * 2);
            }
            else
            {
                if (id >= type_list.Length)
                {
                    id -= (byte)(type_list.Length);
                    array = true;
                }
            }

            try
            {
                return new Tuple<Type?, Type?>((array ? type_list[id]?.MakeListType() : type_list[id]), (array ? type_list[id] : null));
            }
            catch (IndexOutOfRangeException)
            {
                throw new CodecException(String.Format("Unrecognised attribute type: {0}", id + 1));
            }
        }

        protected string ReadString_Raw(BinaryReader reader)
        {
            List<byte> raw = [];
            while (true)
            {
                byte cur = reader.ReadByte();
                if (cur == 0) break;
                else raw.Add(cur);
            }

            var user_encoding = Datamodel.TextEncoding.GetString(raw.ToArray());
            if (user_encoding.Contains('�'))
                return Encoding.Default.GetString(raw.ToArray());
            else return user_encoding;
        }

        class StringDictionary
        {
            readonly Binary? Codec;
            readonly int EncodingVersion;

            readonly List<string> Strings = [];

            /// <summary>Fast string index lookup.</summary>
            readonly Dictionary<string, int>? Indices;

            public bool Dummy;

            // binary 4 uses int for dictionary length, but short for dictionary indices. Whoops!
            public byte LengthSize { get { return (byte)(EncodingVersion < 4 ? sizeof(short) : sizeof(int)); } }
            public byte IndiceSize { get { return (byte)(EncodingVersion < 5 ? sizeof(short) : sizeof(int)); } }

            /// <summary>
            /// Constructs a new <see cref="StringDictionary"/> from a Binary stream.
            /// </summary>
            public StringDictionary(Binary codec, BinaryReader reader)
            {
                Codec = codec;
                EncodingVersion = codec.EncodingVersion;
                Dummy = EncodingVersion == 1;
                if (!Dummy)
                {
                    foreach (var i in Enumerable.Range(0, LengthSize == sizeof(short) ? reader.ReadInt16() : reader.ReadInt32()))
                        AddString(Codec.ReadString_Raw(reader));
                }
            }

            /// <summary>
            /// Constructs a new <see cref="StringDictionary"/> from a <see cref="Datamodel"/> object.
            /// </summary>
            public StringDictionary(int encoding_version, BinaryWriter writer, Datamodel dm, SerializationContext context)
            {
                EncodingVersion = encoding_version;
                Context = context;

                Dummy = EncodingVersion == 1;
                if (!Dummy)
                {
                    Indices = [];
                    Scraped = [];

                    ScrapeElement(dm.Root);

                    // the prefix attributes are also written as a regular element in version 9
                    if (EncodingVersion >= 9 && dm.PrefixAttributes.Count > 0)
                    {
                        AddString(string.Empty);
                        AddString(PrefixElementClass);
                        foreach (var attr in dm.PrefixAttributes)
                        {
                            AddString(attr.Key);
                            if (attr.Value is string stringValue)
                                AddString(stringValue);
                        }
                    }
                }
            }

            private readonly HashSet<Element> Scraped = [];
            private readonly SerializationContext? Context;

            void ScrapeElement(Element? elem)
            {
                if (elem == null || elem.Stub || Scraped.Contains(elem)) return;
                Scraped.Add(elem);

                AddString(elem.Name);
                AddString(elem.ClassName);
                foreach (var attr in Context!.Attributes[elem])
                {
                    AddString(attr.Key);
                    switch (attr.Value)
                    {
                        case string stringValue:
                            AddString(stringValue);
                            break;
                        case Element elementValue:
                            ScrapeElement(elementValue);
                            break;
                        case IList<Element> elementListValue:
                            foreach (var array_elem in elementListValue)
                                ScrapeElement(array_elem);
                            break;
                    }
                }
            }

            /// <summary>
            /// Add non-nullable string.
            /// </summary>
            /// <param name="value"></param>
            void AddString(string value)
            {
                value ??= string.Empty;

                if (Indices == null)
                {
                    Strings.Add(value);
                    return;
                }

                if (Indices.TryAdd(value, Strings.Count))
                    Strings.Add(value);
            }

            int GetIndex(string value)
            {
                value ??= string.Empty;
                return Indices!.TryGetValue(value, out var index) ? index : -1;
            }

            public string ReadString(BinaryReader reader)
            {
                if (Dummy) return Codec!.ReadString_Raw(reader);
                return Strings[IndiceSize == sizeof(short) ? reader.ReadInt16() : reader.ReadInt32()];
            }

            public void WriteString(string value, BinaryWriter writer)
            {
                if (Dummy)
                    writer.Write(value);
                else
                {
                    var index = GetIndex(value);
                    if (IndiceSize == sizeof(short)) writer.Write((short)index);
                    else writer.Write(index);
                }
            }

            public void WriteSelf(BinaryWriter writer)
            {
                if (Dummy) return;

                if (LengthSize == sizeof(short))
                    writer.Write((short)Strings.Count);
                else
                    writer.Write(Strings.Count);

                foreach (var str in Strings)
                    writer.Write(str);
            }
        }
        StringDictionary? StringDict;

        public void Encode(Datamodel dm, string encoding, int encoding_version, Stream stream)
        {
            using var writer = new DmxBinaryWriter(stream);
            var encoder = new Encoder(writer, dm, encoding_version);
            encoder.Encode();
        }

        private static readonly Dictionary<RuntimeTypeHandle, int> TypeMap = new Dictionary<RuntimeTypeHandle, int>
        {
            { typeof(Element).TypeHandle, 0 },
            { typeof(int).TypeHandle, 1 },
            { typeof(float).TypeHandle, 2 },
            { typeof(bool).TypeHandle, 3 },
            { typeof(string).TypeHandle, 4 },
            { typeof(byte[]).TypeHandle, 5 },
            { typeof(TimeSpan).TypeHandle, 6 },
            { typeof(Color).TypeHandle, 7 },
            { typeof(Vector2).TypeHandle, 8 },
            { typeof(Vector3).TypeHandle, 9 },
            { typeof(QAngle).TypeHandle, 10 },
            { typeof(Vector4).TypeHandle, 11 },
            { typeof(Quaternion).TypeHandle, 12 },
            { typeof(Matrix4x4).TypeHandle, 13 },
            { typeof(byte).TypeHandle, 14 },
            { typeof(UInt64).TypeHandle, 15 }
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        object? ReadValue(Datamodel dm, int typeIndex, bool raw_string, BinaryReader reader)
        {
            return typeIndex switch
            {
                0 => ReadElement(dm, reader),
                1 => reader.ReadInt32(),
                2 => reader.ReadSingle(),
                3 => reader.ReadBoolean(),
                4 => raw_string ? ReadString_Raw(reader) : StringDict!.ReadString(reader),
                5 => reader.ReadBytes(reader.ReadInt32()),
                6 => TimeSpan.FromTicks(reader.ReadInt32() * (TimeSpan.TicksPerSecond / DatamodelTicksPerSecond)),
                7 => ReadColor(reader),
                8 => ReadVector2(reader),
                9 => ReadVector3(reader),
                10 => ReadQAngle(reader),
                11 => ReadVector4(reader),
                12 => ReadQuaternion(reader),
                13 => ReadMatrix4x4(reader),
                14 => reader.ReadByte(),
                15 => reader.ReadUInt64(),
                _ => throw new ArgumentException("Cannot read value of type")
            };
        }

        // Specialized methods to avoid repeated vector allocations
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private object? ReadElement(Datamodel dm, BinaryReader reader)
        {
            var index = reader.ReadInt32();

            if (index == -1)
                return null;

            if (index == -2)
            {
                var id = new Guid(ReadString_Raw(reader));
                return dm.AllElements[id] ?? new Element(dm, id);
            }

            if (index < 0 || index >= ElementIndex.Count)
                throw new CodecException($"Element index {index} is out of range.");

            return ElementIndex[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Color ReadColor(BinaryReader reader)
        {
            var rgba = reader.ReadBytes(4);
            return Color.FromBytes(rgba);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector2 ReadVector2(BinaryReader reader)
        {
            return new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static QAngle ReadQAngle(BinaryReader reader)
        {
            return new QAngle(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector4 ReadVector4(BinaryReader reader)
        {
            return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Quaternion ReadQuaternion(BinaryReader reader)
        {
            return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Matrix4x4 ReadMatrix4x4(BinaryReader reader)
        {
            // Read all 16 floats directly without intermediate array
            return new Matrix4x4(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public Datamodel Decode(string encoding, int encoding_version, string format, int format_version, Stream stream, DeferredMode defer_mode, ReflectionParams reflectionParams)
        {
            var resolver = new ElementTypeResolver(reflectionParams);

            stream.Seek(0, SeekOrigin.Begin);
            while (true)
            {
                var b = stream.ReadByte();
                if (b == 0) break;
            }
            var dm = new Datamodel(format, format_version);

            EncodingVersion = encoding_version;

            Reader = new BinaryReader(stream);

            if (EncodingVersion >= 9)
            {
                // Read prefix elements
                foreach (int prefix_elem in Enumerable.Range(0, Reader.ReadInt32()))
                {
                    foreach (int attr_index in Enumerable.Range(0, Reader.ReadInt32()))
                    {
                        var name = ReadString_Raw(Reader);
                        var value = DecodeAttribute(dm, true, Reader);
                        if (prefix_elem == 0) // skip subsequent elements...are they considered "old versions"?
                            dm.PrefixAttributes[name] = value;
                    }
                }
            }

            StringDict = new StringDictionary(this, Reader);
            var num_elements = Reader.ReadInt32();

            // read index
            foreach (var i in Enumerable.Range(0, num_elements))
            {
                var type = StringDict.ReadString(Reader);
                var name = EncodingVersion >= 4 ? StringDict.ReadString(Reader) : ReadString_Raw(Reader);
                var id_bits = Reader.ReadBytes(16);
                var id = new Guid(BitConverter.IsLittleEndian ? id_bits : id_bits.Reverse().ToArray());

                if (!CodecUtilities.TryConstructCustomElement(resolver, dm, type, name, id, out var elem))
                {
                    // note: constructing an element, adds it to the datamodel.AllElements
                    elem = new Element(dm, name, id, type);
                }

                ElementIndex.Add(elem!);
            }


            // read attributes (or not, if we're deferred)
            foreach (var elem in ElementIndex)
            {
                // assert if stub
                Debug.Assert(!elem.Stub);

                var num_attrs = Reader.ReadInt32();

                foreach (var i in Enumerable.Range(0, num_attrs))
                {
                    var name = StringDict.ReadString(Reader);
                    if (defer_mode == DeferredMode.Automatic)
                    {
                        CodecUtilities.AddDeferredAttribute(elem, name, Reader.BaseStream.Position);
                        SkipAttribute(Reader);
                    }
                    else
                    {
                        elem.Add(name, DecodeAttribute(dm, false, Reader));
                    }
                }
            }

            // version 9 also stores the prefix attributes as an unreferenced element right after the root, fold it back in
            if (EncodingVersion >= 9 && dm.PrefixAttributes.Count > 0 && dm.AllElements.Count > 1)
            {
                var duplicate = dm.AllElements[1];

                if (duplicate != null && !duplicate.Stub && duplicate.ClassName == PrefixElementClass && duplicate.Name.Length == 0
                    && duplicate.Keys.SequenceEqual(dm.PrefixAttributes.Keys))
                {
                    dm.PrefixElementId = duplicate.ID;
                    dm.AllElements.RemoveUnreferenced(duplicate);
                }
            }

            return dm;
        }

        const string PrefixElementClass = "DmElement";

        int EncodingVersion;

        public object? DeferredDecodeAttribute(Datamodel dm, long offset)
        {
            if (Reader is null)
            {
                throw new InvalidDataException("Tried to read a deferred attribute but the reader is invalid");
            }

            Reader.BaseStream.Seek(offset, SeekOrigin.Begin);
            return DecodeAttribute(dm, false, Reader);
        }

        object? DecodeAttribute(Datamodel dm, bool prefix, BinaryReader reader)
        {
            var types = IdToType(reader.ReadByte());

            if (types.Item2 == null)
                return ReadValue(dm, TypeMap[types.Item1.TypeHandle], EncodingVersion < 4 || prefix, reader);
            else
            {
                var count = reader.ReadInt32();
                var inner_type = types.Item2;
                var array = CodecUtilities.MakeList(inner_type, count);

                var typeId = TypeMap[inner_type.TypeHandle];
                foreach (var x in Enumerable.Range(0, count))
                    array.Add(ReadValue(dm, typeId, true, reader));

                return array;
            }
        }

        void SkipAttribute(BinaryReader reader)
        {
            var types = IdToType(reader.ReadByte());

            int count = 1;
            Type? type = types.Item1;

            if (type is null)
            {
                throw new InvalidDataException("Failed to match id to type");
            }

            if (types.Item2 != null)
            {
                count = reader.ReadInt32();
                type = types.Item2;
            }

            if (type == typeof(Element))
            {
                foreach (int i in Enumerable.Range(0, count))
                    if (reader.ReadInt32() == -2) reader.BaseStream.Seek(37, SeekOrigin.Current); // skip GUID + null terminator if a stub
                return;
            }

            int length;

            if (type == typeof(TimeSpan))
                length = sizeof(int);
            else if (type == typeof(Color))
                length = 4;
            else if (type == typeof(bool))
                length = 1;
            else if (type == typeof(byte[]))
            {
                foreach (var i in Enumerable.Range(0, count))
                    reader.BaseStream.Seek(reader.ReadInt32(), SeekOrigin.Current);
                return;
            }
            else if (type == typeof(string))
            {
                if (!StringDict!.Dummy && types.Item2 == null && EncodingVersion >= 4)
                    length = StringDict.IndiceSize;
                else
                {
                    foreach (var i in Enumerable.Range(0, count))
                    {
                        byte b;
                        do { b = reader.ReadByte(); } while (b != 0);
                    }
                    return;
                }
            }
            else if (type == typeof(Vector2))
                length = sizeof(float) * 2;
            else if (type == typeof(Vector3))
                length = sizeof(float) * 3;
            else if (type == typeof(Vector4) || type == typeof(Quaternion))
                length = sizeof(float) * 4;
            else if (type == typeof(Matrix4x4))
                length = sizeof(float) * 4 * 4;
            else
                length = System.Runtime.InteropServices.Marshal.SizeOf(type);

            reader.BaseStream.Seek(length * count, SeekOrigin.Current);
        }

        readonly struct Encoder
        {
            readonly Dictionary<Element, int> ElementIndices;
            readonly List<AttributeList> ElementOrder;
            readonly BinaryWriter Writer;
            readonly StringDictionary StringDict;
            readonly Datamodel Datamodel;
            readonly SerializationContext Context;

            readonly int EncodingVersion;

            public Encoder(BinaryWriter writer, Datamodel dm, int version)
            {
                EncodingVersion = version;
                Writer = writer;
                Datamodel = dm;

                Context = new SerializationContext();
                StringDict = new StringDictionary(version, writer, dm, Context);
                ElementIndices = [];
                ElementOrder = [];

            }

            public void Encode()
            {
                Writer.Write(string.Format(CodecUtilities.HeaderPattern, "binary", EncodingVersion, Datamodel.Format, Datamodel.FormatVersion) + "\n");

                if (EncodingVersion >= 9)
                {
                    WritePrefixAttributes();
                }

                StringDict.WriteSelf(Writer);

                var hasPrefixElement = EncodingVersion >= 9 && Datamodel.PrefixAttributes.Count > 0;
                var elementCount = CountChildren(Datamodel.Root, []) + (hasPrefixElement ? 1 : 0);
                Writer.Write(elementCount);

                var root = Datamodel.Root;
                if (root != null && !root.Stub)
                {
                    WriteIndexEntry(root, root.ClassName, root.Name, root.ID);

                    // the prefix attributes are also stored as an unreferenced element right after the root
                    if (hasPrefixElement)
                        WriteIndexEntry(Datamodel.PrefixAttributes, PrefixElementClass, string.Empty, Datamodel.PrefixElementId);

                    WriteIndexChildren(root);
                }

                foreach (var body in ElementOrder)
                    WriteBody(body);
            }

            int CountChildren(Element? elem, HashSet<Element> counter)
            {
                if (elem is null)
                {
                    return 0;
                }

                if (elem.Stub) return 0;
                int num_elems = 1;
                counter.Add(elem);
                foreach (var attr in Context.Attributes[elem])
                {
                    if (attr.Value == null) continue;

                    if (attr.Value is Element child_elem && !counter.Contains(child_elem))
                    {
                        num_elems += CountChildren(child_elem, counter);
                    }
                    else if (attr.Value is IEnumerable<Element> child_array)
                    {
                        foreach (var array_elem in child_array.Where(c => c != null && !counter.Contains(c)))
                            num_elems += CountChildren(array_elem, counter);
                    }
                }

                return num_elems;
            }

            void WriteIndex(Element? elem)
            {
                if (elem is null || elem.Stub || ElementIndices.ContainsKey(elem)) return;

                WriteIndexEntry(elem, elem.ClassName, elem.Name, elem.ID);
                WriteIndexChildren(elem);
            }

            void WriteIndexEntry(AttributeList body, string className, string name, Guid id)
            {
                if (body is Element elem)
                    ElementIndices[elem] = ElementOrder.Count;
                ElementOrder.Add(body);

                StringDict.WriteString(className, Writer);
                if (EncodingVersion >= 4) StringDict.WriteString(name, Writer);
                else Writer.Write(name);
                Writer.Write(id.ToByteArray());
            }

            void WriteIndexChildren(Element elem)
            {
                foreach (var attr in Context.Attributes[elem])
                {
                    var child_elem = attr.Value as Element;
                    if (child_elem != null)
                    {
                        if (!ElementIndices.ContainsKey(child_elem))
                            WriteIndex(child_elem);
                    }
                    else
                    {
                        var elem_list = attr.Value as IList<Element>;
                        if (elem_list != null)
                        {
                            var elem_indices = ElementIndices; // workaround for .Net 4 lambda limitation in structs
                            foreach (var item in elem_list.Where(e => e != null && !elem_indices.ContainsKey(e)))
                                WriteIndex(item);
                        }
                    }
                }
            }

            /// <summary>
            /// Prefix attributes are stored as a list of prefix elements, each a list of name/typed value pairs.
            /// Only the first prefix element is read back, so everything is written into a single one.
            /// </summary>
            void WritePrefixAttributes()
            {
                var prefixAttributes = Datamodel.PrefixAttributes.Where(attr => attr.Value != null).ToArray();

                if (prefixAttributes.Length == 0)
                {
                    Writer.Write(0);
                    return;
                }

                Writer.Write(1);
                Writer.Write(prefixAttributes.Length);

                foreach (var attr in prefixAttributes)
                {
                    Writer.Write(attr.Key);
                    WriteTypedValue(attr.Value, raw_string: true);
                }
            }

            void WriteBody(AttributeList elem)
            {
                var attributesIterated = elem is Element element ? Context.Attributes[element] : elem.GetAllAttributesForSerialization().ToArray();
                Writer.Write(attributesIterated.Length);
                foreach (var attr in attributesIterated)
                {
                    StringDict.WriteString(attr.Key, Writer);
                    WriteTypedValue(attr.Value, raw_string: false);
                }
            }

            /// <summary>
            /// Writes the type id of a value followed by the value itself, or by the item count and items for arrays.
            /// </summary>
            void WriteTypedValue(object? value, bool raw_string)
            {
                var attr_type = value == null ? typeof(Element) : value.GetType();
                var attr_type_id = TypeToId(attr_type, EncodingVersion);
                Writer.Write(attr_type_id);

                if (value == null || value is byte[] || !Datamodel.IsDatamodelArrayType(attr_type))
                {
                    WriteAttribute(value, raw_string);
                    return;
                }

                var array = (System.Collections.IList)value;
                Writer.Write(array.Count);
                foreach (var item in array)
                    WriteAttribute(item, true);
            }

            /// <param name="in_array">Whether the value is an array item or a prefix attribute, in which case strings are written inline rather than through the dictionary.</param>
            void WriteAttribute(object? value, bool in_array)
            {
                if (value == null)
                {
                    Writer.Write(-1);
                    return;
                }

                if (value is Element child_elem)
                {
                    if (child_elem.Stub)
                    {
                        Writer.Write(-2);
                        Writer.Write(child_elem.ID.ToString().ToArray()); // yes, ToString()!
                        Writer.Write((byte)0);
                    }
                    else
                        Writer.Write(ElementIndices[child_elem]);
                    return;
                }

                if (value is string string_value)
                {
                    if (EncodingVersion < 4 || in_array)
                        Writer.Write(string_value);
                    else
                        StringDict.WriteString(string_value, Writer);
                    return;
                }

                if (value is bool bool_value)
                {
                    Writer.Write(bool_value == true ? (byte)1 : (byte)0);
                    return;
                }

                if (value is byte[] binary_value)
                {
                    Writer.Write(binary_value.Length);
                    Writer.Write(binary_value);
                    return;
                }

                if (value is TimeSpan time_span)
                {
                    Writer.Write((int)(time_span.Ticks / (TimeSpan.TicksPerSecond / DatamodelTicksPerSecond)));
                    return;
                }

                if (value is Color colour_value)
                {
                    Writer.Write(colour_value.ToBytes());
                    return;
                }

                if (value is Vector2 vector2)
                {
                    Writer.Write(vector2.X);
                    Writer.Write(vector2.Y);
                    return;
                }
                if (value is Vector3 vector3)
                {
                    Writer.Write(vector3.X);
                    Writer.Write(vector3.Y);
                    Writer.Write(vector3.Z);
                    return;
                }
                if (value is QAngle qangle)
                {
                    Writer.Write(qangle.Pitch);
                    Writer.Write(qangle.Yaw);
                    Writer.Write(qangle.Roll);
                    return;
                }
                if (value is Vector4 vector4)
                {
                    Writer.Write(vector4.X);
                    Writer.Write(vector4.Y);
                    Writer.Write(vector4.Z);
                    Writer.Write(vector4.W);
                    return;
                }
                if (value is Quaternion quaternion)
                {
                    Writer.Write(quaternion.X);
                    Writer.Write(quaternion.Y);
                    Writer.Write(quaternion.Z);
                    Writer.Write(quaternion.W);
                    return;
                }
                if (value is Matrix4x4 matrix)
                {
                    Writer.Write(matrix.M11);
                    Writer.Write(matrix.M12);
                    Writer.Write(matrix.M13);
                    Writer.Write(matrix.M14);
                    Writer.Write(matrix.M21);
                    Writer.Write(matrix.M22);
                    Writer.Write(matrix.M23);
                    Writer.Write(matrix.M24);
                    Writer.Write(matrix.M31);
                    Writer.Write(matrix.M32);
                    Writer.Write(matrix.M33);
                    Writer.Write(matrix.M34);
                    Writer.Write(matrix.M41);
                    Writer.Write(matrix.M42);
                    Writer.Write(matrix.M43);
                    Writer.Write(matrix.M44);
                    return;
                }

                if (value is int intValue)
                {
                    Writer.Write(intValue);
                    return;
                }
                if (value is float floatValue)
                {
                    Writer.Write(floatValue);
                    return;
                }

                if (value is byte byteValue)
                {
                    Writer.Write(byteValue);
                    return;
                }

                if (value is ulong ulongValue)
                {
                    Writer.Write(ulongValue);
                    return;
                }

                throw new InvalidOperationException("Unrecognised output Type.");
            }
        }

        class DmxBinaryWriter : BinaryWriter
        {
            public DmxBinaryWriter(Stream output)
                : base(output, Datamodel.TextEncoding)
            { }

            /// <summary>
            /// Writes a null-terminated string to the underlying stream using <see cref="Datamodel.TextEncoding"/>.
            /// </summary>
            /// <param name="value"></param>
            [System.Security.SecuritySafeCritical]
            public override void Write(string value)
            {
                if (value != null)
                    base.Write(Datamodel.TextEncoding.GetBytes(value));
                base.Write((byte)0);
            }

            protected override void Dispose(bool disposing)
            {
                return; // don't mess with the base stream!
            }
        }
    }
}
