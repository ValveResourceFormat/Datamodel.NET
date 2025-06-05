using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

[Generator]
public class ElementFactoryGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var provider = context.SyntaxProvider.CreateSyntaxProvider(

            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, _) => (ClassDeclarationSyntax)ctx.Node

            ).Where(m => m is not null);

        var compilation = context.CompilationProvider.Combine(provider.Collect());

        context.RegisterSourceOutput(compilation, Execute);
    }

    private void Execute(SourceProductionContext context, (Compilation Left, ImmutableArray<ClassDeclarationSyntax> Right) tuple)
    {
        StringBuilder elementFactory = new();

        var assemblies = new List<FactoryAssembly>();

        elementFactory.Append(
            """"
            public class ElementFactory : Datamodel.Codecs.IElementFactory
            {
                public object? GetClass(string assembly, string nameSpace, string classname)
                {
                    switch (assembly)
                    {

            """");

        var (compilation, classDeclList) = tuple;

        var runningAssembly = new FactoryAssembly(compilation.AssemblyName);
        foreach (var classDecl in classDeclList)
        {
            var type = compilation.GetSemanticModel(classDecl.SyntaxTree).GetDeclaredSymbol(classDecl);

            if (type is not null)
            {
                runningAssembly.AddType(type.ContainingNamespace.ToDisplayString(), type);
            }
        }
        assemblies.Add(runningAssembly);

        foreach (var assemblySymbol in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (assemblySymbol.Kind == SymbolKind.NetModule)
            {
                continue;
            }

            var referencedAssembly = new FactoryAssembly(assemblySymbol.Name);

            var assemblyTypes = GetAllClassesFromAssembly(assemblySymbol);
            foreach (var assemblyType in assemblyTypes)
            {
                referencedAssembly.AddType(assemblyType.ContainingNamespace.ToDisplayString(), assemblyType);
            }

            assemblies.Add(referencedAssembly);
        }

        foreach (var assembly in assemblies)
        {
            var namespaceStringBuilder = new StringBuilder();

            namespaceStringBuilder.Append(
                """"

                                switch (nameSpace)
                                {
                """");

            var validNamespaces = 0;
            foreach (var nameSpace in assembly.Namespaces)
            {
                var typeseStringBuilder = new StringBuilder();

                typeseStringBuilder.Append(
                """"

                                        switch (classname)
                                        {
                """");
                var validTypes = 0;
                foreach (var type in nameSpace.Types)
                {
                    if (ValidateType(type, compilation))
                    {
                        validTypes++;

                        typeseStringBuilder.AppendLine(
                        $""""

                                                case "{type.Name}":
                                                    return new {type.ToDisplayString()}();
                    """");
                    }

                }

                typeseStringBuilder.AppendLine(
                """"
                                        }
                """");

                if (validTypes > 0)
                {
                    validNamespaces++;
                    namespaceStringBuilder.AppendLine(
                    $""""

                                        case "{nameSpace.Name}":
                                            {typeseStringBuilder.ToString()}
                                        break;
                    """");
                }

            }

            namespaceStringBuilder.AppendLine(
                """"
                                }
                """");

            if (validNamespaces > 0)
            {
                elementFactory.AppendLine(
                $"""
                             case "{assembly.Name}" :
                                {namespaceStringBuilder.ToString()}
                             break;
                 
                 """);
            }

        }

        elementFactory.AppendLine(
            """"
                    }

                    return null;
                }

            };
            """");

        context.AddSource("ElementFactory.g.cs", elementFactory.ToString());
    }

    private static bool ValidateType(INamedTypeSymbol type, Compilation compilation)
    {
        if (InheritsFromFullName(type, "Datamodel.Element"))
        {
            // no nonsense please
            if (type.IsAbstract || type.IsVirtual || type.TypeParameters.Length > 0)
            {
                return false;
            }

            if (type.DeclaredAccessibility != Accessibility.Internal && type.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }

            // only internal classes in execution assembly are fine
            if (type.DeclaredAccessibility == Accessibility.Internal)
            {
                if (!SymbolEqualityComparer.Default.Equals(compilation.Assembly, type.ContainingAssembly))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllClassesFromAssembly(IAssemblySymbol assembly)
    {
        return GetAllTypesFromNamespace(assembly.GlobalNamespace)
            .Where(t => t.TypeKind == TypeKind.Class);
    }

    public static bool InheritsFromFullName(INamedTypeSymbol type, string fullBaseClassName)
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

    private static IEnumerable<INamedTypeSymbol> GetAllTypesFromNamespace(INamespaceSymbol namespaceSymbol)
    {
        // Get all types directly in this namespace
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            yield return type;

            // Get nested types recursively
            foreach (var nestedType in GetNestedTypes(type))
            {
                yield return nestedType;
            }
        }

        // Recursively process child namespaces
        foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in GetAllTypesFromNamespace(childNamespace))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nestedType in type.GetTypeMembers())
        {
            yield return nestedType;

            // Recursively get nested types within nested types
            foreach (var deeplyNestedType in GetNestedTypes(nestedType))
            {
                yield return deeplyNestedType;
            }
        }
    }

    private class FactoryAssembly(string name)
    {
        public string Name = name;

        public HashSet<FactoryNamespace> Namespaces = new();

        public void AddType(string nameSpaceName, INamedTypeSymbol type)
        {
            NamespacesContainsNamespace(nameSpaceName, out FactoryNamespace? foundNameSpace);

            if (foundNameSpace == null)
            {
                foundNameSpace = new FactoryNamespace(nameSpaceName);
                Namespaces.Add(foundNameSpace);
            }

            foundNameSpace.Types.Add(type);
        }

        private bool NamespacesContainsNamespace(string namespaceName, out FactoryNamespace? outNameSpace)
        {
            foreach (var nameSpace in Namespaces)
            {
                if (nameSpace.Name == namespaceName)
                {
                    outNameSpace = nameSpace;
                    return true;
                }
            }

            outNameSpace = null;
            return false;
        }
    }

    private class FactoryNamespace(string name)
    {
        public string Name = name;
        public List<INamedTypeSymbol> Types = new();
    }
}