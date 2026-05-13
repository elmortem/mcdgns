using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DiagramGenerator.ClassGraph;

public class SourceGraphBuilder : IGraphBuilder
{
    private SemanticModel? _semanticModel;
    private Dictionary<string, List<TypeDeclarationSyntax>> _partialTypes = new();
    private HashSet<string> _systemNamespaces = new()
    {
        "System", "Microsoft", "System.Collections", "System.Collections.Generic",
        "System.Linq", "System.Threading", "System.Threading.Tasks", "System.Text"
    };

    public bool ExcludeSystemTypes { get; set; } = false;
    public Visibility MinimumVisibility { get; set; } = Visibility.Public;
    public bool Verbose { get; set; } = false;
    private int _filesProcessed = 0;
    private int _classesFound = 0;
    private List<string> _errors = new();

    public Graph Build(IEnumerable<string> files, IEnumerable<string> nsList, IEnumerable<string> typenameList, bool inheritanceOnly)
    {
        // Reset state for multiple Build() calls
        _partialTypes = new Dictionary<string, List<TypeDeclarationSyntax>>();
        _errors = new List<string>();
        _filesProcessed = 0;
        _classesFound = 0;

        var graph = new Graph();
        var fileList = files.ToList();

        // Create compilation for semantic analysis
        var syntaxTrees = new List<SyntaxTree>();
        foreach (var file in fileList)
        {
            if (!File.Exists(file))
            {
                LogError($"File not found: {file}");
                continue;
            }

            try
            {
                var code = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(code, path: file);
                syntaxTrees.Add(tree);
            }
            catch (Exception ex)
            {
                LogError($"Error reading file {file}: {ex.Message}");
            }
        }

        // Create compilation with basic references
        var compilation = CSharpCompilation.Create("DiagramAnalysis")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddSyntaxTrees(syntaxTrees);

        // First pass: collect partial types
        foreach (var tree in syntaxTrees)
        {
            try
            {
                _semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                _filesProcessed++;
                LogVerbose($"Processing file: {tree.FilePath}");

                // Find all classes, interfaces, records, structs
                var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                // Find all enums
                var enumDeclarations = root.DescendantNodes().OfType<EnumDeclarationSyntax>();

                foreach (var typeDecl in typeDeclarations)
                {
                    // Filter by type name
                    if (typenameList.Any() && !typenameList.Contains(typeDecl.Identifier.Text))
                        continue;

                    // Filter by Namespace
                    var ns = GetNamespace(typeDecl);
                    if (nsList.Any() && !nsList.Contains(ns))
                        continue;

                    // Group partial types
                    var fullName = GetFullTypeName(typeDecl);
                    if (typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
                    {
                        if (!_partialTypes.ContainsKey(fullName))
                        {
                            _partialTypes[fullName] = new List<TypeDeclarationSyntax>();
                        }
                        _partialTypes[fullName].Add(typeDecl);
                    }
                    else
                    {
                        var @class = BuildClassFromSyntax(typeDecl, ns);
                        graph.AddClass(@class);
                        _classesFound++;
                    }
                }

                // Process enums
                foreach (var enumDecl in enumDeclarations)
                {
                    // Filter by type name
                    if (typenameList.Any() && !typenameList.Contains(enumDecl.Identifier.Text))
                        continue;

                    // Filter by Namespace
                    var ns = GetNamespace(enumDecl);
                    if (nsList.Any() && !nsList.Contains(ns))
                        continue;

                    var @class = BuildClassFromEnum(enumDecl, ns);
                    graph.AddClass(@class);
                    _classesFound++;
                }
            }
            catch (Exception ex)
            {
                LogError($"Error processing syntax tree {tree.FilePath}: {ex.Message}");
            }
        }

        // Second pass: process partial types
        foreach (var kvp in _partialTypes)
        {
            try
            {
                var mergedClass = MergePartialTypes(kvp.Value);
                graph.AddClass(mergedClass);
                _classesFound++;
            }
            catch (Exception ex)
            {
                LogError($"Error merging partial type {kvp.Key}: {ex.Message}");
            }
        }

        graph.RebuildRelation(inheritanceOnly);

        // Print summary
        if (Verbose)
        {
            Console.WriteLine($"Summary: Processed {_filesProcessed} files, found {_classesFound} types");
            if (_errors.Any())
            {
                Console.WriteLine($"Errors encountered: {_errors.Count}");
                foreach (var error in _errors)
                {
                    Console.WriteLine($"  - {error}");
                }
            }
        }

        return graph;
    }

    private void LogVerbose(string message)
    {
        if (Verbose)
        {
            Console.WriteLine(message);
        }
    }

    private void LogError(string message)
    {
        _errors.Add(message);
        if (Verbose)
        {
            Console.Error.WriteLine($"ERROR: {message}");
        }
    }

    private string GetFullTypeName(TypeDeclarationSyntax typeDecl)
    {
        var ns = GetNamespace(typeDecl);
        return string.IsNullOrEmpty(ns) ? typeDecl.Identifier.Text : $"{ns}.{typeDecl.Identifier.Text}";
    }

    private Class MergePartialTypes(List<TypeDeclarationSyntax> partials)
    {
        var first = partials[0];
        var ns = GetNamespace(first);
        var @class = new Class(first.Identifier.Text)
        {
            IsInterface = first is InterfaceDeclarationSyntax,
            Kind = DetermineTypeKind(first),
            Namespace = ns
        };

        // Merge all partial declarations
        foreach (var partial in partials)
        {
            // Handle Inheritance and Interfaces
            if (partial.BaseList != null)
            {
                ProcessBaseList(partial, @class);
            }

            // Parse Properties
            foreach (var prop in partial.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (!IsVisible(prop.Modifiers)) continue;
                var p = new Property(prop.Identifier.Text, GetVisibility(prop.Modifiers));
                p.Type = prop.Type.ToString();
                ExtractTypeDependencies(prop.Type, p);
                @class.AddProperty(p);
            }

            // Parse Methods
            foreach (var method in partial.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!IsVisible(method.Modifiers)) continue;
                var m = new Method(method.Identifier.Text, GetVisibility(method.Modifiers));
                m.Type = method.ReturnType.ToString();
                ExtractTypeDependencies(method.ReturnType, m);
                @class.AddMethod(m);
            }
        }

        return @class;
    }

