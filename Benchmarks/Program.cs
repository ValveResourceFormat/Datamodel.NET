using System.Diagnostics;
using System.Runtime.CompilerServices;
using Datamodel.Codecs;
using Tests.VMAP;
using DM = Datamodel.Datamodel;

// Measures loading a vmap as plain elements and as the typed classes of Tests.VMAP, and saving the typed model.
//
//   Benchmarks [--iterations N] <file or directory>...
//
// A directory contributes every .vmap inside it, largest last. Each figure is the best of N iterations (default 1).
// "typed alloc" is the managed memory allocated by the typed load, "live heap" the managed heap the typed model keeps, without the file bytes.

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
    var best = new Sample(double.MaxValue, double.MaxValue, double.MaxValue, 0, 0, 0);

    for (var i = 0; i < iterations; i++)
    {
        var sample = Measure(bytes, name);
        best = new Sample(Math.Min(best.Untyped, sample.Untyped), Math.Min(best.Typed, sample.Typed), Math.Min(best.Save, sample.Save), sample.Allocated, sample.Heap, sample.Elements);
    }

    Console.WriteLine($"{name,-26} {size / 1048576.0,7:F1}MB {best.Elements,9} | {Duration(read),10} {Duration(best.Untyped),13} {Duration(best.Typed),11} {best.Typed / best.Untyped,12:F2}x | {best.Allocated / 1048576.0,9:F0}MB {best.Heap / 1048576.0,7:F0}MB | {Duration(best.Save),11}");
}

return 0;

/// <summary>
/// One load-save round in a frame of its own, so that every model of the round is garbage once it returns.
/// The main loop keeps temporaries alive across iterations, which would count the previous model into the next heap figure.
/// </summary>
[MethodImpl(MethodImplOptions.NoInlining)]
static Sample Measure(byte[] bytes, string name)
{
    Collect();
    var untyped = Time(() => DM.Load(new MemoryStream(bytes, false), DeferredMode.Disabled), out var plain);
    var elements = plain.AllElements.Count;
    plain.Dispose();
    plain = null!;

    Collect();
    var before = GC.GetTotalAllocatedBytes(true);
    var typed = Time(() => DM.Load<CMapRootElement>(new MemoryStream(bytes, false), DeferredMode.Disabled), out var map);
    var allocated = GC.GetTotalAllocatedBytes(true) - before;
    var heap = GC.GetTotalMemory(true) - bytes.Length;

    if (map.Root is not CMapRootElement)
    {
        throw new InvalidOperationException($"{name}: the root was not loaded as {nameof(CMapRootElement)}");
    }

    var save = Time(() => { map.Save(Stream.Null, "binary", 9); return 0; }, out _);
    map.Dispose();

    return new Sample(untyped, typed, save, allocated, heap, elements);
}

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

/// <summary>Times in milliseconds, memory in bytes.</summary>
record struct Sample(double Untyped, double Typed, double Save, long Allocated, long Heap, long Elements);
