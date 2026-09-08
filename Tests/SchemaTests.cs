using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using TUnit.Assertions.Enums;
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
    public class SchemaTests
    {
        static string Resource(string name) => Path.Combine(TestContext.TestDirectory!, "Resources", name);

        [Test]
        public async Task Factory_IsRegisteredWhenTheAssemblyInitialises()
        {
            // every assembly with Element subclasses gets a factory: the schema assembly and this one
            var schemaFactory = DM.ElementFactories.SingleOrDefault(f => f.GetType().Assembly == typeof(CMapMesh).Assembly);
            var testFactory = DM.ElementFactories.SingleOrDefault(f => f.GetType().Assembly == typeof(SchemaTests).Assembly);

            await Assert.That(schemaFactory).IsNotNull();
            await Assert.That(schemaFactory!.Create("Tests.VMAP", "CMapMesh")).IsTypeOf<CMapMesh>();
            await Assert.That(schemaFactory.Create("Tests.VMAP", "NoSuchClass")).IsNull();
            await Assert.That(schemaFactory.Create("Other.Namespace", "CMapMesh")).IsNull();
            await Assert.That(schemaFactory.Schemas.Select(schema => schema.ElementType)).Contains(typeof(CMapMesh));

            await Assert.That(testFactory).IsNotNull();
            await Assert.That(testFactory!.Create("Datamodel_Tests", "SchemaTestElement")).IsTypeOf<SchemaTestElement>();
            await Assert.That(testFactory.Create("Tests.VMAP", "CMapMesh")).IsNull().Because("a factory only knows the classes of its own assembly");
        }

        [Test]
        public async Task Schema_ListsPropertiesBaseClassFirstWithAttributeNames()
        {
            var schema = ElementSchema.For(typeof(CMapMesh));

            await Assert.That(schema).IsNotSameReferenceAs(ElementSchema.Empty);
            await Assert.That(schema.ClassName).IsEqualTo("CMapMesh");
            await Assert.That(schema.ElementType).IsEqualTo(typeof(CMapMesh));

            // MapNode's properties come before CMapMesh's own, camelCased by the naming convention
            var names = schema.Properties.Select(property => property.AttributeName).ToList();
            await Assert.That(names[0]).IsEqualTo("origin");
            await Assert.That(names.IndexOf("children")).IsLessThan(names.IndexOf("disableShadows"));
            await Assert.That(schema.GetProperty("disableShadows")!.PropertyType).IsEqualTo(typeof(int));

            // a DMProperty name replaces the convention
            var root = ElementSchema.For(typeof(CMapRootElement));
            await Assert.That(root.GetProperty("visbility")!.PropertyName).IsEqualTo("Visibility");
            await Assert.That(root.GetProperty("Visibility")).IsNull();

            await Assert.That(ElementSchema.For(typeof(Element))).IsSameReferenceAs(ElementSchema.Empty);
        }

        [Test]
        public async Task Schema_AppliesTheNamingConventionsAtBuildTime()
        {
            // the generator computes these with the same rules the attributes apply at run time
            var names = ElementSchema.For(typeof(HungarianTestElement)).Properties.Select(property => property.AttributeName).ToList();
            await Assert.That(names).IsEquivalentTo(["m_nCount", "m_flScale", "m_bVisible", "m_vOrigin", "m_matTransform", "m_name2"], CollectionOrdering.Matching);

            var convention = new HungarianPropertiesAttribute();
            await Assert.That(convention.GetAttributeName("Count", typeof(int))).IsEqualTo("m_nCount");
            await Assert.That(convention.GetAttributeName("Name2", typeof(string))).IsEqualTo("m_name2");
        }

        [Test]
        public async Task Schema_AssignsEveryPropertyShape()
        {
            var element = new SchemaTestElement();

            await Assert.That(element.ClassName).IsEqualTo("SchemaTestElement");
            await Assert.That(element.Schema.Properties.Select(property => property.AttributeName))
                .IsEquivalentTo(["initOnly", "privateSet", "readOnlyArray", "custom", "renamed"], CollectionOrdering.Matching);

            element["initOnly"] = 5;
            element["privateSet"] = "set through the private setter";
            element["readOnlyArray"] = new IntArray([1, 2, 3]);
            element["custom"] = 4;
            element["renamed"] = true;

            await Assert.That(element.InitOnly).IsEqualTo(5);
            await Assert.That(element.PrivateSet).IsEqualTo("set through the private setter");
            await Assert.That(element.ReadOnlyArray).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
            await Assert.That(element.Custom).IsEqualTo(8).Because("the init accessor's own logic runs");
            await Assert.That(element.Renamed).IsTrue();

            // the values read back through the indexer and are all written, nothing lands in the plain attribute list
            await Assert.That(element["custom"]).IsEqualTo(8);
            await Assert.That(element.Count).IsZero();
            await Assert.That(element.GetAllAttributesForSerialization().Select(attr => attr.Key))
                .IsEquivalentTo(["initOnly", "privateSet", "readOnlyArray", "custom", "renamed"], CollectionOrdering.Matching);

            // a read-only array can only be filled while empty
            Assert.Throws<InvalidOperationException>(() => element["readOnlyArray"] = new IntArray([4]));
        }

        [Test]
        public async Task Load_UsesTheNamespaceOfTheRootTypeUnlessToldOtherwise()
        {
            using var typed = DM.Load<CMapRootElement>(Resource("roundtrip_test.vmap"));
            await Assert.That(typed.Root).IsTypeOf<CMapRootElement>();

            using var explicitNamespace = DM.Load<CMapRootElement>(Resource("roundtrip_test.vmap"), new LoadOptions { Namespace = "Tests.VMAP" });
            await Assert.That(explicitNamespace.Root).IsTypeOf<CMapRootElement>();

            // no class of the namespace matches, so the root stays a plain Element and cannot be the requested type
            var exception = Assert.Throws<InvalidDataException>(() => DM.Load<CMapRootElement>(Resource("roundtrip_test.vmap"), new LoadOptions { Namespace = "Nowhere" }));
            await Assert.That(exception.Message).Contains("CMapRootElement");

            using var untyped = DM.Load(Resource("roundtrip_test.vmap"), DeferredMode.Disabled);
            await Assert.That(untyped.Root!.GetType()).IsEqualTo(typeof(Element));
            await Assert.That(untyped.AllElements.All(element => element.GetType() == typeof(Element))).IsTrue();
        }
    }
}
