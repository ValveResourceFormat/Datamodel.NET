using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Datamodel;
using Tests.VMAP;
using DM = Datamodel.Datamodel;

namespace Datamodel_Tests
{
    /// <summary>
    /// Loading a file and saving it again must reproduce every element, every attribute and the prefix attributes,
    /// whether the elements were deserialized as plain <see cref="Element"/>s or as typed subclasses.
    /// </summary>
    [TestFixture]
    public class RoundTripTests
    {
        static string Resource(string name) => Path.Combine(TestContext.CurrentContext.TestDirectory, "Resources", name);

        // a map made for this purpose: every node class, all selection set kinds, nested prefabs and instances,
        // subdivision, vertex paint, baked lighting, a thumbnail and asset references in the prefix
        static readonly string[] VmapFiles =
        [
            "roundtrip_test.vmap",
            Path.Combine("prefabs", "roundtrip_test_prefab1.vmap"),
            Path.Combine("prefabs", "roundtrip_test_prefab2.vmap"),
            Path.Combine("prefabs", "roundtrip_test_prefab3.vmap"),
        ];

        [Test, TestCaseSource(nameof(VmapFiles))]
        public void Binary_Untyped(string file)
        {
            using var original = DM.Load(Resource(file), Datamodel.Codecs.DeferredMode.Disabled);
            var saved = Save(original);

            using var reloaded = DM.Load(saved);
            AssertEquivalent(original, reloaded, orderSensitive: true);
            Assert.That(reloaded.PrefixElementId, Is.EqualTo(original.PrefixElementId));

            Assert.That(Save(reloaded), Is.EqualTo(saved), "saving the reloaded datamodel must reproduce the same bytes");
        }

        [Test, TestCaseSource(nameof(VmapFiles))]
        public void Binary_PrefixElementIsNotAnOrphan(string file)
        {
            using var dm = DM.Load(Resource(file), Datamodel.Codecs.DeferredMode.Disabled);

            Assert.That(dm.PrefixAttributes.Keys, Does.Contain("map_asset_references"));

            var reachable = new HashSet<Element>();
            Visit(dm.Root);
            Assert.That(dm.AllElements.Count, Is.EqualTo(reachable.Count), "every element must be reachable from the root");

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

        [Test, TestCaseSource(nameof(VmapFiles))]
        public void Binary_Typed(string file)
        {
            using var original = DM.Load(Resource(file), Datamodel.Codecs.DeferredMode.Disabled);
            using var typed = DM.Load<CMapRootElement>(Resource(file));

            Assert.That(typed.Root, Is.TypeOf<CMapRootElement>());

            // typed elements write their class properties first, in declaration order, so only the set of attributes is compared
            AssertEquivalent(original, typed, orderSensitive: false);

            var saved = Save(typed);
            using var reloaded = DM.Load(saved);
            AssertEquivalent(original, reloaded, orderSensitive: false);

            Assert.That(Save(reloaded), Is.EqualTo(saved));
        }

        [Test, TestCaseSource(nameof(VmapFiles))]
        public void KeyValues2_Untyped(string file)
        {
            using var original = DM.Load(Resource(file), Datamodel.Codecs.DeferredMode.Disabled);

            using var text = new MemoryStream();
            original.Save(text, "keyvalues2", 4);

            using var reloaded = DM.Load(text.ToArray());

            FloatTolerance = 1e-9;
            try
            {
                AssertEquivalent(original, reloaded, orderSensitive: true);
            }
            finally
            {
                FloatTolerance = 0;
            }

            Assert.That(reloaded.PrefixElementId, Is.EqualTo(original.PrefixElementId));

            using var text2 = new MemoryStream();
            reloaded.Save(text2, "keyvalues2", 4);
            Assert.That(text2.ToArray(), Is.EqualTo(text.ToArray()));
        }

        [Test]
        public void KeyValues2_MatchesReferenceLayout()
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

            Assert.That(Datamodel.Datamodel.TextEncoding.GetString(text.ToArray()), Is.EqualTo(expected));
        }

        [Test]
        public void KeyValues2_FloatFormat()
        {
            using var dm = new DM("test", 1);
            dm.Root = new Element(dm, "root");
            dm.Root["position"] = new Vector3(-270.11304f, -233.07538f, 562.09106f);
            dm.Root["whole"] = 40f;
            dm.Root["negative"] = -1f;

            using var text = new MemoryStream();
            dm.Save(text, "keyvalues2", 4);
            var lines = Datamodel.Datamodel.TextEncoding.GetString(text.ToArray()).Split('\n');

            Assert.That(lines, Does.Contain("\t\"position\" \"vector3\" \"-270.1130371094 -233.075378418 562.0910644531\""));
            Assert.That(lines, Does.Contain("\t\"whole\" \"float\" \"40\""));
            Assert.That(lines, Does.Contain("\t\"negative\" \"float\" \"-1\""));
        }

