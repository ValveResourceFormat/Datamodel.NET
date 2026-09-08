using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using TUnit.Assertions.Enums;
using Datamodel;
using Tests.VMAP;
using DM = Datamodel.Datamodel;

namespace Datamodel_Tests
{
    /// <summary>
    /// Loading a file and saving it again must reproduce every element, every attribute and the prefix attributes,
    /// whether the elements were deserialized as plain <see cref="Element"/>s or as typed subclasses.
    /// </summary>
    public class RoundTripTests
    {
        static string Resource(string name) => Path.Combine(TestContext.TestDirectory!, "Resources", name);

        // a map made for this purpose: every node class, all selection set kinds, nested prefabs and instances,
        // subdivision, vertex paint, baked lighting, a thumbnail and asset references in the prefix
        public static IEnumerable<string> VmapFiles() =>
        [
            "roundtrip_test.vmap",
            Path.Combine("prefabs", "roundtrip_test_prefab1.vmap"),
            Path.Combine("prefabs", "roundtrip_test_prefab2.vmap"),
            Path.Combine("prefabs", "roundtrip_test_prefab3.vmap"),
        ];

        [Test]
        [MethodDataSource(nameof(VmapFiles))]
        public async Task Binary_Untyped(string file)
        {
            using var original = DM.Load(Resource(file), Datamodel.Codecs.DeferredMode.Disabled);
            var saved = Save(original);

            using var reloaded = DM.Load(saved);
            await AssertEquivalent(original, reloaded, orderSensitive: true);
            await Assert.That(reloaded.PrefixElementId).IsEqualTo(original.PrefixElementId);

            await Assert.That(Save(reloaded)).IsEquivalentTo(saved, CollectionOrdering.Matching).Because("saving the reloaded datamodel must reproduce the same bytes");
        }

        [Test]
        [MethodDataSource(nameof(VmapFiles))]
        public async Task Binary_PrefixElementIsNotAnOrphan(string file)
        {
            using var dm = DM.Load(Resource(file), Datamodel.Codecs.DeferredMode.Disabled);

            await Assert.That(dm.PrefixAttributes.Keys).Contains("map_asset_references");

            var reachable = new HashSet<Element>();
            Visit(dm.Root);
            await Assert.That(dm.AllElements.Count).IsEqualTo(reachable.Count).Because("every element must be reachable from the root");

            void Visit(Element? element)
            {
                if (element == null || !reachable.Add(element))
                    return;

                foreach (var attr in element)
                {
                    if (attr.Value is Element child)
                        Visit(child);
                    else if (attr.Value is IEnumerable<Element> children)
                        foreach (var arrayChild in children)
                            Visit(arrayChild);
                }
            }
        }

        [Test]
        [MethodDataSource(nameof(VmapFiles))]
        public async Task Binary_Typed(string file)
        {
            using var original = DM.Load(Resource(file), Datamodel.Codecs.DeferredMode.Disabled);
            using var typed = DM.Load<CMapRootElement>(Resource(file));

            await Assert.That(typed.Root).IsTypeOf<CMapRootElement>();

            // typed elements write their class properties first, in declaration order, so only the set of attributes is compared
            await AssertEquivalent(original, typed, orderSensitive: false);

            var saved = Save(typed);
            using var reloaded = DM.Load(saved);
            await AssertEquivalent(original, reloaded, orderSensitive: false);

            await Assert.That(Save(reloaded)).IsEquivalentTo(saved, CollectionOrdering.Matching);
        }

