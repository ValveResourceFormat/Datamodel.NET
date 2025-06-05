using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using NUnit.Framework;
using Datamodel;
using System.Numerics;
using DM = Datamodel.Datamodel;
using System.Globalization;
using Tests.VMAP;

namespace Datamodel_Tests
{
    public class DatamodelTests
    {
        protected FileStream Binary_9_File = File.OpenRead(TestContext.CurrentContext.TestDirectory + "/Resources/overboss_run.dmx");
        protected FileStream Binary_5_File = File.OpenRead(TestContext.CurrentContext.TestDirectory + "/Resources/taunt05_b5.dmx");
        protected FileStream Binary_4_File = File.OpenRead(TestContext.CurrentContext.TestDirectory + "/Resources/binary4.dmx");
        protected FileStream KeyValues2_1_File = File.OpenRead(TestContext.CurrentContext.TestDirectory + "/Resources/taunt05.dmx");

        const string GameBin = @"D:/Steam/steamapps/common/Counter-Strike Global Offensive/game/bin/win64";

        static readonly string DmxConvertExe = Path.Combine(GameBin, "dmxconvert.exe");
        static readonly bool DmxConvertExe_Exists = File.Exists(DmxConvertExe);

        static DatamodelTests()
        {
            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("fr-FR");
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("fr-FR");

            var binary = new byte[16];
            Random.Shared.NextBytes(binary);
            var quat = Quaternion.Normalize(new Quaternion(1, 2, 3, 4)); // dmxconvert will normalise this if I don't!

            TestValues_V1 = new List<object> {
                "hello_world",
                1,
                1.5f,
                true,
                binary,
                null,
                new Color(1, 255, 2, 244),
                new Vector2(1,2),
                new Vector3(1,2,3),
                new Vector4(1,2,3,4),
                quat,
                new Matrix4x4(1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16)
            };

            TestValues_V2 = TestValues_V1.ToList();
            TestValues_V2.Add(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond / 2));

            TestValues_V3 = TestValues_V1.Concat(new object[] {
                (byte)0xFF,
                (UInt64)0xFFFFFFFF,
                //new QAngle(0, 90, 180)
            }).ToList();
        }


        protected static string OutPath
            => Path.Combine(TestContext.CurrentContext.TestDirectory, TestContext.CurrentContext.Test.Name);
        protected static string DmxSavePath { get { return OutPath + ".dmx"; } }
        protected static string DmxConvertPath { get { return OutPath + "_convert.dmx"; } }

