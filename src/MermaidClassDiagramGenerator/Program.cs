using System.CommandLine;
using DiagramGenerator.ClassGraph;

var outputOption = new Option<FileInfo?>(
    aliases: new[] { "--output", "-o" },
    description: "Output file.",
    getDefaultValue: () => new FileInfo("output.md"));

var nsOption = new Option<IList<string>>(
    aliases: new[] { "--namespace", "-ns" },
    description: "Namespace filter.",
    getDefaultValue: () => new List<string>());

// CHANGED: Input is now a folder path
var inputPathOption = new Option<string>(
        aliases: new[] { "--path", "-p" },
        description: "Path to the folder containing .cs files.")
    { IsRequired = true };

var tnOption = new Option<IList<string>>(
    aliases: new[] { "--type-names", "-t" },
    description: "Specific classes to include.",
    getDefaultValue: () => new List<string>());

var ignoreDependencyOption = new Option<bool>(
    name: "--ignore-dependency",
    description: "If true, skip dependency arrows.");

var excludeSystemTypesOption = new Option<bool>(
    name: "--exclude-system-types",
    description: "If true, exclude system types (System.*, Microsoft.*) from dependencies.");

var visibilityOption = new Option<string>(
    aliases: new[] { "--visibility", "-v" },
    description: "Minimum visibility level to include (Public, Internal, Protected, Private).",
    getDefaultValue: () => "Public");

var verboseOption = new Option<bool>(
    name: "--verbose",
    description: "Enable verbose output with detailed logging.");

var excludePatternsOption = new Option<IList<string>>(
    name: "--exclude-patterns",
    description: "Additional patterns to exclude from file search (e.g., 'Migrations', 'Generated').",
    getDefaultValue: () => new List<string>());

var renderNamespacesOption = new Option<bool>(
    aliases: new[] { "--render-namespaces", "-rns" },
    description: "If true, wrap classes in mermaid namespace blocks by their C# namespace. Top-level classes go under 'namespace global'.");

var rootCommand = new RootCommand("Generate mermaid.js class-diagram from C# source code files.");
rootCommand.AddOption(outputOption);
rootCommand.AddOption(nsOption);
rootCommand.AddOption(inputPathOption);
rootCommand.AddOption(tnOption);
rootCommand.AddOption(ignoreDependencyOption);
rootCommand.AddOption(excludeSystemTypesOption);
rootCommand.AddOption(visibilityOption);
rootCommand.AddOption(verboseOption);
rootCommand.AddOption(excludePatternsOption);
rootCommand.AddOption(renderNamespacesOption);

rootCommand.SetHandler((context) =>
{
    var output = context.ParseResult.GetValueForOption(outputOption);
    var ns = context.ParseResult.GetValueForOption(nsOption);
    var inputPath = context.ParseResult.GetValueForOption(inputPathOption);
    var tns = context.ParseResult.GetValueForOption(tnOption);
    var ignoreDep = context.ParseResult.GetValueForOption(ignoreDependencyOption);
    var excludeSys = context.ParseResult.GetValueForOption(excludeSystemTypesOption);
    var visLevel = context.ParseResult.GetValueForOption(visibilityOption);
    var verbose = context.ParseResult.GetValueForOption(verboseOption);
    var excludePatterns = context.ParseResult.GetValueForOption(excludePatternsOption);
    var renderNamespaces = context.ParseResult.GetValueForOption(renderNamespacesOption);

    Execute(output!, ns!, inputPath!, tns!, ignoreDep, excludeSys, visLevel!, verbose, excludePatterns!, renderNamespaces);
});

return await rootCommand.InvokeAsync(args);

static void Execute(FileInfo outputFile,
    IList<string> nsList,
    string inputPath,
    IList<string> tnList,
    bool ignoreDependency,
    bool excludeSystemTypes,
    string visibilityLevel,
    bool verbose,
    IList<string> excludePatterns,
    bool renderNamespaces)
{
    try
    {
        // Validate input path
        if (!Directory.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: Directory not found: {inputPath}");
            Environment.Exit(1);
        }

        // Parse visibility level
        if (!Enum.TryParse<Visibility>(visibilityLevel, true, out var minVisibility))
        {
            Console.Error.WriteLine($"Error: Invalid visibility level '{visibilityLevel}'. Valid values: Public, Internal, Protected, Private");
            Environment.Exit(1);
        }

        if (verbose)
        {
            Console.WriteLine($"Scanning directory: {inputPath}");
            Console.WriteLine($"Minimum visibility: {minVisibility}");
            Console.WriteLine($"Exclude system types: {excludeSystemTypes}");
        }

        // 1. Gather all .cs files recursively with improved exclusion
        var defaultExclusions = new[] { "obj", "bin", ".vs", "Debug", "Release" };
        var allExclusions = defaultExclusions.Concat(excludePatterns).ToList();

        var files = Directory.GetFiles(inputPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !ShouldExcludeFile(f, allExclusions))
            .ToList();

        if (verbose)
        {
            Console.WriteLine($"Found {files.Count} C# files to process");
        }

        if (!files.Any())
        {
            Console.WriteLine("No C# files found to process.");
            return;
        }

        // 2. Use the SourceGraphBuilder with configuration
        var builder = new SourceGraphBuilder
        {
            ExcludeSystemTypes = excludeSystemTypes,
            MinimumVisibility = minVisibility,
            Verbose = verbose
        };

        var graph = builder.Build(files, nsList, tnList, ignoreDependency);

        if (!graph.Classes.Any())
        {
            Console.WriteLine("No classes found matching the specified criteria.");
            return;
        }

        // 3. Generate Mermaid diagram
        var generator = new MermaidGenerator
        {
            RenderNamespaces = renderNamespaces
        };
        var text = generator.Generate(graph);

        // 4. Write output
        File.WriteAllText(outputFile.FullName, text);
        Console.WriteLine($"Diagram generated at: {outputFile.FullName}");
        Console.WriteLine($"Classes: {graph.Classes.Count}, Relations: {graph.Relations.Count}");
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.Error.WriteLine($"Error: Access denied to path. {ex.Message}");
        Environment.Exit(1);
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"Error: I/O error occurred. {ex.Message}");
        Environment.Exit(1);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: An unexpected error occurred. {ex.Message}");
        if (verbose)
        {
            Console.Error.WriteLine(ex.StackTrace);
        }
        Environment.Exit(1);
    }
}

static bool ShouldExcludeFile(string filePath, List<string> exclusions)
{
    var pathParts = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return pathParts.Any(part => exclusions.Any(excl => part.Equals(excl, StringComparison.OrdinalIgnoreCase)));
}