        [Test]
        [MethodDataSource(nameof(VmapFiles))]
        public async Task KeyValues2_Untyped(string file)
        {
            using var original = DM.Load(Resource(file), Datamodel.Codecs.DeferredMode.Disabled);

            using var text = new MemoryStream();
            original.Save(text, "keyvalues2", 4);

            using var reloaded = DM.Load(text.ToArray());

            // keyvalues2 prints floats with ten decimals, so values that small lose precision in that encoding
            await AssertEquivalent(original, reloaded, orderSensitive: true, floatTolerance: 1e-9);
            await Assert.That(reloaded.PrefixElementId).IsEqualTo(original.PrefixElementId);

            using var text2 = new MemoryStream();
            reloaded.Save(text2, "keyvalues2", 4);
            await Assert.That(text2.ToArray()).IsEquivalentTo(text.ToArray(), CollectionOrdering.Matching);
        }

        [Test]
        public async Task KeyValues2_MatchesReferenceLayout()
        {
            // tab indentation, one array item per line, inline elements followed by a blank line,
            // elements referenced more than once written after the root, as Valve's serializer lays the text out
            using var dm = new DM("test", 1);
            dm.PrefixElementId = new Guid("00000000-0000-0000-0000-000000000001");
            dm.PrefixAttributes["refs"] = new StringArray(["a", "b"]);

            var root = new Element(dm, "root", new Guid("00000000-0000-0000-0000-000000000002"), "DmeRoot");
            var child = new Element(dm, string.Empty, new Guid("00000000-0000-0000-0000-000000000003"), "DmeChild");
            var shared = new Element(dm, string.Empty, new Guid("00000000-0000-0000-0000-000000000004"), "DmeShared");
            var item = new Element(dm, string.Empty, new Guid("00000000-0000-0000-0000-000000000005"), "DmeItem");
            dm.Root = root;

            child["value"] = 1;
            shared["flag"] = true;
            root["child"] = child;
            root["shared"] = shared;
            root["list"] = new ElementArray([shared, item]);
            root["empty"] = new IntArray();
            root["nothing"] = null;

            using var text = new MemoryStream();
            dm.Save(text, "keyvalues2", 4);

            var expected = string.Join("\n",
            [
                "<!-- dmx encoding keyvalues2 4 format test 1 -->",
                "\"$prefix_element$\"",
                "{",
                "\t\"id\" \"elementid\" \"00000000-0000-0000-0000-000000000001\"",
                "\t\"refs\" \"string_array\" ",
                "\t[",
                "\t\t\"a\",",
                "\t\t\"b\"",
                "\t]",
                "}",
                "\"DmeRoot\"",
                "{",
                "\t\"id\" \"elementid\" \"00000000-0000-0000-0000-000000000002\"",
                "\t\"name\" \"string\" \"root\"",
                "\t\"child\" \"DmeChild\"",
                "\t{",
                "\t\t\"id\" \"elementid\" \"00000000-0000-0000-0000-000000000003\"",
                "\t\t\"value\" \"int\" \"1\"",
                "\t}",
                "",
                "\t\"shared\" \"element\" \"00000000-0000-0000-0000-000000000004\"",
                "\t\"list\" \"element_array\" ",
                "\t[",
                "\t\t\"element\" \"00000000-0000-0000-0000-000000000004\",",
                "\t\t\"DmeItem\"",
                "\t\t{",
                "\t\t\t\"id\" \"elementid\" \"00000000-0000-0000-0000-000000000005\"",
                "\t\t}",
                "\t]",
                "\t\"empty\" \"int_array\" ",
                "\t[",
                "\t]",
                "\t\"nothing\" \"element\" \"\"",
                "}",
                "",
                "\"DmeShared\"",
                "{",
                "\t\"id\" \"elementid\" \"00000000-0000-0000-0000-000000000004\"",
                "\t\"flag\" \"bool\" \"1\"",
                "}",
                "",
                "",
            ]);

            await Assert.That(Datamodel.Datamodel.TextEncoding.GetString(text.ToArray())).IsEqualTo(expected);
        }