    private Class BuildClassFromEnum(EnumDeclarationSyntax enumDecl, string ns)
    {
        var c = new Class(enumDecl.Identifier.Text)
        {
            IsInterface = false,
            Kind = TypeKind.Enum,
            Namespace = ns
        };

        // Extract enum member names
        foreach (var member in enumDecl.Members)
        {
            c.EnumValues.Add(member.Identifier.Text);
        }

        return c;
    }

    private Class BuildClassFromSyntax(TypeDeclarationSyntax typeDecl, string ns)
    {
        var c = new Class(typeDecl.Identifier.Text)
        {
            IsInterface = typeDecl is InterfaceDeclarationSyntax,
            Kind = DetermineTypeKind(typeDecl),
            Namespace = ns
        };

        // Handle Inheritance (Base Class) and Interfaces
        ProcessBaseList(typeDecl, c);


        // Parse Properties
        foreach (var prop in typeDecl.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (!IsVisible(prop.Modifiers)) continue;

            var p = new Property(prop.Identifier.Text, GetVisibility(prop.Modifiers));
            
            // 1. Set the exact text representation (e.g. "List<TimingDose>?")
            p.Type = prop.Type.ToString();

            // 2. Deep dive into the syntax tree to find dependencies (e.g. "TimingDose")
            ExtractTypeDependencies(prop.Type, p);

            c.AddProperty(p);
        }

        // Parse Methods
        foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            if (!IsVisible(method.Modifiers)) continue;

            var m = new Method(method.Identifier.Text, GetVisibility(method.Modifiers));
            m.Type = method.ReturnType.ToString();

            ExtractTypeDependencies(method.ReturnType, m);

            c.AddMethod(m);
        }

