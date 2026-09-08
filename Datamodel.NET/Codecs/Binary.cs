using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Numerics;
using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Datamodel.Codecs
{
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

        /// <summary>
        /// Maps a type id of the stream to the attribute type, or to the item type when the id denotes an array.
        /// </summary>
        (Type Type, bool IsArray) IdToType(byte id)
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

            if (id >= type_list.Length || type_list[id] is not Type type)
            {
                throw new CodecException(String.Format("Unrecognised attribute type: {0}", id + 1));
            }

            return (type, array);
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
                    var count = LengthSize == sizeof(short) ? reader.ReadInt16() : reader.ReadInt32();
                    Strings.Capacity = count;
                    for (var i = 0; i < count; i++)
                        Strings.Add(Codec.ReadString_Raw(reader));
                }
            }

            /// <summary>
            /// Constructs an empty dictionary for writing. The encoder adds every string it meets, in the order it meets them.
            /// </summary>
            public StringDictionary(int encoding_version)
            {
                EncodingVersion = encoding_version;
                Dummy = EncodingVersion == 1;
                if (!Dummy)
                    Indices = [];
            }

            /// <summary>
            /// Adds a string to the table unless it is there already. Nothing is added for a version that writes every string in place.
            /// </summary>
            public void AddString(string? value)
            {
                if (Indices == null)
                    return;

                value ??= string.Empty;
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

        public Datamodel Decode(string encoding, int encoding_version, string format, int format_version, Stream stream, DeferredMode defer_mode, ElementTypeResolver resolver)
        {
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

            // the file states how many elements follow, so the tables that hold them are sized once
            ElementIndex.Capacity = num_elements;
            dm.AllElements.EnsureCapacity(num_elements);

            // read index
            Span<byte> id_bits = stackalloc byte[16];
            for (var i = 0; i < num_elements; i++)
            {
                var type = StringDict.ReadString(Reader);
                var name = EncodingVersion >= 4 ? StringDict.ReadString(Reader) : ReadString_Raw(Reader);
                Reader.BaseStream.ReadExactly(id_bits);
                if (!BitConverter.IsLittleEndian)
                    id_bits.Reverse();
                var id = new Guid(id_bits);

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

                for (var i = 0; i < num_attrs; i++)
                {
                    var name = StringDict.ReadString(Reader);
                    if (defer_mode == DeferredMode.Automatic)
                    {
                        CodecUtilities.AddDeferredAttribute(elem, name, Reader.BaseStream.Position);
                        SkipAttribute(Reader);
                    }
                    else
                    {
                        DecodeAttributeInto(dm, elem, name, Reader);
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
            var (type, isArray) = IdToType(reader.ReadByte());

            if (!isArray)
                return ReadValue(dm, TypeMap[type.TypeHandle], EncodingVersion < 4 || prefix, reader);

            return ReadArray(dm, type, reader.ReadInt32(), reader);
        }

        /// <summary>
        /// Reads an attribute of an element straight into the element, so that value types are stored inline without being boxed.
        /// </summary>
        void DecodeAttributeInto(Datamodel dm, AttributeList target, string name, BinaryReader reader)
        {
            var (type, isArray) = IdToType(reader.ReadByte());

            if (isArray)
            {
                target[name] = ReadArray(dm, type, reader.ReadInt32(), reader);
                return;
            }

            switch (TypeMap[type.TypeHandle])
            {
                case 0: target[name] = ReadElement(dm, reader); break;
                case 1: target.Set(name, reader.ReadInt32()); break;
                case 2: target.Set(name, reader.ReadSingle()); break;
                case 3: target.Set(name, reader.ReadBoolean()); break;
                case 4: target[name] = EncodingVersion < 4 ? ReadString_Raw(reader) : StringDict!.ReadString(reader); break;
                case 5: target[name] = reader.ReadBytes(reader.ReadInt32()); break;
                case 6: target.Set(name, TimeSpan.FromTicks(reader.ReadInt32() * (TimeSpan.TicksPerSecond / DatamodelTicksPerSecond))); break;
                case 7: target.Set(name, ReadColor(reader)); break;
                case 8: target.Set(name, ReadVector2(reader)); break;
                case 9: target.Set(name, ReadVector3(reader)); break;
                case 10: target.Set(name, ReadQAngle(reader)); break;
                case 11: target.Set(name, ReadVector4(reader)); break;
                case 12: target.Set(name, ReadQuaternion(reader)); break;
                case 13: target[name] = ReadMatrix4x4(reader); break;
                case 14: target.Set(name, reader.ReadByte()); break;
                case 15: target.Set(name, reader.ReadUInt64()); break;
                default: throw new ArgumentException("Cannot read value of type");
            }
        }

        /// <summary>
        /// Storage shared by the value type arrays of this stream, see <see cref="ArrayChunks"/>.
        /// </summary>
        readonly ArrayChunks Chunks = new();

        /// <summary>
        /// Reads an array attribute. Value types whose memory layout matches the stream are copied in one read into a slice of a shared chunk,
        /// instead of one boxed item at a time into a list of their own.
        /// </summary>
        System.Collections.IList ReadArray(Datamodel dm, Type type, int count, BinaryReader reader)
        {
            var typeId = TypeMap[type.TypeHandle];

            if (BitConverter.IsLittleEndian && count > 0)
            {
                switch (typeId)
                {
                    case 1: { var (buffer, offset) = ReadChunk<int>(count, reader); return new IntArray(buffer, offset, count); }
                    case 2: { var (buffer, offset) = ReadChunk<float>(count, reader); return new FloatArray(buffer, offset, count); }
                    case 3: { var (buffer, offset) = ReadChunk<bool>(count, reader); return new BoolArray(buffer, offset, count); }
                    case 7: { var (buffer, offset) = ReadChunk<Color>(count, reader); return new ColorArray(buffer, offset, count); }
                    case 8: { var (buffer, offset) = ReadChunk<Vector2>(count, reader); return new Vector2Array(buffer, offset, count); }
                    case 9: { var (buffer, offset) = ReadChunk<Vector3>(count, reader); return new Vector3Array(buffer, offset, count); }
                    case 11: { var (buffer, offset) = ReadChunk<Vector4>(count, reader); return new Vector4Array(buffer, offset, count); }
                    case 12: { var (buffer, offset) = ReadChunk<Quaternion>(count, reader); return new QuaternionArray(buffer, offset, count); }
                    case 13: { var (buffer, offset) = ReadChunk<Matrix4x4>(count, reader); return new MatrixArray(buffer, offset, count); }
                    case 14: { var (buffer, offset) = ReadChunk<byte>(count, reader); return new ByteArray(buffer, offset, count); }
                    case 15: { var (buffer, offset) = ReadChunk<ulong>(count, reader); return new UInt64Array(buffer, offset, count); }
                }
            }

            var array = CodecUtilities.MakeList(type, count);
            for (var i = 0; i < count; i++)
                array.Add(ReadValue(dm, typeId, true, reader));

            return array;
        }

        (T[] Buffer, int Offset) ReadChunk<T>(int count, BinaryReader reader) where T : unmanaged
        {
            var (buffer, offset) = Chunks.Rent<T>(count);
            reader.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan(offset, count)));
            return (buffer, offset);
        }

        void SkipAttribute(BinaryReader reader)
        {
            var (type, isArray) = IdToType(reader.ReadByte());

            int count = 1;

            if (isArray)
            {
                count = reader.ReadInt32();
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
                if (!StringDict!.Dummy && !isArray && EncodingVersion >= 4)
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
            else if (type == typeof(QAngle))
                length = sizeof(float) * 3;
            else if (type == typeof(int) || type == typeof(float))
                length = 4;
            else if (type == typeof(byte))
                length = sizeof(byte);
            else if (type == typeof(ulong))
                length = sizeof(ulong);
            else
                throw new CodecException($"Cannot skip an attribute of type {type.Name}.");

            reader.BaseStream.Seek(length * count, SeekOrigin.Current);
        }

        /// <summary>
        /// Writes a datamodel the way Valve's CDmSerializerBinary does: one pass from the root gathers the strings and fixes the order of the elements,
        /// a second pass writes the bodies. Attributes are read in the form their slots hold them, so no value is boxed, and an array of plain values is written in one piece.
        /// </summary>
        sealed class Encoder
        {
            readonly BinaryWriter Writer;
            readonly StringDictionary StringDict;
            readonly Datamodel Datamodel;
            readonly int EncodingVersion;

            /// <summary>The bodies in the order their index entries are written: the root, the prefix attributes when the version stores them as an element, then every element in the order it is first reached.</summary>
            readonly List<AttributeList> Order = [];
            readonly Dictionary<Element, int> Indices = [];

            /// <summary>Type ids of the inline kinds, filled in as they are met, since a version may not support every kind.</summary>
            readonly byte[] KindIds = new byte[16];
            readonly Dictionary<Type, byte> ArrayIds = [];
            readonly byte ElementId, StringId, BinaryId, MatrixId;

            public Encoder(BinaryWriter writer, Datamodel dm, int version)
            {
                EncodingVersion = version;
                Writer = writer;
                Datamodel = dm;
                StringDict = new StringDictionary(version);
                ElementId = TypeToId(typeof(Element), version);
                StringId = TypeToId(typeof(string), version);
                BinaryId = TypeToId(typeof(byte[]), version);
                MatrixId = TypeToId(typeof(Matrix4x4), version);
            }

            public void Encode()
            {
                Writer.Write(string.Format(CodecUtilities.HeaderPattern, "binary", EncodingVersion, Datamodel.Format, Datamodel.FormatVersion) + "\n");

                if (EncodingVersion >= 9)
                {
                    WritePrefixAttributes();
                }

                var hasPrefixElement = EncodingVersion >= 9 && Datamodel.PrefixAttributes.Count > 0;
                var root = Datamodel.Root;
                if (root != null && !root.Stub)
                {
                    Indices[root] = 0;
                    Order.Add(root);

                    // the prefix attributes are also stored as an unreferenced element right after the root
                    if (hasPrefixElement)
                        Order.Add(Datamodel.PrefixAttributes);

                    Gather(root);

                    if (hasPrefixElement)
                    {
                        StringDict.AddString(string.Empty);
                        StringDict.AddString(PrefixElementClass);
                        foreach (var attr in Datamodel.PrefixAttributes)
                        {
                            StringDict.AddString(attr.Key);
                            if (attr.Value is string stringValue)
                                StringDict.AddString(stringValue);
                        }
                    }
                }

                StringDict.WriteSelf(Writer);
                Writer.Write(Order.Count);

                Span<byte> id = stackalloc byte[16];
                foreach (var body in Order)
                {
                    var (className, name, elementId) = body is Element elem ? (elem.ClassName, elem.Name, elem.ID) : (PrefixElementClass, string.Empty, Datamodel.PrefixElementId);
                    StringDict.WriteString(className, Writer);
                    if (EncodingVersion >= 4) StringDict.WriteString(name, Writer);
                    else Writer.Write(name);
                    elementId.TryWriteBytes(id);
                    Writer.Write(id);
                }

                foreach (var body in Order)
                    WriteBody(body);
            }

            /// <summary>
            /// Adds the strings of an element to the table and reaches the elements it refers to, depth first in attribute order, which is the order of the index.
            /// </summary>
            void Gather(Element elem)
            {
                StringDict.AddString(elem.Name);
                StringDict.AddString(elem.ClassName);

                var visitor = new GatherVisitor(this);
                elem.VisitAttributes(ref visitor);
            }

            void Reach(Element? child)
            {
                if (child == null || child.Stub || Indices.ContainsKey(child))
                    return;

                Indices[child] = Order.Count;
                Order.Add(child);
                Gather(child);
            }

            readonly struct GatherVisitor(Encoder encoder) : IAttributeVisitor
            {
                public void Begin(int count)
                {
                }

                public void Visit(string name, AttributeKind kind, in InlineValue inline, object? reference)
                {
                    encoder.StringDict.AddString(name);

                    switch (reference)
                    {
                        case string stringValue:
                            encoder.StringDict.AddString(stringValue);
                            break;
                        case Element child:
                            encoder.Reach(child);
                            break;
                        case ElementArray children:
                            foreach (var child in children.AsSpan())
                                encoder.Reach(child);
                            break;
                        case IList<Element> children:
                            foreach (var child in children)
                                encoder.Reach(child);
                            break;
                    }
                }
            }

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
                    AttributeList.Classify(attr.Value, out var kind, out var inline, out var reference);
                    WriteValue(kind, in inline, reference, rawStrings: true);
                }
            }

            void WriteBody(AttributeList body)
            {
                var visitor = new WriteVisitor(this);
                body.VisitAttributes(ref visitor);
            }

            readonly struct WriteVisitor(Encoder encoder) : IAttributeVisitor
            {
                public void Begin(int count)
                {
                    encoder.Writer.Write(count);
                }

                public void Visit(string name, AttributeKind kind, in InlineValue inline, object? reference)
                {
                    encoder.StringDict.WriteString(name, encoder.Writer);
                    encoder.WriteValue(kind, in inline, reference, rawStrings: false);
                }
            }

            /// <summary>
            /// Writes the type id of a value and the value itself.
            /// </summary>
            /// <param name="rawStrings">Whether a string is written in place rather than as an index into the table, as the prefix attributes and array items are.</param>
            void WriteValue(AttributeKind kind, in InlineValue inline, object? reference, bool rawStrings)
            {
                if (kind != AttributeKind.Reference)
                {
                    Writer.Write(IdOf(kind));
                    WriteInline(kind, in inline);
                    return;
                }

                switch (reference)
                {
                    case null:
                        Writer.Write(ElementId);
                        Writer.Write(-1);
                        return;
                    case Element elem:
                        Writer.Write(ElementId);
                        WriteElement(elem);
                        return;
                    case string stringValue:
                        Writer.Write(StringId);
                        WriteString(stringValue, rawStrings);
                        return;
                    case byte[] binary:
                        Writer.Write(BinaryId);
                        Writer.Write(binary.Length);
                        Writer.Write(binary);
                        return;
                    case Matrix4x4 matrix:
                        Writer.Write(MatrixId);
                        WriteMatrix(in matrix);
                        return;
                    case IList array:
                        WriteArray(array);
                        return;
                    default:
                        throw new InvalidOperationException("Unrecognised output Type.");
                }
            }

            /// <summary>
            /// Writes an array with its type id. The items of an array whose memory layout matches the stream are written in one piece.
            /// </summary>
            void WriteArray(IList array)
            {
                Writer.Write(IdOf(array.GetType()));
                Writer.Write(array.Count);

                switch (array)
                {
                    case ElementArray elements:
                        foreach (var elem in elements.AsSpan())
                        {
                            if (elem == null)
                                Writer.Write(-1);
                            else
                                WriteElement(elem);
                        }
                        return;
                    case StringArray strings:
                        foreach (var stringValue in strings.AsSpan())
                            Writer.Write(stringValue);
                        return;
                    case BinaryArray binaries:
                        foreach (var binary in binaries.AsSpan())
                        {
                            if (binary == null)
                            {
                                Writer.Write(-1);
                                continue;
                            }

                            Writer.Write(binary.Length);
                            Writer.Write(binary);
                        }
                        return;
                    case TimeSpanArray times:
                        foreach (var time in times.AsSpan())
                            Writer.Write(ToTicks(time));
                        return;
                    case IntArray a: WriteItems(a.AsSpan()); return;
                    case FloatArray a: WriteItems(a.AsSpan()); return;
                    case BoolArray a: WriteItems(a.AsSpan()); return;
                    case ColorArray a: WriteItems(a.AsSpan()); return;
                    case Vector2Array a: WriteItems(a.AsSpan()); return;
                    case Vector3Array a: WriteItems(a.AsSpan()); return;
                    case Vector4Array a: WriteItems(a.AsSpan()); return;
                    case QuaternionArray a: WriteItems(a.AsSpan()); return;
                    case MatrixArray a: WriteItems(a.AsSpan()); return;
                    case ByteArray a: WriteItems(a.AsSpan()); return;
                    case UInt64Array a: WriteItems(a.AsSpan()); return;
                }

                foreach (var item in array)
                {
                    AttributeList.Classify(item, out var kind, out var inline, out var reference);
                    if (kind != AttributeKind.Reference)
                        WriteInline(kind, in inline);
                    else if (reference == null)
                        Writer.Write(-1);
                    else if (reference is Element elem)
                        WriteElement(elem);
                    else if (reference is string stringValue)
                        Writer.Write(stringValue);
                    else if (reference is byte[] binary)
                    {
                        Writer.Write(binary.Length);
                        Writer.Write(binary);
                    }
                    else if (reference is Matrix4x4 matrix)
                        WriteMatrix(in matrix);
                    else
                        throw new InvalidOperationException("Unrecognised output Type.");
                }
            }

            /// <summary>
            /// Writes items whose layout in memory is their layout in the stream: the scalars, the vectors and the four by four matrix, all little-endian floats and integers.
            /// </summary>
            void WriteItems<T>(ReadOnlySpan<T> items) where T : unmanaged
            {
                if (BitConverter.IsLittleEndian)
                {
                    Writer.Write(MemoryMarshal.AsBytes(items));
                    return;
                }

                foreach (var item in items)
                {
                    AttributeList.Classify(item, out var kind, out var inline, out var reference);
                    if (kind != AttributeKind.Reference)
                        WriteInline(kind, in inline);
                    else
                        WriteMatrix((Matrix4x4)reference!);
                }
            }

            void WriteInline(AttributeKind kind, in InlineValue inline)
            {
                switch (kind)
                {
                    case AttributeKind.Int: Writer.Write(inline.Int); return;
                    case AttributeKind.Float: Writer.Write(inline.Float); return;
                    case AttributeKind.Bool: Writer.Write(inline.Bool ? (byte)1 : (byte)0); return;
                    case AttributeKind.Byte: Writer.Write(inline.Byte); return;
                    case AttributeKind.UInt64: Writer.Write(inline.UInt64); return;
                    case AttributeKind.Time: Writer.Write(ToTicks(TimeSpan.FromTicks(inline.Ticks))); return;
                    case AttributeKind.Color:
                        Writer.Write(inline.Color.R);
                        Writer.Write(inline.Color.G);
                        Writer.Write(inline.Color.B);
                        Writer.Write(inline.Color.A);
                        return;
                    case AttributeKind.Vector2:
                        Writer.Write(inline.Vector2.X);
                        Writer.Write(inline.Vector2.Y);
                        return;
                    case AttributeKind.Vector3:
                        Writer.Write(inline.Vector3.X);
                        Writer.Write(inline.Vector3.Y);
                        Writer.Write(inline.Vector3.Z);
                        return;
                    case AttributeKind.QAngle:
                        Writer.Write(inline.QAngle.Pitch);
                        Writer.Write(inline.QAngle.Yaw);
                        Writer.Write(inline.QAngle.Roll);
                        return;
                    case AttributeKind.Vector4:
                        Writer.Write(inline.Vector4.X);
                        Writer.Write(inline.Vector4.Y);
                        Writer.Write(inline.Vector4.Z);
                        Writer.Write(inline.Vector4.W);
                        return;
                    case AttributeKind.Quaternion:
                        Writer.Write(inline.Quaternion.X);
                        Writer.Write(inline.Quaternion.Y);
                        Writer.Write(inline.Quaternion.Z);
                        Writer.Write(inline.Quaternion.W);
                        return;
                    default:
                        throw new InvalidOperationException("Unrecognised output Type.");
                }
            }

            void WriteMatrix(in Matrix4x4 matrix)
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
            }

            void WriteElement(Element elem)
            {
                if (elem.Stub)
                {
                    Writer.Write(-2);
                    Writer.Write(elem.ID.ToString().ToCharArray()); // yes, ToString()!
                    Writer.Write((byte)0);
                }
                else
                {
                    Writer.Write(Indices[elem]);
                }
            }

            void WriteString(string value, bool raw)
            {
                if (EncodingVersion < 4 || raw)
                    Writer.Write(value);
                else
                    StringDict.WriteString(value, Writer);
            }

            static int ToTicks(TimeSpan time) => (int)(time.Ticks / (TimeSpan.TicksPerSecond / DatamodelTicksPerSecond));

            byte IdOf(AttributeKind kind)
            {
                ref var id = ref KindIds[(int)kind];
                if (id == 0)
                    id = TypeToId(TypeOf(kind), EncodingVersion);
                return id;
            }

            byte IdOf(Type arrayType)
            {
                if (!ArrayIds.TryGetValue(arrayType, out var id))
                    ArrayIds[arrayType] = id = TypeToId(arrayType, EncodingVersion);
                return id;
            }

            static Type TypeOf(AttributeKind kind) => kind switch
            {
                AttributeKind.Int => typeof(int),
                AttributeKind.Float => typeof(float),
                AttributeKind.Bool => typeof(bool),
                AttributeKind.Byte => typeof(byte),
                AttributeKind.UInt64 => typeof(ulong),
                AttributeKind.Time => typeof(TimeSpan),
                AttributeKind.Color => typeof(Color),
                AttributeKind.Vector2 => typeof(Vector2),
                AttributeKind.Vector3 => typeof(Vector3),
                AttributeKind.Vector4 => typeof(Vector4),
                AttributeKind.Quaternion => typeof(Quaternion),
                AttributeKind.QAngle => typeof(QAngle),
                _ => throw new InvalidOperationException("Unrecognised output Type."),
            };
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