        [Test]
        public async Task KeyValues2_FloatFormat()
        {
            using var dm = new DM("test", 1);
            dm.Root = new Element(dm, "root");
            dm.Root["position"] = new Vector3(-270.11304f, -233.07538f, 562.09106f);
            dm.Root["whole"] = 40f;
            dm.Root["negative"] = -1f;

            using var text = new MemoryStream();
            dm.Save(text, "keyvalues2", 4);
            var lines = Datamodel.Datamodel.TextEncoding.GetString(text.ToArray()).Split('\n');

            await Assert.That(lines).Contains("\t\"position\" \"vector3\" \"-270.1130371094 -233.075378418 562.0910644531\"");
            await Assert.That(lines).Contains("\t\"whole\" \"float\" \"40\"");
            await Assert.That(lines).Contains("\t\"negative\" \"float\" \"-1\"");
        }

        [Test]
        public async Task Binary_PrefixAttributes()
        {
            using var dm = new DM("vmap", 29);
            dm.PrefixAttributes["map_asset_references"] = new StringArray(["a.vmdl", "b.vmat"]);
            dm.PrefixAttributes["thumbnail_format"] = "jpg";
            dm.PrefixAttributes["thumbnail"] = new byte[] { 1, 2, 3 };
            dm.Root = new Element(dm, "root");
            dm.Root["hello"] = "world";

            using var reloaded = DM.Load(Save(dm));

            await Assert.That((StringArray?)reloaded.PrefixAttributes["map_asset_references"]).IsEquivalentTo(["a.vmdl", "b.vmat"], CollectionOrdering.Matching);
            await Assert.That((string?)reloaded.PrefixAttributes["thumbnail_format"]).IsEqualTo("jpg");
            await Assert.That((byte[]?)reloaded.PrefixAttributes["thumbnail"]).IsEquivalentTo(new byte[] { 1, 2, 3 }, CollectionOrdering.Matching);
            await Assert.That(reloaded.Root!.Get<string>("hello")).IsEqualTo("world");
        }

        [Test]
        public async Task Typed_PropertyTypeMismatchIsReported()
        {
            using var dm = new DM("vmap", 29);
            var mesh = new CMapMesh();

            var exception = Assert.Throws<InvalidDataException>(() => mesh["disableShadows"] = "3");
            await Assert.That(exception.Message).Contains("disableShadows");
        }

        [Test]
        public async Task Typed_ConvertsBetweenBoolIntAndFloat()
        {
            using var dm = new DM("vmap", 29);
            var mesh = new CMapMesh();

            // files written by older tools store some int attributes as bool, and Valve's datamodel converts between the scalar types
            mesh["disableShadows"] = true;
            await Assert.That(mesh.DisableShadows).IsEqualTo(1);

            mesh["renderToCubemaps"] = 0;
            await Assert.That(mesh.RenderToCubemaps).IsFalse();

            mesh["smoothingAngle"] = 45;
            await Assert.That(mesh.SmoothingAngle).IsEqualTo(45f);

            mesh["renderAmt"] = 127.9f;
            await Assert.That(mesh.RenderAmount).IsEqualTo(127);
        }

        static byte[] Save(DM dm)
        {
            using var ms = new MemoryStream();
            dm.Save(ms, "binary", 9);
            return ms.ToArray();
        }

        static async Task AssertEquivalent(DM expected, DM actual, bool orderSensitive, double floatTolerance = 0)
        {
            await AssertAttributesEquivalent(expected.PrefixAttributes, actual.PrefixAttributes, "prefix", orderSensitive, floatTolerance);

            var expectedElements = expected.AllElements.ToDictionary(e => e.ID);
            var actualElements = actual.AllElements.ToDictionary(e => e.ID);

            await Assert.That(actualElements.Keys).IsEquivalentTo(expectedElements.Keys).Because("element ids");
            await Assert.That(actual.Root?.ID).IsEqualTo(expected.Root?.ID).Because("root");

            foreach (var (id, expectedElement) in expectedElements)
            {
                var actualElement = actualElements[id];
                await Assert.That(actualElement.ClassName).IsEqualTo(expectedElement.ClassName).Because($"class of {id}");
                await Assert.That(actualElement.Name).IsEqualTo(expectedElement.Name).Because($"name of {id}");
                await Assert.That(actualElement.Stub).IsEqualTo(expectedElement.Stub).Because($"stub of {id}");

                if (!expectedElement.Stub)
                {
                    await AssertAttributesEquivalent(expectedElement, actualElement, $"{expectedElement.ClassName} {id}", orderSensitive, floatTolerance);
                }
            }
        }

