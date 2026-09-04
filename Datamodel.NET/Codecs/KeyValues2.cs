using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Numerics;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using System.Collections;

namespace Datamodel.Codecs
{
    [CodecFormat("keyvalues2", 1)]
    [CodecFormat("keyvalues2", 2)]
    [CodecFormat("keyvalues2", 3)]
    [CodecFormat("keyvalues2", 4)]
    [CodecFormat("keyvalues2_noids", 1)]
    [CodecFormat("keyvalues2_noids", 2)]
    [CodecFormat("keyvalues2_noids", 3)]
    [CodecFormat("keyvalues2_noids", 4)]
    class KeyValues2 : ICodec
    {
        static readonly Dictionary<Type, string> TypeNames = [];
        static readonly Dictionary<int, Type[]> ValidAttributes = [];
        static KeyValues2()
        {
            TypeNames[typeof(Element)] = "element";
            TypeNames[typeof(int)] = "int";
            TypeNames[typeof(float)] = "float";
            TypeNames[typeof(bool)] = "bool";
            TypeNames[typeof(string)] = "string";
            TypeNames[typeof(byte[])] = "binary";
            TypeNames[typeof(TimeSpan)] = "time";
            TypeNames[typeof(Color)] = "color";
            TypeNames[typeof(Vector2)] = "vector2";
            TypeNames[typeof(Vector3)] = "vector3";
            TypeNames[typeof(Vector4)] = "vector4";
            TypeNames[typeof(Quaternion)] = "quaternion";
            TypeNames[typeof(Matrix4x4)] = "matrix";

            ValidAttributes[1] = ValidAttributes[2] = ValidAttributes[3] = TypeNames.Select(kv => kv.Key).ToArray();

            TypeNames[typeof(byte)] = "uint8";
            TypeNames[typeof(ulong)] = "uint64";
            TypeNames[typeof(QAngle)] = "qangle";

            ValidAttributes[4] = TypeNames.Select(kv => kv.Key).ToArray();
        }

        #region Encode
        /// <summary>
        /// Writes lines with tab indentation and LF line endings, as the reference serializer does.
        /// </summary>
        class KV2Writer : IDisposable
        {
            public int Indent { get; set; }

            readonly TextWriter Output;

            public KV2Writer(Stream output)
            {
                Output = new StreamWriter(output, Datamodel.TextEncoding);
            }

            public void Dispose()
            {
                Output.Dispose();
            }

            public static string Sanitise(string value)
            {
                return value
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
            }

            public static string Token(string value) => "\"" + Sanitise(value) + "\"";

            public void WriteLine(string line)
            {
                for (var i = 0; i < Indent; i++)
                    Output.Write('\t');

                Output.Write(line);
                Output.Write('\n');
            }

            public void WriteLine()
            {
                Output.Write('\n');
            }

            public void Flush()
            {
                Output.Flush();
            }
        }

        // Elements referenced more than once are written as separate blocks after the root and referred to by id.
        // Elements referenced once are written inline.
        Dictionary<Element, int> ReferenceCount = [];
        SerializationContext Context = new();

        bool SupportsReferenceIds;

        void CountReferences(Element? elem)
        {
            if (elem is null)
            {
                return;
            }

            if (ReferenceCount.ContainsKey(elem))
                ReferenceCount[elem]++;
            else
            {
                ReferenceCount[elem] = 1;
                foreach (var attr in Context.Attributes[elem])
                {
                    if (attr.Value == null)
                        continue;

                    if (attr.Value is Element child_elem)
                    {
                        CountReferences(child_elem);
                    }
                    else if (attr.Value is IEnumerable<Element> enumerable)
                    {
                        foreach (var array_elem in enumerable.Where(c => c != null))
                            CountReferences(array_elem);
                    }
                }
            }
        }

        static string FormatFloat(float value)
        {
            // ten decimals with trailing zeros dropped, as the reference serializer prints them
            return ((double)value).ToString("0.##########", CultureInfo.InvariantCulture);
        }

        static string FormatFloats(params float[] values)
        {
            return string.Join(" ", values.Select(FormatFloat));
        }

