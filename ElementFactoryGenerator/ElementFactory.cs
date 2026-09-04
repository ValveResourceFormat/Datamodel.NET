using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

/// <summary>
/// Emits an <c>ElementFactory</c> into every assembly that declares subclasses of <c>Datamodel.Element</c>.
/// The factory constructs those classes by name and describes their properties, so that Datamodel.NET can load
/// and save typed elements without reflection. It registers itself when the assembly is initialised.
/// </summary>
[Generator]
public class ElementFactoryGenerator : IIncrementalGenerator
{
    /// <summary>The one library type the generator matches by name, since only the Format namespace is compiled into it.</summary>
    const string ElementTypeName = "Datamodel.Element";

    static readonly string NamingConventionTypeName = typeof(Datamodel.Format.AttributeNamingConventionAttribute).FullName!;
    static readonly string PropertyAttributeTypeName = typeof(Datamodel.Format.DMProperty).FullName!;

    private static readonly DiagnosticDescriptor AmbiguousElementClassName = new(
        id: "DMX001",
        title: "Ambiguous Datamodel element class name",
        messageFormat: "Element class name '{0}' is declared more than once in namespace '{1}'; '{2}' will be used for deserialisation and '{3}' will be ignored",
        category: "Datamodel",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Datamodel files identify elements by their simple class name, so two Element subclasses sharing a name in the same namespace cannot be told apart when deserialising.");

    private static readonly DiagnosticDescriptor InaccessibleElementClass = new(
        id: "DMX002",
        title: "Datamodel element class is not accessible to the generated factory",
        messageFormat: "Element class '{0}' is not visible outside its declaring type, so the generated ElementFactory can neither construct it nor bind its properties; make it internal or public",
        category: "Datamodel",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedNamingConvention = new(
        id: "DMX003",
        title: "Naming convention attribute cannot be constructed by the generated factory",
        messageFormat: "The naming convention attribute on '{0}' uses a constructor argument that the generated ElementFactory cannot reproduce; the property names are used unchanged",
        category: "Datamodel",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var provider = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
            transform: static (ctx, _) => ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node) as INamedTypeSymbol)
            .Where(static symbol => symbol is not null && InheritsFrom(symbol, ElementTypeName));

        var compilation = context.CompilationProvider.Combine(provider.Collect());

        context.RegisterSourceOutput(compilation, Execute);
    }

    private static void Execute(SourceProductionContext context, (Compilation Left, ImmutableArray<INamedTypeSymbol?> Right) tuple)
    {
        var (compilation, symbols) = tuple;

        // a partial class is reported once per declaration
        var elementTypes = new List<INamedTypeSymbol>();
        foreach (var symbol in symbols)
        {
            if (symbol is not null && !elementTypes.Contains(symbol, SymbolEqualityComparer.Default))
            {
                elementTypes.Add(symbol);
            }
        }

        var classes = new List<ElementClass>();

        foreach (var type in elementTypes.OrderBy(type => type.ToDisplayString()))
        {
            if (type.IsGenericType || type.ContainingType?.IsGenericType == true)
            {
                continue;
            }

            if (!compilation.IsSymbolAccessibleWithin(type, compilation.Assembly))
            {
                context.ReportDiagnostic(Diagnostic.Create(InaccessibleElementClass, type.Locations.FirstOrDefault() ?? Location.None, type.ToDisplayString()));
                continue;
            }

            classes.Add(new ElementClass(type));
        }

        if (classes.Count == 0)
        {
            return;
        }

        var emitter = new Emitter(context, compilation, classes);
        context.AddSource("ElementFactory.g.cs", emitter.Emit());
    }

