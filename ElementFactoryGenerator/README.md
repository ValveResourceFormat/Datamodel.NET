Source generator that lets Datamodel.NET load and save `Element` subclasses without reflection. It ships inside the KeyValues2 package, so referencing the package is all a project needs.

For every subclass of `Datamodel.Element` in the assembly it emits an `ElementFactory` that constructs the class by name, lists its public properties with their attribute names, and registers itself with `Datamodel.RegisterElementFactory` when the assembly is initialised.