        static string FormatValue(object value)
        {
            return value switch
            {
                string stringValue => stringValue,
                bool boolValue => boolValue ? "1" : "0",
                int intValue => intValue.ToString(CultureInfo.InvariantCulture),
                float floatValue => FormatFloat(floatValue),
                byte byteValue => byteValue.ToString(CultureInfo.InvariantCulture),
                ulong ulongValue => "0x" + ulongValue.ToString("x", CultureInfo.InvariantCulture),
                byte[] binaryValue => Convert.ToHexString(binaryValue),
                TimeSpan timeValue => FormatFloat((float)timeValue.TotalSeconds),
                Color colorValue => FormattableString.Invariant($"{colorValue.R} {colorValue.G} {colorValue.B} {colorValue.A}"),
                Vector2 v => FormatFloats(v.X, v.Y),
                Vector3 v => FormatFloats(v.X, v.Y, v.Z),
                Vector4 v => FormatFloats(v.X, v.Y, v.Z, v.W),
                Quaternion q => FormatFloats(q.X, q.Y, q.Z, q.W),
                QAngle a => FormatFloats(a.Pitch, a.Yaw, a.Roll),
                Matrix4x4 m => FormatFloats(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44),
                _ => throw new CodecException($"Cannot serialize a value of type {value.GetType().Name} to KeyValues2"),
            };
        }

        void WriteAttribute(string name, int encodingVersion, object? value, KV2Writer writer)
        {
            var nameToken = KV2Writer.Token(name);

            if (value is null || value is Element)
            {
                WriteElementAttribute(nameToken, encodingVersion, (Element?)value, writer);
                return;
            }

            var type = value.GetType();

            // a byte[] is always serialized as "binary", never as a uint8_array
            var innerType = type == typeof(byte[]) ? null : Datamodel.GetArrayInnerType(type);

            if (innerType != null)
            {
                if (!ValidAttributes[encodingVersion].Contains(innerType))
                    throw new CodecException(innerType.Name + " is not valid in KeyValues2 " + encodingVersion);

                WriteArrayAttribute(nameToken, encodingVersion, innerType, (IList)value, writer);
                return;
            }

            if (!ValidAttributes[encodingVersion].Contains(type))
                throw new CodecException(type.Name + " is not valid in KeyValues2 " + encodingVersion);

            writer.WriteLine($"{nameToken} {KV2Writer.Token(TypeNames[type])} {KV2Writer.Token(FormatValue(value))}");
        }

        void WriteElementAttribute(string nameToken, int encodingVersion, Element? elem, KV2Writer writer)
        {
            if (elem is null || ShouldBeReferenced(elem))
            {
                writer.WriteLine($"{nameToken} \"element\" \"{(elem is null ? string.Empty : elem.ID.ToString())}\"");
                return;
            }

            writer.WriteLine($"{nameToken} {KV2Writer.Token(elem.ClassName)}");
            WriteElementBody(elem, encodingVersion, writer);
            writer.WriteLine("}");

            // the reference serializer leaves a blank line after an inline element
            writer.WriteLine();
        }

        void WriteArrayAttribute(string nameToken, int encodingVersion, Type innerType, IList array, KV2Writer writer)
        {
            writer.WriteLine($"{nameToken} {KV2Writer.Token(TypeNames[innerType] + "_array")} ");
            writer.WriteLine("[");
            writer.Indent++;

            for (var i = 0; i < array.Count; i++)
            {
                var separator = i == array.Count - 1 ? string.Empty : ",";
                var item = array[i];

                if (innerType == typeof(Element))
                {
                    var elem = (Element?)item;

                    if (elem is null || ShouldBeReferenced(elem))
                    {
                        writer.WriteLine($"\"element\" \"{(elem is null ? string.Empty : elem.ID.ToString())}\"{separator}");
                    }
                    else
                    {
                        writer.WriteLine(KV2Writer.Token(elem.ClassName));
                        WriteElementBody(elem, encodingVersion, writer);
                        writer.WriteLine("}" + separator);
                    }
                }
                else
                {
                    writer.WriteLine(KV2Writer.Token(FormatValue(item!)) + separator);
                }
            }

            writer.Indent--;
            writer.WriteLine("]");
        }

        private bool ShouldBeReferenced(Element elem)
        {
            return SupportsReferenceIds && ReferenceCount.TryGetValue(elem, out var refCount) && refCount > 1;
        }

