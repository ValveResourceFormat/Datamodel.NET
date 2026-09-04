Datamodel.NET is a library which implements the Datamodel structure and Datamodel Exchange (DMX) file format.

Datamodel is a strongly-typed generic data structure designed by Valve Corporation. It is primarily used as a developer storage format for meshes, animations, and maps.  

## Usage
```shell
dotnet add package KeyValues2
```

```cs
using Datamodel;

// Load a file with unknown layout
using var dm = Datamodel.Load("my_file.dmx");
var element = dm.Root;
var value = element.Get<string>("my_property");

// Load a file with a known layout
using var map = Datamodel.Load<CMapRootElement>("fy_pool.vmap");
var root = (CMapRootElement)map.Root;
Debug.Assert(root.IsPrefab == false);

// Layout definition
// Full implementation can be found here:
// https://github.com/ValveResourceFormat/Datamodel.NET/blob/master/Tests/ValveMap.cs
[LowercaseProperties]
class CMapRootElement : Element
{
    public bool IsPrefab { get; set; }
    public int EditorBuild { get; set; } = 8600;
    public int EditorVersion { get; set; } = 400;
}
```

## Features

* Support for all known versions of Valve's `binary` and `keyvalues2` DMX encodings
* Inline documentation
* Binary codec supports just-in-time attribute loading
* Write your own codecs with the `ICodec` interface
* Serialize and deserialize support for Datamodel.Element subclasses
* Prefix attributes (such as the `map_asset_references` of a vmap) survive a load and save cycle in both encodings
* Output laid out like Valve's own serializers: `binary` 9 stores the prefix attributes as an element right after the root, `keyvalues2` uses tab indentation and one array item per line

## Typed elements

`Datamodel.Load<T>` gives every element whose class name matches a subclass of `Element` in the namespace of `T` that subclass.
Elements with no matching class are loaded as plain `Element`s.

How the classes are found:

* The `KeyValues2.ElementFactoryGenerator` source generator emits an `ElementFactory` into every assembly that references this package.
* Loading asks those factories, the one in the assembly of `T` first. No reflection over types happens at load time.

How a subclass maps onto the file:

* Every public property is an attribute. The attribute name is the property name, adjusted by `[LowercaseProperties]`, `[CamelCaseProperties]` or `[DMProperty]`.
* Attributes of the file that no property claims are kept as plain attributes and written back unchanged.
* Every property is always written, like in Valve's datamodel. Loading an older file through a class with newer properties adds those with their default values.
* Assigning a file attribute to a property of an incompatible type throws an `InvalidDataException` naming the property, which usually means the class does not match the format.

## Serialization

```c#
var HelloWorld = new Datamodel.Datamodel("helloworld", 1); // must provide a format name (can be anything) and version

HelloWorld.Root = new Datamodel.Element(HelloWorld, "my_root");
HelloWorld.Root["Hello"] = "World"; // any supported attribute type can be assigned

var MyString = HelloWorld.Root.Get<string>("Hello");

HelloWorld.Save("hello world.dmx", "keyvalues2", 1); // must provide an encoding name and version
```

```vdf
<--! dmx encoding keyvalues2 1 format helloworld 1>
{
    "Hello" "string" "World"
}
```

## Attributes

The following .NET types are supported as Datamodel attributes:

* `int`
* `float`
* `bool`
* `string`
* `byte`
* `byte[]`
* `Vector2`
* `Vector3`
* `Vector4` / `Quaternion`
* `Matrix4x4`
* `ulong`
* `System.TimeSpan`

Additionally, the following Datamodel.NET types are supported:

* `Element` (a named collection of attributes)
* `QAngle`

`IList<T>` collections of the above types are also supported. (This can be a bit confusing given that both `byte` and `byte[]` are valid attribute types; use the `ByteArray` type if you run into trouble.)


## License

Datamodel.NET is licensed under the [MIT License](LICENSE).

The sample `.dmx` and `.vmap` files under [`Tests/Resources`](Tests/Resources) are derived from Valve
Corporation game content and are included solely as test fixtures. They are **not** covered by the MIT
license above and remain the property of Valve Corporation. They are not distributed in the NuGet package.
