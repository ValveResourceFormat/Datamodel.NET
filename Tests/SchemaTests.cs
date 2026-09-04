using System;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Datamodel;
using Datamodel.Codecs;
using Datamodel.Format;
using Tests.VMAP;
using DM = Datamodel.Datamodel;

namespace Datamodel_Tests
{
    /// <summary>
    /// A class exercising every property shape the generated factory has to bind without reflection.
    /// </summary>
    [CamelCaseProperties]
    internal class SchemaTestElement : Element
    {
        public int InitOnly { get; init; }

        public string PrivateSet { get; private set; } = string.Empty;

        public IntArray ReadOnlyArray { get; } = [];

        int custom;
        public int Custom
        {
            get => custom;
            init => custom = value * 2;
        }

        [DMProperty("renamed")]
        public bool Renamed { get; set; }
    }

    /// <summary>
    /// A class using the convention that depends on the property type.
    /// </summary>
    [HungarianProperties]
    internal class HungarianTestElement : Element
    {
        public int Count { get; set; }
        public float Scale { get; set; }
        public bool Visible { get; set; }
        public Vector3 Origin { get; set; }
        public Matrix4x4 Transform { get; set; }
        public string Name2 { get; set; } = string.Empty;
    }

    /// <summary>
    /// The ElementFactory generated into this assembly registers itself and describes the properties of every Element subclass.
    /// </summary>
    [TestFixture]
    public class SchemaTests
    {
        static string Resource(string name) => Path.Combine(TestContext.CurrentContext.TestDirectory, "Resources", name);

        [Test]
        public void Factory_IsRegisteredWhenTheAssemblyInitialises()
        {
            // every assembly with Element subclasses gets a factory: the schema assembly and this one
            var schemaFactory = DM.ElementFactories.SingleOrDefault(f => f.GetType().Assembly == typeof(CMapMesh).Assembly);
            var testFactory = DM.ElementFactories.SingleOrDefault(f => f.GetType().Assembly == typeof(SchemaTests).Assembly);

            Assert.That(schemaFactory, Is.Not.Null);
            Assert.That(schemaFactory!.Create("Tests.VMAP", "CMapMesh"), Is.InstanceOf<CMapMesh>());
            Assert.That(schemaFactory.Create("Tests.VMAP", "NoSuchClass"), Is.Null);
            Assert.That(schemaFactory.Create("Other.Namespace", "CMapMesh"), Is.Null);
            Assert.That(schemaFactory.Schemas.Select(schema => schema.ElementType), Does.Contain(typeof(CMapMesh)));

            Assert.That(testFactory, Is.Not.Null);
            Assert.That(testFactory!.Create("Datamodel_Tests", "SchemaTestElement"), Is.InstanceOf<SchemaTestElement>());
            Assert.That(testFactory.Create("Tests.VMAP", "CMapMesh"), Is.Null, "a factory only knows the classes of its own assembly");
        }

        [Test]
        public void Schema_ListsPropertiesBaseClassFirstWithAttributeNames()
        {
            var schema = ElementSchema.For(typeof(CMapMesh));

            Assert.That(schema, Is.Not.SameAs(ElementSchema.Empty));
            Assert.That(schema.ClassName, Is.EqualTo("CMapMesh"));
            Assert.That(schema.ElementType, Is.EqualTo(typeof(CMapMesh)));

            // MapNode's properties come before CMapMesh's own, camelCased by the naming convention
            var names = schema.Properties.Select(property => property.AttributeName).ToList();
            Assert.That(names[0], Is.EqualTo("origin"));
            Assert.That(names.IndexOf("children"), Is.LessThan(names.IndexOf("disableShadows")));
            Assert.That(schema.GetProperty("disableShadows")!.PropertyType, Is.EqualTo(typeof(int)));

            // a DMProperty name replaces the convention
            var root = ElementSchema.For(typeof(CMapRootElement));
            Assert.That(root.GetProperty("visbility")!.PropertyName, Is.EqualTo("Visibility"));
            Assert.That(root.GetProperty("Visibility"), Is.Null);

            Assert.That(ElementSchema.For(typeof(Element)), Is.SameAs(ElementSchema.Empty));
        }

        [Test]
        public void Schema_AppliesTheNamingConventionsAtBuildTime()
        {
            // the generator computes these with the same rules the attributes apply at run time
            var names = ElementSchema.For(typeof(HungarianTestElement)).Properties.Select(property => property.AttributeName).ToList();
            Assert.That(names, Is.EqualTo(new[] { "m_nCount", "m_flScale", "m_bVisible", "m_vOrigin", "m_matTransform", "m_name2" }));

            var convention = new HungarianPropertiesAttribute();
            Assert.That(convention.GetAttributeName("Count", typeof(int)), Is.EqualTo("m_nCount"));
            Assert.That(convention.GetAttributeName("Name2", typeof(string)), Is.EqualTo("m_name2"));
        }

        [Test]
        public void Schema_AssignsEveryPropertyShape()
        {
            var element = new SchemaTestElement();

            Assert.That(element.ClassName, Is.EqualTo("SchemaTestElement"));
            Assert.That(element.Schema.Properties.Select(property => property.AttributeName),
                Is.EqualTo(new[] { "initOnly", "privateSet", "readOnlyArray", "custom", "renamed" }));

            element["initOnly"] = 5;
            element["privateSet"] = "set through the private setter";
            element["readOnlyArray"] = new IntArray([1, 2, 3]);
            element["custom"] = 4;
            element["renamed"] = true;

            Assert.That(element.InitOnly, Is.EqualTo(5));
            Assert.That(element.PrivateSet, Is.EqualTo("set through the private setter"));
            Assert.That(element.ReadOnlyArray, Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(element.Custom, Is.EqualTo(8), "the init accessor's own logic runs");
            Assert.That(element.Renamed, Is.True);

            // the values read back through the indexer and are all written, nothing lands in the plain attribute list
            Assert.That(element["custom"], Is.EqualTo(8));
            Assert.That(element.Count, Is.Zero);
            Assert.That(element.GetAllAttributesForSerialization().Select(attr => attr.Key),
                Is.EqualTo(new[] { "initOnly", "privateSet", "readOnlyArray", "custom", "renamed" }));

            // a read-only array can only be filled while empty
            Assert.Throws<InvalidOperationException>(() => element["readOnlyArray"] = new IntArray([4]));
        }

        [Test]
        public void Load_UsesTheNamespaceOfTheRootTypeUnlessToldOtherwise()
        {
            using var typed = DM.Load<CMapRootElement>(Resource("roundtrip_test.vmap"));
            Assert.That(typed.Root, Is.InstanceOf<CMapRootElement>());

            using var explicitNamespace = DM.Load<CMapRootElement>(Resource("roundtrip_test.vmap"), new LoadOptions { Namespace = "Tests.VMAP" });
            Assert.That(explicitNamespace.Root, Is.InstanceOf<CMapRootElement>());

            // no class of the namespace matches, so the root stays a plain Element and cannot be the requested type
            var exception = Assert.Throws<InvalidDataException>(() => DM.Load<CMapRootElement>(Resource("roundtrip_test.vmap"), new LoadOptions { Namespace = "Nowhere" }));
            Assert.That(exception!.Message, Does.Contain("CMapRootElement"));

            using var untyped = DM.Load(Resource("roundtrip_test.vmap"), DeferredMode.Disabled);
            Assert.That(untyped.Root!.GetType(), Is.EqualTo(typeof(Element)));
            Assert.That(untyped.AllElements.All(element => element.GetType() == typeof(Element)), Is.True);
        }
    }
}
