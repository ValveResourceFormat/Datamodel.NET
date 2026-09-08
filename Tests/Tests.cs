using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using TUnit.Assertions.Enums;
using Datamodel;
using System.Numerics;
using DM = Datamodel.Datamodel;
using System.Globalization;
using Tests.VMAP;
using ValveResourceFormat.IO;

namespace Datamodel_Tests
{
    // sadly we must now involve the french in order to test culture invariance
    public static class TestCulture
    {
        [Before(TestSession)]
        public static void UseDecimalComma()
        {
            var culture = new CultureInfo("fr-FR");
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.CurrentCulture = culture;
        }
    }

    public class DatamodelTests
    {
        protected FileStream Binary_9_File = File.OpenRead(TestContext.TestDirectory + "/Resources/overboss_run.dmx");
        protected FileStream Binary_5_File = File.OpenRead(TestContext.TestDirectory + "/Resources/taunt05_b5.dmx");
        protected FileStream Binary_4_File = File.OpenRead(TestContext.TestDirectory + "/Resources/binary4.dmx");
        protected FileStream KeyValues2_1_File = File.OpenRead(TestContext.TestDirectory + "/Resources/taunt05.dmx");

        /// <summary>dmxconvert.exe of any installed Source 2 game, used to validate what the library writes. Null when no game is installed.</summary>
        static readonly string? DmxConvertExe = GameFolderLocator.FindAllSteamGames()
            .Select(game => Path.Combine(game.GamePath, "game", "bin", "win64", "dmxconvert.exe"))
            .FirstOrDefault(File.Exists);
        static readonly bool DmxConvertExe_Exists = DmxConvertExe != null;

