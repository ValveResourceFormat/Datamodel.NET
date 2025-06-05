using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Numerics;
using System.IO;
using System.Runtime.CompilerServices;
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
        class KV2Writer : IDisposable
        {
            public int Indent
            {
                get { return indent_count; }
                set
                {
                    indent_count = value;
                    indent_string = "\n" + string.Concat(Enumerable.Repeat("    ", value));
                }
            }
            int indent_count = 0;
            string indent_string = "\n";
            readonly TextWriter Output;

            public KV2Writer(Stream output)
            {
                Output = new StreamWriter(output, Datamodel.TextEncoding);
            }

            public void Dispose()
            {
                Output.Dispose();
            }

            static string Sanitise(string value)
            {
                return value.Replace("\"", "\\\"");
            }

            /// <summary>
            /// Writes the string straight to the output steam, with no sanitisation.
            /// </summary>
            public void Write(string value)
            {
                Output.Write(value);
            }

            public void WriteTokens(params string[] values)
            {
                Output.Write('"' + string.Join("\" \"", values.Select(s => Sanitise(s))) + '"');
            }

            public void WriteLine()
            {
                Output.Write(indent_string);
            }

            /// <summary>
            /// Writes a new line followed by the given value
            /// </summary>
            public void WriteLine(string value)
            {
                WriteLine();
                Output.Write(value);
            }

            public void WriteTokenLine(params string[] values)
            {
                Output.Write(indent_string);
                WriteTokens(values);
            }

            public void TrimEnd(int count)
            {
                if (count > 0)
                {
                    Output.Flush();
                    var stream = ((StreamWriter)Output).BaseStream;
                    stream.SetLength(stream.Length - count);
                }
            }

            public void Flush()
            {
                Output.Flush();
            }
        }

        // Multi-referenced elements are written out as a separate block at the end of the file.
        // In-line only the id is written.
        Dictionary<Element, int> ReferenceCount = [];

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
                foreach (var attr in elem.GetAllAttributesForSerialization())
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

        void WriteAttribute(string name, int encodingVersion, Type type, object value, bool in_array, KV2Writer writer)
        {
            bool is_element = type == typeof(Element) || type.IsSubclassOf(typeof(Element));

            Type? inner_type = null;
            if (!in_array)
            {
                // TODO: subclass check in this method like above - and in all other places with == typeof(Element)
                inner_type = Datamodel.GetArrayInnerType(type);

                if (inner_type == typeof(byte) && type == typeof(byte[]))
                    inner_type = null; // serialize as binary at all times

                /*
                if (inner_type == typeof(byte) && !ValidAttributes[EncodingVersion].Contains(typeof(byte)))
                    inner_type = null; // fall back on the "binary" type in older KV2 versions
                */
            }

            // Elements are supported by all.
            if (!is_element && !ValidAttributes[encodingVersion].Contains(inner_type ?? type))
                throw new CodecException(type.Name + " is not valid in KeyValues2 " + encodingVersion);

            if (inner_type != null)
            {
                is_element = inner_type == typeof(Element);

                writer.WriteTokenLine(name, TypeNames[inner_type] + "_array");

                if (((System.Collections.IList)value).Count == 0)
                {
                    writer.Write(" [ ]");
                    return;
                }

                if (is_element) writer.WriteLine("[");
                else writer.Write(" [");

                writer.Indent++;
                foreach (var array_value in (System.Collections.IList)value)
                    WriteAttribute(string.Empty, encodingVersion, inner_type, array_value, true, writer);
                writer.Indent--;
                writer.TrimEnd(1); // remove trailing comma

                if (inner_type == typeof(Element)) writer.WriteLine("]");
                else writer.Write(" ]");
                return;
            }

            if (is_element)
            {
                var elem = (Element)value;
                var id = elem.ID.ToString();

                if (in_array)
                {
                    if (ShouldBeReferenced(elem))
                    {
                        writer.WriteTokenLine("element", id);
                    }
                    else
                    {
                        writer.WriteLine();
                        WriteElement(elem, encodingVersion, writer);
                    }

                    writer.Write(",");
                }
                else
                {
                    if (ShouldBeReferenced(elem))
                    {
                        writer.WriteTokenLine(name, "element", id);
                    }
                    else
                    {
                        writer.WriteLine($"\"{name}\" ");
                        WriteElement(elem, encodingVersion, writer);
                    }
                }
            }
            else
            {
                if (type == typeof(bool))
                    value = (bool)value ? 1 : 0;
                else if (type == typeof(float))
                    value = FormattableString.Invariant($"{(float)value}");
                else if (type == typeof(byte[]))
                    value = Convert.ToHexString((byte[])value).Replace("-", string.Empty, false, CultureInfo.InvariantCulture);
                else if (type == typeof(TimeSpan))
                    value = ((TimeSpan)value).TotalSeconds.ToString(CultureInfo.InvariantCulture);
                else if (type == typeof(Color))
                {
                    var castValue = (Color)value;
                    value = FormattableString.Invariant($"{castValue.R} {castValue.G} {castValue.B} {castValue.A}");
                }
                else if (value is ulong ulong_value)
                    value = $"0x{ulong_value.ToString("x", CultureInfo.InvariantCulture)}";
                else if (type == typeof(Vector2))
                {
                    var castValue = (Vector2)value;
                    value = FormattableString.Invariant($"{castValue.X} {castValue.Y}");
                }
                else if (type == typeof(Vector3))
                {
                    var castValue = (Vector3)value;
                    value = FormattableString.Invariant($"{castValue.X} {castValue.Y} {castValue.Z}");
                }
                else if (type == typeof(Vector4))
                {
                    var castValue = (Vector4)value;
                    value = FormattableString.Invariant($"{castValue.X} {castValue.Y} {castValue.Z} {castValue.W}");
                }
                else if (type == typeof(Quaternion))
                {
                    var castValue = (Quaternion)value;
                    value = FormattableString.Invariant($"{castValue.X} {castValue.Y} {castValue.Z} {castValue.W}");
                }
                else if (type == typeof(Matrix4x4))
                {
                    var castValue = (Matrix4x4)value;
                    var matrixString =
                        $"{castValue.M11} {castValue.M12} {castValue.M13} {castValue.M14}" +
                        $" {castValue.M21} {castValue.M22} {castValue.M23} {castValue.M24}" +
                        $" {castValue.M31} {castValue.M32} {castValue.M33} {castValue.M34}" +
                        $" {castValue.M41} {castValue.M42} {castValue.M43} {castValue.M44}";

                    value = FormattableString.Invariant(FormattableStringFactory.Create(matrixString));
                }
                else if (value is QAngle castValue)
                {
                    value = FormattableString.Invariant($"{castValue.Pitch} {castValue.Yaw} {castValue.Roll}");
                }

                if (in_array)
                    writer.Write(FormattableString.Invariant($" \"{value}\","));
                else
                    writer.WriteTokenLine(name, TypeNames[type], FormattableString.Invariant($"{value}"));
            }

        }

        private bool ShouldBeReferenced(Element? elem)
        {
            if (elem is null)
            {
                return false;
            }

            return SupportsReferenceIds && (elem == null || ReferenceCount.TryGetValue(elem, out var refCount) && refCount > 1);
        }

        void WriteElement(Element element, int encodingVersion, KV2Writer writer)
        {
            if (TypeNames.ContainsValue(element.ClassName))
                throw new CodecException($"Element {element.ID} uses reserved type name \"{element.ClassName}\"");
            writer.WriteTokens(element.ClassName);
            writer.WriteLine("{");
            writer.Indent++;

            if (SupportsReferenceIds)
                writer.WriteTokenLine("id", "elementid", element.ID.ToString());

            // Skip empty names right now.
            if (!string.IsNullOrEmpty(element.Name))
            {
                writer.WriteTokenLine("name", "string", element.Name);
            }

            foreach (var attr in element.GetAllAttributesForSerialization())
            {
                if (attr.Value != null)
                    WriteAttribute(attr.Key, encodingVersion, attr.Value.GetType(), attr.Value, false, writer);
            }

            writer.Indent--;
            writer.WriteLine("}");
        }

        public void Encode(Datamodel dm, string encoding, int encodingVersion, Stream stream)
        {
            var writer = new KV2Writer(stream);

            SupportsReferenceIds = encoding != "keyvalues2_noids";

            writer.Write(String.Format(CodecUtilities.HeaderPattern, encoding, encodingVersion, dm.Format, dm.FormatVersion));
            writer.WriteLine();

            ReferenceCount = [];

            if (encodingVersion >= 4 && dm.PrefixAttributes.Count > 0)
            {
                writer.WriteTokens("$prefix_element$");
                writer.WriteLine("{");
                writer.Indent++;
                writer.WriteTokenLine("id", "elementid", Guid.NewGuid().ToString());
                foreach (var attr in dm.PrefixAttributes)
                    if (attr.Value != null)
                    {
                        WriteAttribute(attr.Key, encodingVersion, attr.Value.GetType(), attr.Value, false, writer);
                    }
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine();
            }

            if (SupportsReferenceIds)
                CountReferences(dm.Root);

            if (dm.Root != null)
            {
                WriteElement(dm.Root, encodingVersion, writer);
            }
            writer.WriteLine();

            if (SupportsReferenceIds)
            {
                foreach (var pair in ReferenceCount.Where(pair => pair.Value > 1))
                {
                    if (pair.Key == dm.Root)
                        continue;
                    writer.WriteLine();
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
            public Dictionary<IList, List<Guid>> ListRefs = [];

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

            public void HandleListRefs(IList list, Guid id)
            {
                ListRefs.TryGetValue(list, out var guidList);

                if (guidList == null)
                {
                    guidList = [];
                    ListRefs.Add(list, guidList);
                }

                guidList.Add(id);
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
                    TokenBuilder.Append(c);
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
                                intermediateData.HandleListRefs(array, new Guid(id_s));
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

            foreach (var list in intermediateData.ListRefs)
            {
                foreach (var id in list.Value)
                {
                    var elemToAdd = dataModel.AllElements[id];
                    list.Key.Add(elemToAdd);
                }
            }

            return dataModel;
        }
        #endregion
    }
}