        return c;
    }

    private TypeKind DetermineTypeKind(TypeDeclarationSyntax typeDecl)
    {
        return typeDecl switch
        {
            InterfaceDeclarationSyntax => TypeKind.Interface,
            RecordDeclarationSyntax when typeDecl.Kind() == SyntaxKind.RecordStructDeclaration => TypeKind.RecordStruct,
            RecordDeclarationSyntax => TypeKind.Record,
            StructDeclarationSyntax => TypeKind.Struct,
            _ => TypeKind.Class
        };
    }

    private void ProcessBaseList(TypeDeclarationSyntax typeDecl, Class c)
    {
        if (typeDecl.BaseList == null) return;

        foreach (var baseType in typeDecl.BaseList.Types)
        {
            var fallbackName = baseType.Type.ToString();

            if (_semanticModel != null)
            {
                var symbolInfo = _semanticModel.GetSymbolInfo(baseType.Type);
                if (symbolInfo.Symbol is INamedTypeSymbol namedType)
                {
                    var ns = namedType.ContainingNamespace?.ToDisplayString();
                    var isGlobal = string.IsNullOrEmpty(ns) || ns == "<global namespace>";
                    var typeName = isGlobal ? namedType.Name : $"{ns}.{namedType.Name}";

                    if (namedType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface)
                    {
                        if (!ShouldExcludeType(namedType.ContainingNamespace?.ToDisplayString()))
                        {
                            if (!c.ImplementedInterface.Contains(typeName))
                            {
                                c.ImplementedInterface.Add(typeName);
                            }
                        }
                    }
                    else if (c.BaseType == null)
                    {
                        if (!ShouldExcludeType(namedType.ContainingNamespace?.ToDisplayString()))
                        {
                            c.BaseType = typeName;
                        }
                    }
                    continue;
                }
            }

            // Fallback to heuristic if semantic model is not available or symbol not resolved
            if (c.BaseType == null && (!fallbackName.StartsWith("I") || (fallbackName.Length > 1 && char.IsLower(fallbackName[1]))))
            {
                c.BaseType = fallbackName;
            }
            else
            {
                if (!c.ImplementedInterface.Contains(fallbackName))
                {
                    c.ImplementedInterface.Add(fallbackName);
                }
            }
        }
    }

    private bool ShouldExcludeType(string? typeNamespace)
    {
        if (!ExcludeSystemTypes || string.IsNullOrEmpty(typeNamespace)) return false;

        return _systemNamespaces.Any(ns => typeNamespace.StartsWith(ns));
    }

    private string ResolveTypeReference(TypeSyntax typeSyntax, string fallback)
    {
        if (_semanticModel == null)
        {
            return fallback;
        }

        var symbolInfo = _semanticModel.GetSymbolInfo(typeSyntax);
        if (symbolInfo.Symbol is INamedTypeSymbol namedType)
        {
            var ns = namedType.ContainingNamespace?.ToDisplayString();
            var isGlobal = string.IsNullOrEmpty(ns) || ns == "<global namespace>";
            return isGlobal ? namedType.Name : $"{ns}.{namedType.Name}";
        }

        return fallback;
    }

    /// <summary>
    /// Recursively unwraps Nullables and Arrays to find Generics or Class Identifiers.
    /// Fills the GenericType and TypeParams fields on the Member object.
    /// Now handles deeply nested generics like Dictionary<string, List<Foo>>.
    /// </summary>
    private void ExtractTypeDependencies(TypeSyntax typeSyntax, Member member)
    {
        // Case: "int?" or "List<T>?" -> Unwrap the "?"
        if (typeSyntax is NullableTypeSyntax nullable)
        {
            ExtractTypeDependencies(nullable.ElementType, member);
            return;
        }

        // Case: "TimingDose[]" -> Unwrap the "[]"
        if (typeSyntax is ArrayTypeSyntax array)
        {
            ExtractTypeDependencies(array.ElementType, member);
            return;
        }

        // Case: "List<TimingDose>" or "Dictionary<string, List<Inner>>" -> Handle Generic
        if (typeSyntax is GenericNameSyntax generic)
        {
            if (member is Property p) p.GenericType = generic.Identifier.Text;
            if (member is Method m) m.GenericType = generic.Identifier.Text;

            foreach (var arg in generic.TypeArgumentList.Arguments)
            {
                // Recursively extract dependencies from nested generics
                ExtractTypeFromArgument(arg, member);
            }
            return;
        }

        // Case: "Medication" -> Simple Identifier
        if (typeSyntax is IdentifierNameSyntax identifier)
        {
            var typeName = ResolveTypeReference(identifier, identifier.Identifier.Text);
            AddTypeDependency(typeName, member);
        }

        // Case: Qualified names like "System.String"
        if (typeSyntax is QualifiedNameSyntax qualifiedName)
        {
            var fallback = qualifiedName.Right.Identifier.Text;
            var typeName = ResolveTypeReference(qualifiedName, fallback);
            AddTypeDependency(typeName, member);
        }
    }

    private void ExtractTypeFromArgument(TypeSyntax arg, Member member)
    {
        // Handle nested generics: Dictionary<string, List<Inner>>
        if (arg is GenericNameSyntax nestedGeneric)
        {
            // Recursively process the nested generic
            foreach (var nestedArg in nestedGeneric.TypeArgumentList.Arguments)
            {
                ExtractTypeFromArgument(nestedArg, member);
            }
        }
        else if (arg is IdentifierNameSyntax identifier)
        {
            var typeName = ResolveTypeReference(identifier, identifier.Identifier.Text);
            AddTypeDependency(typeName, member);
        }
        else if (arg is QualifiedNameSyntax qualifiedName)
        {
            var fallback = qualifiedName.Right.Identifier.Text;
            var typeName = ResolveTypeReference(qualifiedName, fallback);
            AddTypeDependency(typeName, member);
        }
        else if (arg is NullableTypeSyntax nullable)
        {
            ExtractTypeFromArgument(nullable.ElementType, member);
        }
        else if (arg is ArrayTypeSyntax array)
        {
            ExtractTypeFromArgument(array.ElementType, member);
        }
        else
        {
            // Fallback: use string representation
            var typeName = arg.ToString();
            AddTypeDependency(typeName, member);
        }
    }

    private void AddTypeDependency(string typeName, Member member)
    {
        // Skip primitive types
        var primitives = new[] { "int", "string", "bool", "double", "float", "decimal", "long", "short", "byte", "char", "object", "void" };
        if (primitives.Contains(typeName.ToLower())) return;

        // Skip if it's a system type and we're excluding them
        if (ExcludeSystemTypes && _systemNamespaces.Any(ns => typeName.StartsWith(ns))) return;

        if (member is Property p && !p.TypeParams.Contains(typeName))
        {
            p.TypeParams.Add(typeName);
        }
        if (member is Method m && !m.TypeParams.Contains(typeName))
        {
            m.TypeParams.Add(typeName);
        }
    }

    private string GetNamespace(SyntaxNode node)
    {
        var potentialNamespace = node.Parent;
        while (potentialNamespace != null && 
               !(potentialNamespace is NamespaceDeclarationSyntax) && 
               !(potentialNamespace is FileScopedNamespaceDeclarationSyntax))
        {
            potentialNamespace = potentialNamespace.Parent;
        }

        if (potentialNamespace is BaseNamespaceDeclarationSyntax ns)
        {
            return ns.Name.ToString();
        }
        return string.Empty;
    }

    private bool IsVisible(SyntaxTokenList modifiers)
    {
        var visibility = GetVisibility(modifiers);
        return visibility >= MinimumVisibility;
    }

    private Visibility GetVisibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))) return Visibility.Public;
        if (modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword))) return Visibility.Protected;
        if (modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword))) return Visibility.Internal;
        return Visibility.Private;
    }
}