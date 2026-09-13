Source generator that lets Datamodel.NET load and save `Element` subclasses without reflection. It ships inside the KeyValues2 package, so referencing the package is all a project needs.

For every subclass of `Datamodel.Element` in the assembly it emits an `ElementFactory` that constructs the class by name, lists its public properties (except those marked `[DMIgnore]`) with their attribute names, and registers itself with `Datamodel.RegisterElementFactory` when the assembly is initialised.

A generic subclass cannot be listed, since its type arguments are only known where it is constructed. Declare it `partial` and the generator gives it a static constructor that registers the schema of each constructed type before its first instance is created. A generic class that is not partial, is nested, or has its own static constructor gets warning DMX004 and its properties are not stored as attributes. Generic classes are not constructed when loading.
