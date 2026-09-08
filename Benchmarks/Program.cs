using System.Diagnostics;
using Datamodel.Codecs;
using Tests.VMAP;
using DM = Datamodel.Datamodel;

// Measures loading a vmap as plain elements and as the typed classes of Tests/ValveMap.cs, and saving the typed model.
//
//   Benchmarks [--iterations N] <file or directory>...
//
// A directory contributes every .vmap inside it, largest last. Each figure is the best of N iterations (default 1).
// "typed alloc" is the managed memory allocated by the typed load, "live heap" the managed heap that survives it.

// dots as decimal separators whatever the machine locale, so that tables can be pasted anywhere
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

var iterations = 1;
var inputs = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--iterations" && i + 1 < args.Length)
    {
        iterations = int.Parse(args[++i]);
        continue;
    }

    inputs.Add(args[i]);
}

if (inputs.Count == 0)
{
    Console.WriteLine("usage: Benchmarks [--iterations N] <file or directory>...");
    return 1;
}

var files = inputs
    .SelectMany(input => Directory.Exists(input) ? Directory.GetFiles(input, "*.vmap") : [input])
    .OrderBy(file => new FileInfo(file).Length)
    .ToList();

Console.WriteLine($"{"map",-26} {"file size",9} {"elements",9} | {"read file",10} {"untyped load",13} {"typed load",11} {"typed/untyped",13} | {"typed alloc",11} {"live heap",9} | {"binary save",11}");

foreach (var path in files)
{
    var name = Path.GetFileName(path);
    var size = new FileInfo(path).Length;

    var read = Time(() => File.ReadAllBytes(path), out var bytes);

    var untyped = double.MaxValue;
    var typed = double.MaxValue;
    var save = double.MaxValue;
    long allocated = 0, heap = 0, elements = 0;

    for (var i = 0; i < iterations; i++)
    {
        Collect();
        untyped = Math.Min(untyped, Time(() => DM.Load(new MemoryStream(bytes, false), DeferredMode.Disabled), out var plain));
        elements = plain.AllElements.Count;
        plain.Dispose();

        Collect();
        var before = GC.GetTotalAllocatedBytes(true);
        typed = Math.Min(typed, Time(() => DM.Load<CMapRootElement>(new MemoryStream(bytes, false), DeferredMode.Disabled), out var map));
        allocated = GC.GetTotalAllocatedBytes(true) - before;
        heap = GC.GetTotalMemory(true);

        if (map.Root is not CMapRootElement)
        {
            throw new InvalidOperationException($"{name}: the root was not loaded as {nameof(CMapRootElement)}");
        }

        save = Math.Min(save, Time(() => { map.Save(Stream.Null, "binary", 9); return 0; }, out _));
        map.Dispose();
    }

    Console.WriteLine($"{name,-26} {size / 1048576.0,7:F1}MB {elements,9} | {Duration(read),10} {Duration(untyped),13} {Duration(typed),11} {typed / untyped,12:F2}x | {allocated / 1048576.0,9:F0}MB {heap / 1048576.0,7:F0}MB | {Duration(save),11}");
}

return 0;

/// <summary>Milliseconds up to a tenth of a second, seconds with two decimals above.</summary>
static string Duration(double milliseconds)
{
    return milliseconds < 100 ? $"{milliseconds:F0}ms" : $"{milliseconds / 1000:F2}s";
}

static double Time<T>(Func<T> action, out T result)
{
    var stopwatch = Stopwatch.StartNew();
    result = action();
    return stopwatch.Elapsed.TotalMilliseconds;
}

static void Collect()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}