        static DatamodelTests()
        {
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

        /// <summary>The name of the running test, used to keep the files each test writes apart.</summary>
        protected static string TestName => TestContext.Current?.Metadata.TestName ?? "test";

        protected static string OutPath
            => Path.Combine(TestContext.TestDirectory!, TestName);
        protected static string DmxSavePath { get { return OutPath + ".dmx"; } }
        protected static string DmxConvertPath { get { return OutPath + "_convert.dmx"; } }

        public static IEnumerable<string> GetDmxFiles()
        {
            var path = Path.Combine(TestContext.TestDirectory!, "Resources");
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

        protected static async Task<bool> SaveAndConvert(DM datamodel, string encoding, int version)
        {
            datamodel.Save(DmxSavePath, encoding, version);

            if (!DmxConvertExe_Exists)
            {
                Console.WriteLine("dmxconvert.exe not available.");
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

            Console.WriteLine($"Converting {TestName}.dmx to {encoding}");

            dmxconvert.Start();
            var err = dmxconvert.StandardOutput.ReadToEnd();
            err += dmxconvert.StandardError.ReadToEnd();
            dmxconvert.WaitForExit();

            await Assert.That(dmxconvert.ExitCode).IsZero().Because($"dmxconvert failed to convert the file with error: {err}");

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

        protected static async Task Populate(Datamodel.Datamodel dm, string encoding_name, int encoding_version)
        {
            dm.Root = new Element(dm, "root", RootGuid);

            foreach (var value in AttributeValuesFor(encoding_name, encoding_version))
            {
                if (value == null) continue;
                var name = value.GetType().Name;

                dm.Root[name] = value;
                await Assert.That(dm.Root[name]).IsSameReferenceAs(value);

                name += " array";
                var list = value.GetType().MakeListType().GetConstructor(Type.EmptyTypes).Invoke(null) as IList;
                list.Add(value);
                list.Add(value);
                dm.Root[name] = list;
                await Assert.That(dm.Root[name]).IsSameReferenceAs(list);
            }

            dm.Root["Recursive"] = dm.Root;
            dm.Root["NoName"] = new Element();
            dm.Root["ElemArray"] = new ElementArray(new Element[] { new Element(dm, Guid.NewGuid()), new Element(), dm.Root, new Element(dm, "TestElement") });
            dm.Root["ElementStub"] = new Element(dm, Guid.NewGuid());
        }

        protected static async Task ValidatePopulated(string encoding_name, int encoding_version)
        {
            var dm = DM.Load(DmxConvertPath);
            await Assert.That(dm.Root.ID).IsEqualTo(RootGuid);
            foreach (var value in AttributeValuesFor(encoding_name, encoding_version))
            {
                if (value == null) continue;
                var name = value.GetType().Name;

                if (value is ICollection collection)
                    await Assert.That(((ICollection)dm.Root[name]).Cast<object>()).IsEquivalentTo(collection.Cast<object>(), CollectionOrdering.Matching).Because(name);
                else if (value is Color color)
                    await Assert.That(dm.Root.Get<Color>(name)).IsEqualTo(color);
                else if (value is Quaternion quat)
                {
                    var expected = dm.Root.Get<Quaternion>(name);
                    await Assert.That(expected.W).IsEqualTo(quat.W).Within(1e-6f).Because(name + " W");
                    await Assert.That(expected.X).IsEqualTo(quat.X).Within(1e-6f).Because(name + " X");
                    await Assert.That(expected.Y).IsEqualTo(quat.Y).Within(1e-6f).Because(name + " Y");
                    await Assert.That(expected.Z).IsEqualTo(quat.Z).Within(1e-6f).Because(name + " Z");
                }
                else
                    await Assert.That(dm.Root[name]).IsEqualTo(value).Because(name);
            }

            dm.Dispose();
        }

        protected static async Task<DM> Create(string encoding, int version, bool memory_save = false)
        {
            var dm = MakeDatamodel();
            await Populate(dm, encoding, version);

            dm.Root["Arr"] = new System.Collections.ObjectModel.ObservableCollection<int>();
            dm.Root.GetArray<int>("Arr");

            if (memory_save)
                dm.Save(new MemoryStream(), encoding, version);
            else
            {
                dm.Save(DmxSavePath, encoding, version);
                if (await SaveAndConvert(dm, encoding, version))
                {
                    await ValidatePopulated(encoding, version);
                }
                Cleanup();
            }

            dm.AllElements.Remove(dm.Root.GetArray<Element>("ElemArray")[3], DM.ElementList.RemoveMode.MakeStubs);
            await Assert.That(dm.Root.GetArray<Element>("ElemArray")[3].Stub).IsTrue();

            dm.AllElements.Remove(dm.Root, DM.ElementList.RemoveMode.MakeStubs);
            await Assert.That(dm.Root.Stub).IsTrue();

            return dm;
        }
    }

    public class Functionality : DatamodelTests
    {
        [Test]
        public async Task ElementEqualityMatchesHashCode()
        {
            var id = Guid.NewGuid();
            var element = new Element { ID = id };
            var sameId = new Element { ID = id };
            var otherId = new Element { ID = Guid.NewGuid() };

            // Equality is by ID, so anything hashing these has to agree.
            await Assert.That(element.Equals(sameId)).IsTrue();
            await Assert.That(element.Equals((object)sameId)).IsTrue();
            await Assert.That(sameId.GetHashCode()).IsEqualTo(element.GetHashCode());

            await Assert.That(element.Equals(otherId)).IsFalse();
            await Assert.That(element.Equals((object)otherId)).IsFalse();
            await Assert.That(element.Equals(null)).IsFalse();
            await Assert.That(element.Equals("not an element")).IsFalse();

            var set = new HashSet<Element> { element };
            await Assert.That(set.Contains(sameId)).IsTrue();
            await Assert.That(set.Add(sameId)).IsFalse();
            await Assert.That(set.Add(otherId)).IsTrue();

            var dictionary = new Dictionary<Element, int> { [element] = 1 };
            await Assert.That(dictionary.ContainsKey(sameId)).IsTrue();
            await Assert.That(dictionary[sameId]).IsEqualTo(1);
        }

        [Test]
        public async Task ElementHashCollisionsStayDistinct()
        {
            // Guid.GetHashCode folds 128 bits into 32, so distinct IDs can share a hash code. These two do.
            var a = new Guid("ee080de0-48b6-4173-86ba-9c6bc7b989ef");
            var b = new Guid("2e3e559b-4817-441f-aaad-d9b931f696f8");
            await Assert.That(a).IsNotEqualTo(b);
            await Assert.That(b.GetHashCode()).IsEqualTo(a.GetHashCode()).Because("these GUIDs were chosen because their hashes collide");

            using var dm = MakeDatamodel();
            dm.Root = new Element(dm, "root", Guid.NewGuid());
            var first = new Element(dm, "first", a);
            var second = new Element(dm, "second", b);
            dm.Root["first"] = first;
            dm.Root["second"] = second;

            // A colliding hash only shares a bucket; Equals still has to separate them.
            await Assert.That(first.Equals(second)).IsFalse();
            var set = new HashSet<Element> { first, second };
            await Assert.That(set.Count).IsEqualTo(2);

            foreach (var (encoding, version) in new[] { ("binary", 9), ("keyvalues2", 4) })
            {
                using var stream = new MemoryStream();
                dm.Save(stream, encoding, version);
                stream.Seek(0, SeekOrigin.Begin);

                using var loaded = DM.Load(stream);
                await Assert.That(loaded.AllElements.Count).IsEqualTo(3).Because(encoding);
                await Assert.That(loaded.Root.Get<Element>("first").ID).IsEqualTo(a).Because(encoding);
                await Assert.That(loaded.Root.Get<Element>("second").ID).IsEqualTo(b).Because(encoding);
                await Assert.That(loaded.Root.Get<Element>("first").Name).IsEqualTo("first").Because(encoding);
                await Assert.That(loaded.Root.Get<Element>("second").Name).IsEqualTo("second").Because(encoding);
            }
        }

        [Test]
        public async Task TypedArrayAddingRemoving()
        {
            using var dm = MakeDatamodel();
            var array = new ElementArray();

            var elementA = new Element(dm, "a");
            var elementB = new Element();

            await Assert.That(array.Remove(elementB)).IsFalse();
            await Assert.That(array.Remove(elementA)).IsFalse();

            dm.Root["a"] = array;

            await Assert.That(array.Remove(elementB)).IsFalse();
            await Assert.That(array.Remove(elementA)).IsFalse();

            array.Add(elementB);
            await Assert.That(array.Remove(elementB)).IsTrue();

            await Assert.That(array.Remove(elementB)).IsFalse();
            await Assert.That(array.Remove(elementA)).IsFalse();

            ((IList)array).Add(elementA);
            array.Add(elementB);

            array.Add(elementA); // add again?
            array.Remove(elementA);

            await Assert.That(array.Count).IsEqualTo(2); // only removes first instance

            array.Remove(elementA);
            array.Remove(elementB);

            await Assert.That(array.Count).IsEqualTo(0);
        }

        private static async Task Validate_Vmap_Reflection(Datamodel.Datamodel unserialisedVmap)
        {
            await Assert.That(unserialisedVmap.Root.GetType()).IsEqualTo(typeof(CMapRootElement));

            CMapRootElement root = (CMapRootElement)unserialisedVmap.Root;

            await Assert.That(root.World.GetType()).IsEqualTo(typeof(CMapWorld));

            var world = root.World;

            var props = world.GetChildren<CMapEntity>().ToList();
            await Assert.That(props).IsNotEmpty();
            await Assert.That(props[0].GetEntityClassName()).IsNotNull();

            var meshes = world.GetChildren<CMapMesh>().ToList();
            var mesh = meshes[0];

            var vertexData = mesh.MeshData.VertexData;

            await Assert.That(vertexData.Size).IsEqualTo(8);
            await Assert.That(vertexData.Streams[0]["semanticName"]).IsEqualTo("position");

            var typedPolygonMeshData = (CDmePolygonMeshDataStream)vertexData.Streams[0];
            await Assert.That(typedPolygonMeshData.SemanticName).IsEqualTo("position");

            var typedPolygonMeshDataStream = typedPolygonMeshData.Data as Vector3Array;
            await Assert.That(typedPolygonMeshDataStream).IsNotNull();
            await Assert.That(vertexData.GetStreamData<Vector3>("position")).IsSameReferenceAs(typedPolygonMeshDataStream);
            await Assert.That(mesh.MeshData.FaceVertexData.GetStreamData<Vector3>("normal")).IsNotNull();

            await Assert.That(((StringArray)unserialisedVmap.PrefixAttributes["map_asset_references"]!).Count).IsGreaterThan(0);

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

                await Assert.That(elem.GetType()).IsNotEqualTo(typeof(Element)).Because($"Found object {elem.ID} {elem.ClassName} that is still an Element type.");
            }
        }

        [Test]
        public async Task LoadVmap_Reflection_Binary()
        {
            var unserialisedVmap = DM.Load<CMapRootElement>(Path.Combine(TestContext.TestDirectory!, "Resources", "cs2_map.vmap"));
            await Validate_Vmap_Reflection(unserialisedVmap);
        }

        [Test]
        public async Task LoadVmap_Reflection_Text()
        {
            var unserialisedVmap = DM.Load<CMapRootElement>(Path.Combine(TestContext.TestDirectory!, "Resources", "cs2_map.vmap.txt"));
            await Validate_Vmap_Reflection(unserialisedVmap);
        }

        public class NullOwnerElement
        {
            [Test]
            public async Task ElementInitializes()
            {
                var elem = new Element();

                await Assert.That(elem.Owner).IsNull();
                await Assert.That(elem.Count).IsZero();
            }

            [Test]
            public async Task CanImportToRoot()
            {
                var elem = new Element();

                var dm = new DM("test", 1);
                dm.Root = elem;

                await Assert.That(elem.Owner).IsEqualTo(dm);
            }

            [Test]
            public async Task Nested_CanImportToRoot()
            {
                var elem = new Element();
                var elem2 = new Element();
                elem["elem2"] = elem2;
                elem2["woah"] = 5;

                await Assert.That(elem.Owner).IsNull();
                await Assert.That(elem.Count).IsEqualTo(1);
                await Assert.That(elem2.Owner).IsNull();
                await Assert.That(elem2.Count).IsEqualTo(1);

                await Assert.That(elem.First().Key).IsEqualTo("elem2");
                await Assert.That(elem.First().Value).IsEqualTo(elem2);
                await Assert.That(elem2.First().Key).IsEqualTo("woah");
                await Assert.That(elem2.First().Value).IsEqualTo(5);

                var dm = new DM("test", 1);
                dm.Root = elem;

                await Assert.That(elem.Owner).IsEqualTo(dm);
                await Assert.That(elem2.Owner).IsEqualTo(dm);
            }

            [Test]
            public async Task ElementArrayInitializes()
            {
                var elem = new ElementArray();

                await Assert.That(elem.Owner).IsNull();
                await Assert.That(elem.Count).IsZero();
            }
        }

        public class ElementSubclassing
        {
            internal class CustomElement : Element
            {
                public int MyProperty { get; set; } = 1337;
            }

            [Test]
            public async Task ElementSubclassInitializes()
            {
                var elem = new CustomElement();

                await Assert.That(elem.Owner).IsNull();
                await Assert.That(elem.Count).IsZero();
                await Assert.That(elem.MyProperty).IsEqualTo(1337);
            }

            [Test]
            public async Task PropertyAccessByKey()
            {
                var elem = new CustomElement();
                var myprop = elem["MyProperty"];

                await Assert.That(myprop).IsEqualTo(1337);
            }

            [Test]
            public async Task CanBeAssignedToDatamodelRoot()
            {
                var elem = new CustomElement();

                var dm = new DM("test", 1);
                dm.Root = elem;

                await Assert.That(elem.Owner).IsEqualTo(dm);
            }

            [Test]
            public async Task Nested_CanBeAssignedToDatamodelRoot()
            {
                var elem = new CustomElement();
                var elem2 = new CustomElement();
                elem["elem2"] = elem2;
                elem2["woah"] = 5;

                await Assert.That(elem.Owner).IsNull();
                await Assert.That(elem.Count).IsEqualTo(1);
                await Assert.That(elem2.Owner).IsNull();
                await Assert.That(elem2.Count).IsEqualTo(1);

                await Assert.That(elem.First().Key).IsEqualTo("elem2");
                await Assert.That(elem.First().Value).IsEqualTo(elem2);
                await Assert.That(elem2.First().Key).IsEqualTo("woah");
                await Assert.That(elem2.First().Value).IsEqualTo(5);

                var dm = new DM("test", 1);
                dm.Root = elem;

                await Assert.That(elem.Owner).IsEqualTo(dm);
                await Assert.That(elem2.Owner).IsEqualTo(dm);
            }

            [Test]
            public async Task SerializesText()
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

                    using (Assert.Multiple())
                    {
                        await Assert.That(text).Contains("CustomElement");
                        await Assert.That(text).Contains("MyProperty");
                        await Assert.That(text).Contains("1337");
                        await Assert.That(text).Contains("\"as_child\" \"CustomElement\"");
                    }
                }

                await SaveAndConvert(dm, "keyvalues2", 4);

                // binary
                using var stream2 = new MemoryStream();
                dm.Save(stream2, "binary", 9);

                stream2.Position = 0;
                using var reader2 = new BinaryReader(stream2);
                var bytes = reader2.ReadBytes((int)stream2.Length);

                await SaveAndConvert(dm, "binary", 9);
            }
        }

        [Test]
        public async Task Create_Binary_9()
        {
            await Create("binary", 9);
        }
        [Test]
        public async Task Create_Binary_5()
        {
            await Create("binary", 5);
        }
        [Test]
        public async Task Create_Binary_4()
        {
            await Create("binary", 4);
        }
        [Test]
        public async Task Create_Binary_3()
        {
            await Create("binary", 3);
        }
        [Test]
        public async Task Create_Binary_2()
        {
            await Create("binary", 2);
        }

        [Test]
        public async Task Create_KeyValues2_4()
        {
            await Create("keyvalues2", 4);
        }


        [Test]
        public async Task Create_KeyValues2_1()
        {
            await Create("keyvalues2", 1);
        }

        void Get_TF2(Datamodel.Datamodel dm)
        {
            dm.Root.Get<Element>("skeleton").GetArray<Element>("children")[0].Any();
            dm.FormatVersion = 22; // otherwise recent versions of dmxconvert fail
        }

        [Test]
        public async Task Dota2_Binary_9()
        {
            var dm = DM.Load<Element>(Binary_9_File);
            PrintContents(dm);
            dm.Root.Get<Element>("skeleton").GetArray<Element>("children")[0].Any();
            await SaveAndConvert(dm, "binary", 9);

            Cleanup();
        }

        [Test]
        public async Task TF2_Binary_5()
        {
            var dm = DM.Load(Binary_5_File);
            PrintContents(dm);
            Get_TF2(dm);
            await SaveAndConvert(dm, "binary", 5);

            Cleanup();
        }

        [Test]
        public async Task TF2_Binary_4()
        {
            var dm = DM.Load(Binary_4_File);
            PrintContents(dm);
            Get_TF2(dm);
            await SaveAndConvert(dm, "binary", 4);

            Cleanup();
        }

        [Test]
        public async Task TF2_KeyValues2_1()
        {
            var dm = DM.Load(KeyValues2_1_File);
            PrintContents(dm);
            Get_TF2(dm);
            await SaveAndConvert(dm, "keyvalues2", 1);

            Cleanup();
        }

        [Test]
        [MethodDataSource(nameof(GetDmxFiles))]
        public void Unserialize(string path)
        {
            var dm = DM.Load(path, Datamodel.Codecs.DeferredMode.Automatic);
            PrintContents(dm);
            dm.Dispose();
        }

        [Test]
        public void Cs2MapConvert()
        {
            using var dm = DM.Load(Path.Combine(TestContext.TestDirectory!, "Resources", "cs2_map.vmap"));

            // written next to the other test outputs, not into Resources where Unserialize would pick them up as inputs
            dm.Save(OutPath + ".txt", "keyvalues2", 4);
            dm.Save(OutPath + ".vmap", dm.Encoding, dm.EncodingVersion);
        }

        [Test]
        public async Task Import()
        {
            var dm = MakeDatamodel();
            await Populate(dm, "binary", 9);

            var dm2 = MakeDatamodel();
            dm2.Root = dm2.ImportElement(dm.Root, DM.ImportRecursionMode.Recursive, DM.ImportOverwriteMode.All);

            await SaveAndConvert(dm, "keyvalues2", 4);
            await SaveAndConvert(dm, "binary", 9);
        }
    }

    [Category("Performance")]
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
        public async Task Perf_Create_Binary5()
        {
            foreach (var i in Enumerable.Range(0, 1000))
                await Create("binary", 5, true);
        }

        [Test]
        public async Task Perf_CreateElements_Binary5()
        {
            var dm = MakeDatamodel();
            dm.Root = new Element(dm, "root");
            var inner_elem = new Element(dm, "inner_elem");
            var arr = new ElementArray(20000);
            dm.Root["big_array"] = arr;

            foreach (int i in Enumerable.Range(0, 19999))
                arr.Add(inner_elem);

            await SaveAndConvert(dm, "binary", 5);
            Cleanup();
        }

        [Test]
        public async Task Perf_CreateAttributes_Binary5()
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

            await SaveAndConvert(dm, "binary", 5);
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
