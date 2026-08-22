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