    private static bool InheritsFrom(INamedTypeSymbol type, string fullBaseClassName)
    {
        var current = type.BaseType;
        while (current != null)
        {
            if (current.ToDisplayString() == fullBaseClassName)
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private sealed class ElementClass(INamedTypeSymbol type)
    {
        public INamedTypeSymbol Type { get; } = type;
        public string Namespace { get; } = type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString();
        public bool IsConstructible => !Type.IsAbstract && Type.InstanceConstructors.Any(ctor => ctor.Parameters.Length == 0 && ctor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal);

        /// <summary>
        /// The Element subclasses this class derives from, base class first, this class last.
        /// </summary>
        public IEnumerable<INamedTypeSymbol> Chain
        {
            get
            {
                var chain = new List<INamedTypeSymbol>();
                for (var current = Type; current != null && current.ToDisplayString() != ElementTypeName; current = current.BaseType)
                {
                    chain.Add(current);
                }

                chain.Reverse();
                return chain;
            }
        }
    }

    /// <summary>
    /// Writes the factory source: the overview first (construction and the schema list), then one section per class
    /// holding its property bindings and the accessors they need. A class in an inheritance chain gets one section, shared by its subclasses.
    /// </summary>
    private sealed class Emitter(SourceProductionContext context, Compilation compilation, List<ElementClass> classes)
    {
        /// <summary>Types are written fully qualified but without the global:: prefix, which the generated namespace makes unnecessary.</summary>
        static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

        /// <summary>Names a section may not take, because the factory uses them itself or they start a qualified name it writes.</summary>
        static readonly HashSet<string> ReservedNames = ["ElementFactory", "Instance", "Register", "Schemas", "Create", "AllSchemas", "Element", "ElementSchema", "PropertyBinding", "IElementFactory", "Datamodel", "System", "KeyValues2"];

        readonly Dictionary<INamedTypeSymbol, string> SectionNames = new(SymbolEqualityComparer.Default);

        public string Emit()
        {
            AssignSectionNames();

            // the generated factory is stamped with the version of the library it was generated for, which is the one the consumer compiles against
            var version = LibraryVersion(compilation.GetTypeByMetadataName(ElementTypeName)?.ContainingAssembly);

            var source = new StringBuilder();

            // marked as generated so that analyzers and documentation warnings of the consuming project leave it alone,
            // and internal so that it does not become part of the consuming assembly's public surface
            source.Append($$"""
                // <auto-generated/>
                // Generated by KeyValues2.ElementFactoryGenerator for Datamodel.NET {{version}}.
                //
                // Constructs the Element subclasses of this assembly by class name and lists the properties each of them stores as
                // attributes, so that Datamodel.NET loads and saves them without reflection. Registers itself when the assembly is initialised.
                #nullable enable
                #pragma warning disable

                using System.Runtime.CompilerServices;

                namespace KeyValues2.Generated
                {
                    using Element = Datamodel.Element;
                    using ElementSchema = Datamodel.ElementSchema;
                    using PropertyBinding = Datamodel.PropertyBinding;

                    [System.CodeDom.Compiler.GeneratedCode("KeyValues2.ElementFactoryGenerator", "{{version}}")]
                    internal sealed class ElementFactory : Datamodel.Codecs.IElementFactory
                    {
                        public static readonly ElementFactory Instance = new ElementFactory();

                        [ModuleInitializer]
                        internal static void Register()
                        {
                            Datamodel.Datamodel.RegisterElementFactory(Instance);
                        }

                        public System.Collections.Generic.IReadOnlyList<ElementSchema> Schemas
                        {
                            get { return AllSchemas; }
                        }

                        /// <summary>
                        /// Constructs the class with the given name in the given namespace, or returns null when this assembly has none.
                        /// </summary>
                        public Element? Create(string nameSpace, string className)
                        {
                            switch (nameSpace)
                            {

                """);

            foreach (var byNamespace in classes.Where(elementClass => elementClass.IsConstructible).GroupBy(elementClass => elementClass.Namespace).OrderBy(group => group.Key))
            {
                source.AppendLine($"                case {Literal(byNamespace.Key)}:");
                source.AppendLine("                    switch (className)");
                source.AppendLine("                    {");

                var emitted = new Dictionary<string, INamedTypeSymbol>();
                foreach (var elementClass in byNamespace)
                {
                    var type = elementClass.Type;

                    // Datamodel files record only the simple class name, so a duplicate is
                    // unresolvable: emit the first and tell the user the rest are unreachable.
                    if (emitted.TryGetValue(type.Name, out var existing))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(AmbiguousElementClassName, type.Locations.FirstOrDefault() ?? Location.None,
                            type.Name, byNamespace.Key, existing.ToDisplayString(), type.ToDisplayString()));
                        continue;
                    }

                    emitted.Add(type.Name, type);
                    source.AppendLine($"                        case {Literal(type.Name)}: return new {TypeName(type)}();");
                }

                source.AppendLine("                    }");
                source.AppendLine("                    break;");
            }

            source.Append("""
                            }

                            return null;
                        }

                        /// <summary>
                        /// The schema of every concrete class: the properties of its base classes first, then its own.
                        /// </summary>
                        static readonly ElementSchema[] AllSchemas = new ElementSchema[]
                        {

                """);

            foreach (var elementClass in classes.Where(elementClass => !elementClass.Type.IsAbstract))
            {
                var groups = string.Concat(elementClass.Chain.Select(type => $", {SectionNames[type]}.Properties"));
                source.AppendLine($"            new ElementSchema(typeof({TypeName(elementClass.Type)}), {Literal(elementClass.Type.Name)}{groups}),");
            }

            source.AppendLine("        };");

            foreach (var type in classes.SelectMany(elementClass => elementClass.Chain).Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>().OrderBy(type => type.ToDisplayString()))
            {
                source.AppendLine();
                EmitSection(source, type);
            }

            source.Append("""
                    }
                }

                """);

            return source.ToString();
        }

        /// <summary>
        /// The package version of the library, from its informational version attribute, falling back to the assembly version.
        /// </summary>
        static string LibraryVersion(IAssemblySymbol? library)
        {
            if (library is null)
            {
                return "unknown";
            }

            var informational = library.GetAttributes()
                .FirstOrDefault(attr => attr.AttributeClass?.ToDisplayString() == typeof(System.Reflection.AssemblyInformationalVersionAttribute).FullName)
                ?.ConstructorArguments.FirstOrDefault().Value as string;

            if (!string.IsNullOrEmpty(informational))
            {
                // without the source revision that the SDK appends after a plus sign
                var plus = informational!.IndexOf('+');
                return plus < 0 ? informational : informational.Substring(0, plus);
            }

            return library.Identity.Version.ToString(3);
        }

        /// <summary>
        /// Names each class's section after the class. A name that is reserved or shared by another class gets its namespace appended.
        /// </summary>
        void AssignSectionNames()
        {
            var types = classes.SelectMany(elementClass => elementClass.Chain).Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>().ToList();
            var taken = new HashSet<string>(ReservedNames);

            // the first identifier of every qualified name the factory writes must not be shadowed by a section
            foreach (var type in types)
            {
                var root = type.ContainingNamespace;
                while (root is { IsGlobalNamespace: false, ContainingNamespace.IsGlobalNamespace: false })
                    root = root.ContainingNamespace;
                if (root is { IsGlobalNamespace: false })
                    taken.Add(root.Name);
            }

            foreach (var group in types.GroupBy(type => type.Name))
            {
                var unique = group.Count() == 1 && !taken.Contains(group.Key);

                foreach (var type in group)
                {
                    var name = unique ? type.Name : type.ToDisplayString().Replace('.', '_');
                    while (!taken.Add(name))
                        name += "_";

                    SectionNames[type] = name;
                }
            }
        }

        /// <summary>
        /// Writes the nested class holding the bindings of the properties <paramref name="type"/> declares.
        /// </summary>
        void EmitSection(StringBuilder source, INamedTypeSymbol type)
        {
            var naming = NamingConventionFor(type, out var namingField);
            var kind = type.IsAbstract ? " (abstract, shared by its subclasses)" : string.Empty;

            source.AppendLine($"        // {type.ToDisplayString()}{kind}: {naming?.Description ?? "attribute names are the property names"}");
            source.AppendLine($"        static class {SectionNames[type]}");
            source.AppendLine("        {");

            if (namingField != null)
            {
                source.AppendLine($"            {namingField}");
                source.AppendLine();
            }

            var accessors = new StringBuilder();
            var properties = type.GetMembers().OfType<IPropertySymbol>()
                .Where(property => !property.IsStatic && !property.IsIndexer && property.DeclaredAccessibility == Accessibility.Public && property.GetMethod is not null && property.ExplicitInterfaceImplementations.Length == 0)
                .ToList();

            if (properties.Count == 0)
            {
                source.AppendLine("            public static readonly PropertyBinding[] Properties = new PropertyBinding[0];");
            }
            else
            {
                source.AppendLine("            public static readonly PropertyBinding[] Properties = new PropertyBinding[]");
                source.AppendLine("            {");

                foreach (var property in properties)
                {
                    var elementType = TypeName(type);
                    var valueType = TypeName(property.Type);
                    var attributeName = AttributeNameFor(property, naming, valueType);
                    var getter = GetterFor(type, property, accessors);
                    var setter = SetterFor(type, property, accessors);

                    source.AppendLine($"                PropertyBinding.Create<{elementType}, {valueType}>({Literal(property.Name)}, {attributeName}, {getter}, {setter}),");
                }

                source.AppendLine("            };");
            }

            source.Append(accessors);
            source.AppendLine("        }");
        }

        sealed class NamingConvention
        {
            /// <summary>One of the library's own attribute classes, applied by the generator, or null for a custom one applied at run time through the section's Naming field.</summary>
            public System.Type? BuiltIn;
            public string Description = string.Empty;
        }

        /// <summary>
        /// Finds the naming convention attribute applied to <paramref name="type"/>. The library's own conventions are applied by the generator;
        /// any other is constructed at run time in a field of the section, whose declaration is returned in <paramref name="field"/>.
        /// </summary>
        NamingConvention? NamingConventionFor(INamedTypeSymbol type, out string? field)
        {
            field = null;

            var attribute = type.GetAttributes().FirstOrDefault(attr => attr.AttributeClass is not null && InheritsFrom(attr.AttributeClass, NamingConventionTypeName));
            if (attribute?.AttributeClass is null)
            {
                return null;
            }

            // the library's own attributes and rules are compiled into this generator, so they are applied here with the same code that runs in the library
            var attributeClassName = attribute.AttributeClass.ToDisplayString();
            var builtIn = Datamodel.Format.NamingConventions.BuiltIn.FirstOrDefault(convention => convention.FullName == attributeClassName);
            if (builtIn is not null)
            {
                return new NamingConvention { BuiltIn = builtIn, Description = $"attribute names by {builtIn.Name}" };
            }

            var arguments = new List<string>();
            foreach (var argument in attribute.ConstructorArguments)
            {
                var literal = Literal(argument);
                if (literal is null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedNamingConvention, type.Locations.FirstOrDefault() ?? Location.None, type.ToDisplayString()));
                    return null;
                }

                arguments.Add(literal);
            }