        static async Task AssertAttributesEquivalent(AttributeList expected, AttributeList actual, string context, bool orderSensitive, double floatTolerance)
        {
            var expectedAttributes = expected.GetAllAttributesForSerialization().ToArray();
            var actualAttributes = actual.GetAllAttributesForSerialization().ToArray();

            var expectedNames = expectedAttributes.Select(a => a.Key).ToArray();
            var actualNames = actualAttributes.Select(a => a.Key).ToArray();

            if (orderSensitive)
            {
                await Assert.That(actualNames).IsEquivalentTo(expectedNames, CollectionOrdering.Matching).Because($"attribute names and order of {context}");
            }
            else
            {
                // a typed element also writes class properties the source lacked, with their default values, like the real datamodel does
                await Assert.That(expectedNames.Except(actualNames)).IsEmpty().Because($"attribute names of {context}");
            }

            var actualByName = actualAttributes.ToDictionary(a => a.Key, a => a.Value);

            foreach (var (name, expectedValue) in expectedAttributes)
            {
                await AssertValueEquivalent(expectedValue, actualByName[name], $"{context}.{name}", floatTolerance);
            }
        }

        static async Task AssertValueEquivalent(object? expected, object? actual, string context, double floatTolerance)
        {
            if (expected is null || actual is null)
            {
                await Assert.That(actual).IsEqualTo(expected).Because(context);
                return;
            }

            switch (expected)
            {
                case Element expectedElement:
                    await Assert.That(actual).IsAssignableTo<Element>().Because($"type of {context}");
                    await Assert.That(((Element)actual).ID).IsEqualTo(expectedElement.ID).Because(context);
                    break;
                case byte[] expectedBytes:
                    await Assert.That((byte[])actual).IsEquivalentTo(expectedBytes, CollectionOrdering.Matching).Because(context);
                    break;
                case IList expectedList:
                    await Assert.That(actual.GetType()).IsEqualTo(expected.GetType()).Because($"type of {context}");
                    var actualList = (IList)actual;
                    await Assert.That(actualList.Count).IsEqualTo(expectedList.Count).Because($"count of {context}");
                    for (var i = 0; i < expectedList.Count; i++)
                    {
                        await AssertValueEquivalent(expectedList[i], actualList[i], $"{context}[{i}]", floatTolerance);
                    }
                    break;
                default:
                    await Assert.That(actual.GetType()).IsEqualTo(expected.GetType()).Because($"type of {context}");

                    if (floatTolerance > 0 && TryGetComponents(expected, out var expectedComponents) && TryGetComponents(actual, out var actualComponents))
                    {
                        for (var i = 0; i < expectedComponents.Length; i++)
                        {
                            await Assert.That((double)actualComponents[i]).IsEqualTo(expectedComponents[i]).Within(floatTolerance).Because($"{context} component {i}");
                        }
                        break;
                    }

                    await Assert.That(actual).IsEqualTo(expected).Because(context);
                    break;
            }
        }

        static bool TryGetComponents(object value, out float[] components)
        {
            components = value switch
            {
                float f => [f],
                Vector2 v => [v.X, v.Y],
                Vector3 v => [v.X, v.Y, v.Z],
                Vector4 v => [v.X, v.Y, v.Z, v.W],
                Quaternion q => [q.X, q.Y, q.Z, q.W],
                QAngle a => [a.Pitch, a.Yaw, a.Roll],
                _ => [],
            };

            return components.Length > 0;
        }
    }
}
