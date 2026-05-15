namespace DiagramGenerator.ClassGraph;

public class MermaidGenerator : IDiagramGenerator {
  public bool RenderNamespaces { get; set; } = false;

  private Dictionary<string, string> _idByFqn = new();

  private static string MDFrame =
      @"```mermaid
classDiagram

{0}
{1}
```
";

  private static string ClassFrame =
@"class {0} {{
{1}{2}
}}";

  public string Generate(Graph graph) {
    _idByFqn = BuildIdMap(graph.Classes);

    var allRelation = new List<string>();
    foreach (var relation in graph.Relations) {
      var relationString = GenerateRelation(relation);
      allRelation.Add(relationString);
    }
    var relationSection = string.Join("\r\n", allRelation);

    string classSection;
    if (RenderNamespaces) {
      classSection = GenerateGroupedByNamespace(graph);
    }
    else {
      var allClass = new List<string>();
      foreach (var @class in graph.Classes) {
        var classString = GenerateClass(@class);
        allClass.Add(classString);
      }
      classSection = string.Join("\r\n\r\n", allClass);
    }

    var sections = classSection;
    if (!string.IsNullOrEmpty(relationSection)) {
      sections += "\r\n\r\n" + relationSection;
    }

    return string.Format(MDFrame, sections, string.Empty);
  }

  private string GenerateGroupedByNamespace(Graph graph) {
    var groups = graph.Classes
      .GroupBy(c => string.IsNullOrWhiteSpace(c.Namespace) ? "global" : c.Namespace)
      .OrderBy(g => g.Key, StringComparer.Ordinal);

    var blocks = new List<string>();
    foreach (var group in groups) {
      var classBlocks = new List<string>();
      foreach (var @class in group) {
        var classBlock = GenerateClass(@class);
        classBlocks.Add(IndentBlock(classBlock, "  "));
      }

      var inner = string.Join("\r\n\r\n", classBlocks);
      blocks.Add($"namespace {group.Key} {{\r\n{inner}\r\n}}");
    }

    return string.Join("\r\n\r\n", blocks);
  }

  private static string IndentBlock(string block, string indent) {
    var lines = block.Split("\r\n");
    for (var i = 0; i < lines.Length; i++) {
      lines[i] = indent + lines[i];
    }
    return string.Join("\r\n", lines);
  }

  private string GenerateClass(Class @class) {
    var lines = new List<string>();

    // Add type annotation (<<interface>>, <<record>>, etc.) as first line inside class block
    var typeAnnotation = GetTypeAnnotation(@class.Kind);
    if (!string.IsNullOrEmpty(typeAnnotation)) {
      lines.Add($"  {typeAnnotation}");
    }

    // For enums, add enum values instead of properties/methods
    if (@class.Kind == TypeKind.Enum) {
      foreach (var enumValue in @class.EnumValues) {
        lines.Add($"  {enumValue}");
      }
    }
    else {
      // Add properties
      foreach (var property in @class.Properties) {
        lines.Add(GenerateClassProperty(@class.Name, property, @class.Kind));
      }

      // Add methods
      foreach (var method in @class.Methods) {
        lines.Add(GenerateClassMethod(@class.Name, method, @class.Kind));
      }
    }

    // Join all lines without trailing newline
    var content = string.Join("\r\n", lines);

    return string.Format(ClassFrame, GetClassHeader(@class), content, string.Empty);
  }

  private string GetClassHeader(Class @class) {
    var id = GetClassId(@class);
    return id == @class.Name ? @class.Name : $"{id}[\"{@class.Name}\"]";
  }

  private string GetClassId(Class @class) {
    return _idByFqn.TryGetValue(@class.Fqn, out var id) ? id : @class.Name;
  }

  private static Dictionary<string, string> BuildIdMap(IList<Class> classes) {
    var map = new Dictionary<string, string>();
    foreach (var group in classes.GroupBy(c => c.Name)) {
      if (group.Count() > 1) {
        foreach (var c in group) {
          map[c.Fqn] = SanitizeFqn(c.Fqn);
        }
      }
      else {
        var c = group.Single();
        map[c.Fqn] = c.Name;
      }
    }
    return map;
  }

  private static string SanitizeFqn(string fqn) =>
    new string(fqn.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

  private string GetTypeAnnotation(TypeKind kind) {
    return kind switch {
      TypeKind.Interface => "<<interface>>",
      TypeKind.Record => "<<record>>",
      TypeKind.Struct => "<<struct>>",
      TypeKind.RecordStruct => "<<record struct>>",
      TypeKind.Enum => "<<enumeration>>",
      _ => string.Empty
    };
  }

  private string GenerateClassProperty(string className, Property property, TypeKind typeKind) {
    // Pass the raw Type string (e.g. "List<TimingDose>?")
    var typeString = GetTypeString(property.Type);
    var visibilityNotion = GetVisibilityNotion(property.MemberVisibility);

    // For cleaner Mermaid output, don't prefix with className - just indent
    return $"  {visibilityNotion}{typeString} {property.Name}";
  }

  private string GenerateClassMethod(string className, Method method, TypeKind typeKind) {
    // Pass the raw Type string
    var typeString = GetTypeString(method.Type);
    var visibilityNotion = GetVisibilityNotion(method.MemberVisibility);

    // For cleaner Mermaid output, don't prefix with className - just indent
    return $"  {visibilityNotion}{method.Name}() {typeString}";
  }

  /// <summary>
  /// Converts C# Type string to Mermaid safe string (swapping < > for ~)
  /// </summary>
  private string GetTypeString(string? type) {
    if (string.IsNullOrEmpty(type)) return "void";

    // Mermaid uses ~ for generics, e.g., List~string~
    // It renders "?" correctly as is.
    return type.Replace("<", "~").Replace(">", "~");
  }

  private string GenerateRelation(ClassRelation relation) {
    var toId = GetClassId(relation.To);
    var fromId = GetClassId(relation.From);

    switch (relation.Type) {
      case RelationType.Implementation:
        return $"{toId} <|.. {fromId} : implements";
      case RelationType.Inheritance:
        return $"{toId} <|-- {fromId}";
      case RelationType.Dependency:
        return $"{fromId} ..> {toId}";
      case RelationType.Usage:
        return $"{fromId} ..> {toId} : uses";
      case RelationType.EventSubscription:
        return $"{fromId} ..> {toId} : subscribes";
      default:
        return string.Empty;
    }
  }

  private string GetVisibilityNotion(Visibility visibility) {
    switch (visibility) {
      case Visibility.Private: return "-";
      case Visibility.Protected: return "#";
      case Visibility.Public: return "+";
      case Visibility.Internal: return "~";
      default: return string.Empty;
    }
  }
}