            var initializers = new List<string>();
            foreach (var argument in attribute.NamedArguments)
            {
                var literal = Literal(argument.Value);
                if (literal is null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedNamingConvention, type.Locations.FirstOrDefault() ?? Location.None, type.ToDisplayString()));
                    return null;
                }

                initializers.Add($"{argument.Key} = {literal}");
            }

            var initializer = initializers.Count > 0 ? $" {{ {string.Join(", ", initializers)} }}" : string.Empty;
            field = $"static readonly {NamingConventionTypeName} Naming = new {TypeName(attribute.AttributeClass)}({string.Join(", ", arguments)}){initializer};";

            return new NamingConvention { Description = $"attribute names by {attributeClassName} at run time" };
        }

        /// <summary>
        /// The expression for the attribute name of a property: a literal when the generator can compute it, otherwise a call to the section's naming field.
        /// </summary>
        static string AttributeNameFor(IPropertySymbol property, NamingConvention? naming, string valueType)
        {
            var attribute = property.GetAttributes().FirstOrDefault(attr => attr.AttributeClass?.ToDisplayString() == PropertyAttributeTypeName);

            if (attribute is not null && attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string explicitName)
            {
                return Literal(explicitName);
            }

            if (naming is null)
            {
                return Literal(property.Name);
            }

            if (naming.BuiltIn is null)
            {
                return $"Naming.GetAttributeName({Literal(property.Name)}, typeof({valueType}))";
            }

            var propertyTypeName = property.Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(RuntimeTypeNameFormat);
            return Literal(Datamodel.Format.NamingConventions.Apply(naming.BuiltIn, property.Name, propertyTypeName));
        }

        /// <summary>Prints a type the way <see cref="System.Type.FullName"/> does for the types the naming conventions distinguish, such as System.Int32.</summary>
        static readonly SymbolDisplayFormat RuntimeTypeNameFormat = TypeFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

        string GetterFor(INamedTypeSymbol type, IPropertySymbol property, StringBuilder accessors)
        {
            if (compilation.IsSymbolAccessibleWithin(property.GetMethod!, compilation.Assembly))
            {
                return $"e => e.{property.Name}";
            }

            var accessor = $"Get_{property.Name}";
            accessors.AppendLine();
            accessors.AppendLine($"            [UnsafeAccessor(UnsafeAccessorKind.Method, Name = \"get_{property.Name}\")]");
            accessors.AppendLine($"            static extern {TypeName(property.Type)} {accessor}({TypeName(type)} target);");

            return $"e => {accessor}(e)";
        }

        string SetterFor(INamedTypeSymbol type, IPropertySymbol property, StringBuilder accessors)
        {
            var setMethod = property.SetMethod;

            if (setMethod is null)
            {
                return "null";
            }

            if (!setMethod.IsInitOnly && compilation.IsSymbolAccessibleWithin(setMethod, compilation.Assembly))
            {
                return $"(e, v) => e.{property.Name} = v";
            }

            var accessor = $"Set_{property.Name}";
            accessors.AppendLine();

            // an init accessor can only be called from an initializer, so an auto-property is assigned through its backing field
            var backingField = type.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(field => SymbolEqualityComparer.Default.Equals(field.AssociatedSymbol, property));
            if (setMethod.IsInitOnly && backingField is not null)
            {
                accessors.AppendLine($"            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = {Literal(backingField.Name)})]");
                accessors.AppendLine($"            static extern ref {TypeName(property.Type)} {accessor}({TypeName(type)} target);");

                return $"(e, v) => {accessor}(e) = v";
            }

            accessors.AppendLine($"            [UnsafeAccessor(UnsafeAccessorKind.Method, Name = \"set_{property.Name}\")]");
            accessors.AppendLine($"            static extern void {accessor}({TypeName(type)} target, {TypeName(property.Type)} value);");

            return $"(e, v) => {accessor}(e, v)";
        }

        static string TypeName(ITypeSymbol type)
        {
            return type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(TypeFormat);
        }

        static string Literal(string value)
        {
            return SymbolDisplay.FormatLiteral(value, quote: true);
        }

        /// <summary>
        /// Formats an attribute argument as C# source, or returns null when it cannot be reproduced.
        /// </summary>
        static string? Literal(TypedConstant constant)
        {
            switch (constant.Kind)
            {
                case TypedConstantKind.Primitive:
                    return constant.Value is null ? "null" : SymbolDisplay.FormatPrimitive(constant.Value, quoteStrings: true, useHexadecimalNumbers: false);
                case TypedConstantKind.Enum:
                    return constant.Type is null ? null : $"({TypeName(constant.Type)}){SymbolDisplay.FormatPrimitive(constant.Value!, quoteStrings: false, useHexadecimalNumbers: false)}";
                case TypedConstantKind.Type:
                    return constant.Value is ITypeSymbol type ? $"typeof({TypeName(type)})" : null;
                default:
                    return null;
            }
        }
    }
}