        protected static string[] GetDmxFiles()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Resources");
            return Enumerable.Concat(
                Directory.GetFiles(path, "*.dmx"),
                Directory.GetFiles(path, "*.vmap")
            ).ToArray();
        }

        protected static void Cleanup()
        {
            File.Delete(DmxSavePath);
            if (DmxConvertExe_Exists)
            {
                File.Delete(DmxConvertPath);
            }
        }

        protected static DM MakeDatamodel()
        {
            return new DM("model", 1); // using "model" to keep dxmconvert happy
        }

        protected static bool SaveAndConvert(DM datamodel, string encoding, int version)
        {
            datamodel.Save(DmxSavePath, encoding, version);

            if (!DmxConvertExe_Exists)
            {
                Assert.Warn("dmxconvert.exe not available.");
                return false;
            }

            var dmxconvert = new Process()
            {
                StartInfo = new ProcessStartInfo()
                {
                    FileName = DmxConvertExe,
                    Arguments = string.Format("-i \"{0}\" -o \"{1}\" -oe {2}", DmxSavePath, DmxConvertPath, encoding),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };

            Console.WriteLine($"Converting {TestContext.CurrentContext.Test.Name}.dmx to {encoding}");

            dmxconvert.Start();
            var err = dmxconvert.StandardOutput.ReadToEnd();
            err += dmxconvert.StandardError.ReadToEnd();
            dmxconvert.WaitForExit();

            Assert.That(dmxconvert.ExitCode, Is.Zero, $"dmxconvert failed to convert the file with error: {err}");

            return true;
        }

        /// <summary>
        /// Perform a parallel loop over all elements and attributes
        /// </summary>
        protected static void PrintContents(DM dm)
        {
            System.Threading.Tasks.Parallel.ForEach(dm.AllElements, e =>
            {
                System.Threading.Tasks.Parallel.ForEach(e, a => {; });
            });
        }

        protected static List<object> TestValues_V1 { get; }
        protected static List<object> TestValues_V2 { get; }
        protected static List<object> TestValues_V3 { get; }
        protected static Guid RootGuid { get; } = Guid.NewGuid();

        protected static List<object> AttributeValuesFor(string encoding_name, int encoding_version)
        {
            if (encoding_name == "keyvalues2")
            {
                return encoding_version >= 4 ? TestValues_V3 : TestValues_V2;
            }
            else if (encoding_name == "binary")
            {
                if (encoding_version >= 9)
                    return TestValues_V3;
                else if (encoding_version >= 3)
                    return TestValues_V2;
                else
                    return TestValues_V1;
            }
            else
                throw new ArgumentException("Unrecognised encoding.");
        }

        protected static void Populate(Datamodel.Datamodel dm, string encoding_name, int encoding_version)
        {
            dm.Root = new Element(dm, "root", RootGuid);

            foreach (var value in AttributeValuesFor(encoding_name, encoding_version))
            {
                if (value == null) continue;
                var name = value.GetType().Name;

                dm.Root[name] = value;
                Assert.AreSame(value, dm.Root[name]);

                name += " array";
                var list = value.GetType().MakeListType().GetConstructor(Type.EmptyTypes).Invoke(null) as IList;
                list.Add(value);
                list.Add(value);
                dm.Root[name] = list;
                Assert.AreSame(list, dm.Root[name]);
            }

            dm.Root["Recursive"] = dm.Root;
            dm.Root["NoName"] = new Element();
            dm.Root["ElemArray"] = new ElementArray(new Element[] { new Element(dm, Guid.NewGuid()), new Element(), dm.Root, new Element(dm, "TestElement") });
            dm.Root["ElementStub"] = new Element(dm, Guid.NewGuid());
        }

        private void CompareVector(DM dm, string name, float[] actual)
        {
            var expected = (IEnumerable<float>)dm.Root[name];

            Assert.AreEqual(actual.Count(), expected.Count());

            foreach (var t in actual.Zip(expected, (a, e) => new Tuple<float, float>(a, e)))
                Assert.AreEqual(t.Item1, t.Item2, 1e-6, name);
        }

        protected static void ValidatePopulated(string encoding_name, int encoding_version)
        {
            var dm = DM.Load(DmxConvertPath);
            Assert.AreEqual(RootGuid, dm.Root.ID);
            foreach (var value in AttributeValuesFor(encoding_name, encoding_version))
            {
                if (value == null) continue;
                var name = value.GetType().Name;

                if (value is ICollection collection)
                    CollectionAssert.AreEqual(collection, (ICollection)dm.Root[name]);
                else if (value is Color color)
                    Assert.AreEqual(color, dm.Root.Get<Color>(name));
                else if (value is Quaternion quat)
                {
                    var expected = dm.Root.Get<Quaternion>(name);
                    Assert.AreEqual(quat.W, expected.W, 1e-6, name + " W");
                    Assert.AreEqual(quat.X, expected.X, 1e-6, name + " X");
                    Assert.AreEqual(quat.Y, expected.Y, 1e-6, name + " Y");
                    Assert.AreEqual(quat.Z, expected.Z, 1e-6, name + " Z");
                }
                else
                    Assert.AreEqual(value, dm.Root[name], name);
            }

            dm.Dispose();
        }

        protected static DM Create(string encoding, int version, bool memory_save = false)
        {
            var dm = MakeDatamodel();
            Populate(dm, encoding, version);

            dm.Root["Arr"] = new System.Collections.ObjectModel.ObservableCollection<int>();
            dm.Root.GetArray<int>("Arr");

            if (memory_save)
                dm.Save(new MemoryStream(), encoding, version);
            else
            {
                dm.Save(DmxSavePath, encoding, version);
                if (SaveAndConvert(dm, encoding, version))
                {
                    ValidatePopulated(encoding, version);
                }
                Cleanup();
            }

            dm.AllElements.Remove(dm.Root.GetArray<Element>("ElemArray")[3], DM.ElementList.RemoveMode.MakeStubs);
            Assert.AreEqual(true, dm.Root.GetArray<Element>("ElemArray")[3].Stub);

            dm.AllElements.Remove(dm.Root, DM.ElementList.RemoveMode.MakeStubs);
            Assert.AreEqual(true, dm.Root.Stub);

            return dm;
        }
    }

    [TestFixture]
    public class Functionality : DatamodelTests
    {
        [Test]
        public static void TypedArrayAddingRemoving()
        {
            using var dm = MakeDatamodel();
            var array = new ElementArray();

            var elementA = new Element(dm, "a");
            var elementB = new Element();

            Assert.False(array.Remove(elementB));
            Assert.False(array.Remove(elementA));

            dm.Root["a"] = array;

            Assert.False(array.Remove(elementB));
            Assert.False(array.Remove(elementA));

            array.Add(elementB);
            Assert.True(array.Remove(elementB));

            Assert.False(array.Remove(elementB));
            Assert.False(array.Remove(elementA));

            ((IList)array).Add(elementA);
            array.Add(elementB);

            array.Add(elementA); // add again?
            array.Remove(elementA);

            Assert.AreEqual(2, array.Count); // only removes first instance

            array.Remove(elementA);
            array.Remove(elementB);

            Assert.AreEqual(0, array.Count);
        }

        private static void Validate_Vmap_Reflection(Datamodel.Datamodel unserialisedVmap)
        {
            Assert.AreEqual(typeof(CMapRootElement), unserialisedVmap.Root.GetType());

            CMapRootElement root = (CMapRootElement)unserialisedVmap.Root;

            Assert.AreEqual(typeof(CMapWorld), root.world.GetType());

            var world = root.world;

            var props = world.children.Where(i => i.ClassName == "CMapEntity").OfType<CMapEntity>().ToList();

            var prop = props[0];
            var propclass = prop.ClassName;
            var proptype = prop.GetType();
            var entityprop = prop;


            var propProperties = props[0].EntityProperties;
            var classname = propProperties.Get<string>("classname");

            var meshes = world.children.Where(i => i.ClassName == "CMapMesh").OfType<CMapMesh>().ToList();
            var mesh = meshes[0];

            var vertexData = mesh.meshData.vertexData;

            Assert.AreEqual(vertexData.size, 8);
            Assert.AreEqual(vertexData.streams[0]["semanticName"], "position");

            var typedPolygonMeshData = (CDmePolygonMeshDataStream)vertexData.streams[0];
            Assert.AreEqual(typedPolygonMeshData.semanticName, "position");

            var typedPolygonMeshDataStream = typedPolygonMeshData.data as Vector3Array;
            Assert.IsNotNull(typedPolygonMeshDataStream);

            Assert.That(unserialisedVmap.PrefixAttributes["map_asset_references"], Is.Not.Empty);

            // iterate all datamodel elements, and verify that all their types are superclasses of Element
            foreach (var elem in unserialisedVmap.AllElements)
            {
                if (elem.Name == "subdivisionBinding")
                {
                    continue; // known case, skip
                }

                // prefix elements, still an Element type
                if (elem.ContainsKey("map_asset_references"))
                {
                    continue;
                }

                Assert.That(elem, Is.Not.TypeOf<Element>(), $"Found object {elem.ID} {elem.ClassName} that is still an Element type.");
            }

        }

        [Test]
        public void LoadVmap_Reflection_Binary()
        {
            var unserialisedVmap = DM.Load<CMapRootElement>(Path.Combine(TestContext.CurrentContext.TestDirectory, "Resources", "cs2_map.vmap"));
            Validate_Vmap_Reflection(unserialisedVmap);
        }

        [Test]
        public void LoadVmap_Reflection_Text()
        {
            var unserialisedVmap = DM.Load<CMapRootElement>(Path.Combine(TestContext.CurrentContext.TestDirectory, "Resources", "cs2_map.vmap.txt"));
            Validate_Vmap_Reflection(unserialisedVmap);
        }

        public class NullOwnerElement
        {
            [Test]
            public void ElementInitializes()
            {
                var elem = new Element();

                Assert.That(elem.Owner, Is.Null);
                Assert.That(elem.Count, Is.Zero);
            }

            [Test]
            public void CanImportToRoot()
            {
                var elem = new Element();

                var dm = new DM("test", 1);
                dm.Root = elem;

                Assert.That(elem.Owner, Is.EqualTo(dm));
            }

            [Test]
            public void Nested_CanImportToRoot()
            {
                var elem = new Element();
                var elem2 = new Element();
                elem["elem2"] = elem2;
                elem2["woah"] = 5;

                Assert.That(elem.Owner, Is.Null);
                Assert.That(elem.Count, Is.EqualTo(1));
                Assert.That(elem2.Owner, Is.Null);
                Assert.That(elem2.Count, Is.EqualTo(1));

                Assert.That(elem.First().Key, Is.EqualTo("elem2"));
                Assert.That(elem.First().Value, Is.EqualTo(elem2));
                Assert.That(elem2.First().Key, Is.EqualTo("woah"));
                Assert.That(elem2.First().Value, Is.EqualTo(5));

                var dm = new DM("test", 1);
                dm.Root = elem;

                Assert.That(elem.Owner, Is.EqualTo(dm));
                Assert.That(elem2.Owner, Is.EqualTo(dm));
            }

            [Test]
            public void ElementArrayInitializes()
            {
                var elem = new ElementArray();

                Assert.That(elem.Owner, Is.Null);
                Assert.That(elem.Count, Is.Zero);
            }
        }

        public class ElementSubclassing
        {
            class CustomElement : Element
            {
                public int MyProperty { get; set; } = 1337;
            }

            [Test]
            public void ElementSubclassInitializes()
            {
                var elem = new CustomElement();

                Assert.That(elem.Owner, Is.Null);
                Assert.That(elem.Count, Is.Zero);
                Assert.That(elem.MyProperty, Is.EqualTo(1337));
            }

            [Test]
            public void PropertyAccessByKey()
            {
                var elem = new CustomElement();
                var myprop = elem["MyProperty"];

                Assert.That(myprop, Is.EqualTo(1337));
            }

            [Test]
            public void CanBeAssignedToDatamodelRoot()
            {
                var elem = new CustomElement();

                var dm = new DM("test", 1);
                dm.Root = elem;

                Assert.That(elem.Owner, Is.EqualTo(dm));
            }

            [Test]
            public void Nested_CanBeAssignedToDatamodelRoot()
            {
                var elem = new CustomElement();
                var elem2 = new CustomElement();
                elem["elem2"] = elem2;
                elem2["woah"] = 5;

                Assert.That(elem.Owner, Is.Null);
                Assert.That(elem.Count, Is.EqualTo(1));
                Assert.That(elem2.Owner, Is.Null);
                Assert.That(elem2.Count, Is.EqualTo(1));

                Assert.That(elem.First().Key, Is.EqualTo("elem2"));
                Assert.That(elem.First().Value, Is.EqualTo(elem2));
                Assert.That(elem2.First().Key, Is.EqualTo("woah"));
                Assert.That(elem2.First().Value, Is.EqualTo(5));

                var dm = new DM("test", 1);
                dm.Root = elem;

                Assert.That(elem.Owner, Is.EqualTo(dm));
                Assert.That(elem2.Owner, Is.EqualTo(dm));
            }

            [Test]
            public void SerializesText()
            {
                var elem = new CustomElement();
                using var dm = new DM("vmap", 29);
                dm.Root = elem;

                elem["as_child"] = new CustomElement() { MyProperty = 5 };

                using var stream = new MemoryStream();
                dm.Save(stream, "keyvalues2", 4);

                stream.Position = 0;
                using (var reader = new StreamReader(stream))
                {
                    var text = reader.ReadToEnd();

                    Assert.Multiple(() =>
                    {
                        Assert.That(text, Does.Contain("CustomElement"));
                        Assert.That(text, Does.Contain("MyProperty"));
                        Assert.That(text, Does.Contain("1337"));
                        Assert.That(text, Does.Contain("\"as_child\" \"CustomElement\""));
                    });
                }

                SaveAndConvert(dm, "keyvalues2", 4);

                // binary
                using var stream2 = new MemoryStream();
                dm.Save(stream2, "binary", 9);

                stream2.Position = 0;
                using var reader2 = new BinaryReader(stream2);
                var bytes = reader2.ReadBytes((int)stream2.Length);

                // idk
                //Assert.That(bytes, Does.Contain(
                //    [.. Encoding.ASCII.GetBytes("CustomElement")]
                //));

                SaveAndConvert(dm, "binary", 9);
            }
        }

        [Test]
        public void Create_Binary_9()
        {
            Create("binary", 9);
        }
        [Test]
        public void Create_Binary_5()
        {
            Create("binary", 5);
        }
        [Test]
        public void Create_Binary_4()
        {
            Create("binary", 4);
        }
        [Test]
        public void Create_Binary_3()
        {
            Create("binary", 3);
        }
        [Test]
        public void Create_Binary_2()
        {
            Create("binary", 2);
        }

        [Test]
        public void Create_KeyValues2_4()
        {
            Create("keyvalues2", 4);
        }


        [Test]
        public void Create_KeyValues2_1()
        {
            Create("keyvalues2", 1);
        }

        void Get_TF2(Datamodel.Datamodel dm)
        {
            dm.Root.Get<Element>("skeleton").GetArray<Element>("children")[0].Any();
            dm.FormatVersion = 22; // otherwise recent versions of dmxconvert fail
        }

        [Test]
        public void Dota2_Binary_9()
        {
            var dm = DM.Load<Element>(Binary_9_File);
            PrintContents(dm);
            dm.Root.Get<Element>("skeleton").GetArray<Element>("children")[0].Any();
            SaveAndConvert(dm, "binary", 9);

            Cleanup();
        }

        [Test]
        public void TF2_Binary_5()
        {
            var dm = DM.Load(Binary_5_File);
            PrintContents(dm);
            Get_TF2(dm);
            SaveAndConvert(dm, "binary", 5);

            Cleanup();
        }

        [Test]
        public void TF2_Binary_4()
        {
            var dm = DM.Load(Binary_4_File);
            PrintContents(dm);
            Get_TF2(dm);
            SaveAndConvert(dm, "binary", 4);

            Cleanup();
        }

        [Test]
        public void TF2_KeyValues2_1()
        {
            var dm = DM.Load(KeyValues2_1_File);
            PrintContents(dm);
            Get_TF2(dm);
            SaveAndConvert(dm, "keyvalues2", 1);

            Cleanup();
        }

        [Test, TestCaseSource(nameof(GetDmxFiles))]
        public void Unserialize(string path)
        {
            var dm = DM.Load(path, Datamodel.Codecs.DeferredMode.Automatic);
            PrintContents(dm);
            dm.Dispose();
        }

        [Test]
        public void Cs2MapConvert()
        {
            var file = Path.Combine(TestContext.CurrentContext.TestDirectory, "Resources", "cs2_map.vmap");
            var dm = DM.Load(file);

            // file will be in bin/Debug/net6.0/Resources
            dm.Save(file + "datamodel.txt", "keyvalues2", 4);
            dm.Save(file + "datamodel.vmap", dm.Encoding, dm.EncodingVersion);
        }

        [Test]
        public void Import()
        {
            var dm = MakeDatamodel();
            Populate(dm, "binary", 9);

            var dm2 = MakeDatamodel();
            dm2.Root = dm2.ImportElement(dm.Root, DM.ImportRecursionMode.Recursive, DM.ImportOverwriteMode.All);

            SaveAndConvert(dm, "keyvalues2", 4);
            SaveAndConvert(dm, "binary", 9);
        }
    }

    [TestFixture, Category("Performance")]
    public class Performance : DatamodelTests
    {
        const int Load_Iterations = 10;
        readonly Stopwatch Timer = new();

        void Load(FileStream f)
        {
            long elapsed = 0;
            Timer.Start();
            foreach (var i in Enumerable.Range(0, Load_Iterations + 1))
            {
                DM.Load(f, Datamodel.Codecs.DeferredMode.Disabled);
                if (i > 0)
                {
                    Console.Write(Timer.ElapsedMilliseconds + ", ");
                    elapsed += Timer.ElapsedMilliseconds;
                }
                Timer.Restart();
            }
            Timer.Stop();
            Console.WriteLine("Average: {0}ms", elapsed / Load_Iterations);
        }

        [Test]
        public void Perf_Load_Binary5()
        {
            Load(Binary_5_File);
        }

        [Test]
        public void Perf_Load_KeyValues2_1()
        {
            Load(KeyValues2_1_File);
        }

        [Test]
        public void Perf_Create_Binary5()
        {
            foreach (var i in Enumerable.Range(0, 1000))
                Create("binary", 5, true);
        }

        [Test]
        public void Perf_CreateElements_Binary5()
        {
            var dm = MakeDatamodel();
            dm.Root = new Element(dm, "root");
            var inner_elem = new Element(dm, "inner_elem");
            var arr = new ElementArray(20000);
            dm.Root["big_array"] = arr;

            foreach (int i in Enumerable.Range(0, 19999))
                arr.Add(inner_elem);

            SaveAndConvert(dm, "binary", 5);
            Cleanup();
        }

        [Test]
        public void Perf_CreateAttributes_Binary5()
        {
            var dm = MakeDatamodel();
            dm.Root = new Element(dm, "root");

            foreach (int x in Enumerable.Range(0, 5000))
            {
                var elem_name = x.ToString();
                foreach (int i in Enumerable.Range(0, 5))
                {
                    var elem = new Element(dm, elem_name);
                    var key = i.ToString();
                    elem[key] = i;
                    elem.Get<int>(key);
                }
            }

            SaveAndConvert(dm, "binary", 5);
            Cleanup();
        }
    }

    static class Extensions
    {
        public static Type MakeListType(this Type t)
        {
            return typeof(List<>).MakeGenericType(t);
        }
    }
}