        /// <summary>
        /// Writes the opening brace, id, name and attributes of an element. The caller writes the class name before and the closing brace after.
        /// </summary>
        void WriteElementBody(Element element, int encodingVersion, KV2Writer writer)
        {
            if (TypeNames.ContainsValue(element.ClassName))
                throw new CodecException($"Element {element.ID} uses reserved type name \"{element.ClassName}\"");

            writer.WriteLine("{");
            writer.Indent++;

            if (SupportsReferenceIds)
                writer.WriteLine($"\"id\" \"elementid\" \"{element.ID}\"");

            if (!string.IsNullOrEmpty(element.Name))
                writer.WriteLine($"\"name\" \"string\" {KV2Writer.Token(element.Name)}");

            foreach (var attr in Context.Attributes[element])
                WriteAttribute(attr.Key, encodingVersion, attr.Value, writer);

            writer.Indent--;
        }

        void WriteElement(Element element, int encodingVersion, KV2Writer writer)
        {
            writer.WriteLine(KV2Writer.Token(element.ClassName));
            WriteElementBody(element, encodingVersion, writer);
            writer.WriteLine("}");
        }

        public void Encode(Datamodel dm, string encoding, int encodingVersion, Stream stream)
        {
            Context = new SerializationContext();
            var writer = new KV2Writer(stream);

            SupportsReferenceIds = encoding != "keyvalues2_noids";

            writer.WriteLine(string.Format(CodecUtilities.HeaderPattern, encoding, encodingVersion, dm.Format, dm.FormatVersion));

            ReferenceCount = [];

            if (encodingVersion >= 4 && dm.PrefixAttributes.Count > 0)
            {
                writer.WriteLine("\"$prefix_element$\"");
                writer.WriteLine("{");
                writer.Indent++;
                writer.WriteLine($"\"id\" \"elementid\" \"{dm.PrefixElementId}\"");
                foreach (var attr in dm.PrefixAttributes)
                    WriteAttribute(attr.Key, encodingVersion, attr.Value, writer);
                writer.Indent--;
                writer.WriteLine("}");
            }

            if (SupportsReferenceIds)
                CountReferences(dm.Root);

            if (dm.Root != null)
            {
                WriteElement(dm.Root, encodingVersion, writer);
                writer.WriteLine();
            }

            if (SupportsReferenceIds)
            {
                foreach (var pair in ReferenceCount.Where(pair => pair.Value > 1))
                {
                    if (pair.Key == dm.Root)
                        continue;

                    WriteElement(pair.Key, encodingVersion, writer);
                    writer.WriteLine();
                }
            }

            writer.Flush();
        }
        #endregion

        #region Decode

        private class IntermediateData
        {
            // these store element refs while we process the elements, once were done
            // we can go trough these and actually create the attributes
            // and add the elements to lists
            public Dictionary<Element, List<(string, Guid)>> PropertiesToAdd = [];

            // array items referenced by id keep their slot (filled with null while parsing) and are resolved in place afterwards
            public List<(IList List, int Index, Guid Id)> ListRefs = [];

            public void HandleElementProp(Element? element, string attrName, Guid id)
            {
                if (element is null)
                {
                    throw new InvalidDataException("Trying to handle the propery of an invalid element");
                }

                PropertiesToAdd.TryGetValue(element, out var attrList);

                if (attrList == null)
                {
                    attrList = [];
                    PropertiesToAdd.Add(element, attrList);
                }

                attrList.Add((attrName, id));

            }

            public void HandleListRefs(ElementArray list, Guid id)
            {
                list.Add(null!);
                ListRefs.Add((list, list.Count - 1, id));
            }
        }

        readonly StringBuilder TokenBuilder = new();
        int Line = 0;
        string Decode_NextToken(StreamReader reader)
        {
            TokenBuilder.Clear();
            bool escaped = false;
            bool in_block = false;
            while (true)
            {
                var read = reader.Read();
                if (read == -1) throw new EndOfStreamException();
                var c = (char)read;
                if (escaped)
                {
                    TokenBuilder.Append(c switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => c,
                    });
                    escaped = false;
                    continue;
                }
                switch (c)
                {
                    case '"':
                        if (in_block) return TokenBuilder.ToString();
                        in_block = true;
                        break;
                    case '\\':
                        escaped = true; break;
                    case '\r':
                    case '\n':
                        Line++;
                        if (in_block) TokenBuilder.Append(c);
                        break;
                    case '{':
                    case '}':
                    case '[':
                    case ']':
                        if (!in_block)
                            return c.ToString();
                        else goto default;
                    default:
                        if (in_block) TokenBuilder.Append(c);
                        break;
                }
            }
        }