        [Test]
        public void Binary_PrefixAttributes()
        {
            using var dm = new DM("vmap", 29);
            dm.PrefixAttributes["map_asset_references"] = new StringArray(["a.vmdl", "b.vmat"]);
            dm.PrefixAttributes["thumbnail_format"] = "jpg";
            dm.PrefixAttributes["thumbnail"] = new byte[] { 1, 2, 3 };
            dm.Root = new Element(dm, "root");
            dm.Root["hello"] = "world";

            using var reloaded = DM.Load(Save(dm));

            Assert.That((StringArray?)reloaded.PrefixAttributes["map_asset_references"], Is.EqualTo(new[] { "a.vmdl", "b.vmat" }));
            Assert.That((string?)reloaded.PrefixAttributes["thumbnail_format"], Is.EqualTo("jpg"));
            Assert.That((byte[]?)reloaded.PrefixAttributes["thumbnail"], Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(reloaded.Root!.Get<string>("hello"), Is.EqualTo("world"));
        }

        [Test]
        public void Typed_PropertyTypeMismatchIsReported()
        {
            using var dm = new DM("vmap", 29);
            var mesh = new CMapMesh();

            // disableShadows is an int in the file format
            var exception = Assert.Throws<InvalidDataException>(() => mesh["disableShadows"] = true);
            Assert.That(exception!.Message, Does.Contain("disableShadows"));
        }

        static byte[] Save(DM dm)
        {
            using var ms = new MemoryStream();
            dm.Save(ms, "binary", 9);
            return ms.ToArray();
        }

        static void AssertEquivalent(DM expected, DM actual, bool orderSensitive)
        {
            AssertAttributesEquivalent(expected.PrefixAttributes, actual.PrefixAttributes, "prefix", orderSensitive);

            var expectedElements = expected.AllElements.ToDictionary(e => e.ID);
            var actualElements = actual.AllElements.ToDictionary(e => e.ID);

            Assert.That(actualElements.Keys, Is.EquivalentTo(expectedElements.Keys), "element ids");
            Assert.That(actual.Root?.ID, Is.EqualTo(expected.Root?.ID), "root");

            foreach (var (id, expectedElement) in expectedElements)
            {
                var actualElement = actualElements[id];
                Assert.That(actualElement.ClassName, Is.EqualTo(expectedElement.ClassName), $"class of {id}");
                Assert.That(actualElement.Name, Is.EqualTo(expectedElement.Name), $"name of {id}");
                Assert.That(actualElement.Stub, Is.EqualTo(expectedElement.Stub), $"stub of {id}");

                if (!expectedElement.Stub)
                {
                    AssertAttributesEquivalent(expectedElement, actualElement, $"{expectedElement.ClassName} {id}", orderSensitive);
                }
            }
        }

        static void AssertAttributesEquivalent(AttributeList expected, AttributeList actual, string context, bool orderSensitive)
        {
            var expectedAttributes = expected.GetAllAttributesForSerialization().ToArray();
            var actualAttributes = actual.GetAllAttributesForSerialization().ToArray();

            var expectedNames = expectedAttributes.Select(a => a.Key);
            var actualNames = actualAttributes.Select(a => a.Key);

            if (orderSensitive)
            {
                Assert.That(actualNames, Is.EqualTo(expectedNames), $"attribute names and order of {context}");
            }
            else
            {
                // a typed element also writes class properties the source lacked, with their default values, like the real datamodel does
                Assert.That(actualNames, Is.SupersetOf(expectedNames), $"attribute names of {context}");
            }

            var actualByName = actualAttributes.ToDictionary(a => a.Key, a => a.Value);

            foreach (var (name, expectedValue) in expectedAttributes)
            {
                AssertValueEquivalent(expectedValue, actualByName[name], $"{context}.{name}");
            }
        }

        static void AssertValueEquivalent(object? expected, object? actual, string context)
        {
            if (expected is null || actual is null)
            {
                Assert.That(actual, Is.EqualTo(expected), context);
                return;
            }

            switch (expected)
            {
                case Element expectedElement:
                    Assert.That(actual, Is.InstanceOf<Element>(), $"type of {context}");
                    Assert.That(((Element)actual).ID, Is.EqualTo(expectedElement.ID), context);
                    break;
                case byte[] expectedBytes:
                    Assert.That(actual, Is.EqualTo(expectedBytes), context);
                    break;
                case IList expectedList:
                    Assert.That(actual.GetType(), Is.EqualTo(expected.GetType()), $"type of {context}");
                    var actualList = (IList)actual;
                    Assert.That(actualList.Count, Is.EqualTo(expectedList.Count), $"count of {context}");
                    for (var i = 0; i < expectedList.Count; i++)
                    {
                        AssertValueEquivalent(expectedList[i], actualList[i], $"{context}[{i}]");
                    }
                    break;
                default:
                    Assert.That(actual.GetType(), Is.EqualTo(expected.GetType()), $"type of {context}");

                    if (FloatTolerance > 0 && TryGetComponents(expected, out var expectedComponents) && TryGetComponents(actual, out var actualComponents))
                    {
                        Assert.That(actualComponents, Is.EqualTo(expectedComponents).Within(FloatTolerance), context);
                        break;
                    }

                    Assert.That(actual, Is.EqualTo(expected), context);
                    break;
            }
        }

        // keyvalues2 prints floats with ten decimals, so values that small lose precision in that encoding
        static double FloatTolerance;

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