        Element? Decode_ParseElement(IElementFactory elementFactory, string class_name, ReflectionParams reflectionParams, StreamReader reader, Datamodel dataModel, IntermediateData intermediateData)
        {
            string elem_class = class_name ?? Decode_NextToken(reader);
            string elem_name = string.Empty;
            string elem_id = string.Empty;
            Element? elem = null;

            string next = Decode_NextToken(reader);
            if (next != "{") throw new CodecException($"Expected Element opener, got '{next}'.");
            while (true)
            {
                next = Decode_NextToken(reader);
                if (next == "}") break;

                var attr_name = next;
                var attr_type_s = Decode_NextToken(reader);
                var attr_type = TypeNames.FirstOrDefault(kv => kv.Value == attr_type_s.Split('_')[0]).Key;

                if (elem == null && attr_name == "id" && attr_type_s == "elementid")
                {
                    elem_id = Decode_NextToken(reader);
                    var id = new Guid(elem_id);
                    if (elem_class != "$prefix_element$")
                    {
                        CodecUtilities.TryConstructCustomElement(elementFactory, reflectionParams, dataModel, elem_class, elem_name, id, out elem);
                        elem ??= new Element(dataModel, elem_name, id, elem_class);
                    }
                    else
                    {
                        dataModel.PrefixElementId = id;
                    }

                    continue;
                }

                if (attr_name == "name" && attr_type == typeof(string))
                {
                    elem_name = Decode_NextToken(reader);
                    if (elem != null)
                        elem.Name = elem_name;
                    continue;
                }

                if (attr_type_s == "element")
                {
                    var id_s = Decode_NextToken(reader);

                    // the attribute keeps its position, it is filled in once every element has been parsed; an empty id is a null reference
                    elem?.Add(attr_name, null);

                    if (!string.IsNullOrEmpty(id_s))
                    {
                        intermediateData.HandleElementProp(elem, attr_name, new Guid(id_s));
                    }
                    continue;
                }

                object? attr_value = null;

                if (attr_type == null)
                    attr_value = Decode_ParseElement(elementFactory, attr_type_s, reflectionParams, reader, dataModel, intermediateData);
                else if (attr_type_s.EndsWith("_array"))
                {
                    var array = CodecUtilities.MakeList(attr_type, 5); // assume 5 items
                    attr_value = array;

                    next = Decode_NextToken(reader);
                    if (next != "[") throw new CodecException(String.Format("Expected array opener, got '{0}'.", next));
                    while (true)
                    {
                        next = Decode_NextToken(reader);
                        if (next == "]") break;

                        if (next == "element") // Element ID reference
                        {
                            var id_s = Decode_NextToken(reader);

                            if (!string.IsNullOrEmpty(id_s))
                            {
                                intermediateData.HandleListRefs((ElementArray)array, new Guid(id_s));
                            }
                            else
                            {
                                ((ElementArray)array).Add(null!);
                            }
                        }
                        // inline Element
                        else if (attr_type == typeof(Element))
                        {
                            array.Add(Decode_ParseElement(elementFactory, next, reflectionParams, reader, dataModel, intermediateData));
                        }
                        // normal value
                        else
                        {
                            array.Add(Decode_ParseValue(elementFactory, attr_type, next, reflectionParams, reader, dataModel, intermediateData));
                        }
                    }
                }
                else
                    attr_value = Decode_ParseValue(elementFactory, attr_type, Decode_NextToken(reader), reflectionParams, reader, dataModel, intermediateData);

                if (elem != null)
                    elem.Add(attr_name, attr_value);
                else
                    dataModel.PrefixAttributes[attr_name] = attr_value;
            }
            return elem;
        }

        object? Decode_ParseValue(IElementFactory elementFactory, Type type, string value, ReflectionParams reflectionParams, StreamReader reader, Datamodel dataModel, IntermediateData intermediateData)
        {
            if (type == typeof(string))
                return value;

            value = value.Trim();

            if (type == typeof(Element))
                return Decode_ParseElement(elementFactory, value, reflectionParams, reader, dataModel, intermediateData);
            if (type == typeof(int))
                return int.Parse(value, CultureInfo.InvariantCulture);
            else if (type == typeof(float))
                return float.Parse(value, CultureInfo.InvariantCulture);
            else if (type == typeof(bool))
                return byte.Parse(value, CultureInfo.InvariantCulture) == 1;
            else if (type == typeof(byte[]))
            {
                // need to sanitise input here because for example when exporting map as txt,
                // hammer will format the binary data to fit nicer on the screen by inserting two tabs
                var sb = new StringBuilder(value.Length);
                foreach (char c in value)
                {
                    switch (c)
                    {
                        case '\\':
                        case '\r':
                        case '\n':
                        case '\t':
                        case ' ':
                            continue;
                        default:
                            sb.Append(c);
                            break;
                    }
                }
                value = sb.ToString();

                byte[] result = new byte[value.Length / 2];

                for (int i = 0; i * 2 < value.Length; i++)
                {
                    var slice = value.AsSpan(i * 2, 2);
                    result[i] = byte.Parse(slice, System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }

                return result;
            }
            else if (type == typeof(TimeSpan))
                return TimeSpan.FromTicks((long)(double.Parse(value, CultureInfo.InvariantCulture) * TimeSpan.TicksPerSecond));

            var num_list = value.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries);

            if (type == typeof(Color))
            {
                var rgba = num_list.Select(i => byte.Parse(i, CultureInfo.InvariantCulture)).ToArray();
                return Color.FromBytes(rgba);
            }

            if (type == typeof(ulong)) return ulong.Parse(value.Remove(0, 2), System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (type == typeof(byte)) return byte.Parse(value, CultureInfo.InvariantCulture);

            var f_list = num_list.Select(i => float.Parse(i, CultureInfo.InvariantCulture)).ToArray();
            if (type == typeof(Vector2)) return new Vector2(f_list[0], f_list[1]);
            else if (type == typeof(Vector3)) return new Vector3(f_list[0], f_list[1], f_list[2]);
            else if (type == typeof(Vector4)) return new Vector4(f_list[0], f_list[1], f_list[2], f_list[3]);
            else if (type == typeof(Quaternion)) return new Quaternion(f_list[0], f_list[1], f_list[2], f_list[3]);
            else if (type == typeof(Matrix4x4)) return new Matrix4x4(
                f_list[0], f_list[1], f_list[2], f_list[3],
                f_list[4], f_list[5], f_list[6], f_list[7],
                f_list[8], f_list[9], f_list[10], f_list[11],
                f_list[12], f_list[13], f_list[14], f_list[15]);
            else if (type == typeof(QAngle)) return new QAngle(f_list[0], f_list[1], f_list[2]);

            else throw new ArgumentException($"Internal error: ParseValue passed unsupported type: {type}.");
        }

        public Datamodel Decode(string encoding, int encoding_version, string format, int format_version, Stream stream, DeferredMode defer_mode, ReflectionParams reflectionParams)
        {
            var elementFactoryTypes = CodecUtilities.GetIElementFactoryClasses();
            var elementFactory = (IElementFactory)Activator.CreateInstance(elementFactoryTypes.First());

            var dataModel = new Datamodel(format, format_version);

            if (encoding == "keyvalues2_noids")
                throw new NotImplementedException("KeyValues2_noids decoding not implemented.");

            stream.Seek(0, SeekOrigin.Begin);
            var reader = new StreamReader(stream, Datamodel.TextEncoding);
            reader.ReadLine(); // skip DMX header
            Line = 1;
            string next;

            var intermediateData = new IntermediateData();

            while (true)
            {
                try
                { next = Decode_NextToken(reader); }
                catch (EndOfStreamException)
                { break; }

                try
                { Decode_ParseElement(elementFactory, next, reflectionParams, reader, dataModel, intermediateData); }
                catch (Exception err)
                { throw new CodecException($"KeyValues2 decode failed on line {Line}:\n\n{err.Message}", err); }
            }

            foreach (var propList in intermediateData.PropertiesToAdd)
            {
                foreach (var prop in propList.Value)
                {
                    propList.Key.Add(prop.Item1, dataModel.AllElements[prop.Item2]);
                }

            }

            foreach (var (list, index, id) in intermediateData.ListRefs)
            {
                list[index] = dataModel.AllElements[id];
            }

            return dataModel;
        }
        #endregion
    }
}
