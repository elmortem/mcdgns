using System.CommandLine;
using System.Xml.Linq;
using DiagramGenerator.ClassGraph;

var outputOption = new Option<FileInfo?>(
    aliases: new[] { "--output", "-o" },
    description: "Output file.",
    getDefaultValue: () => new FileInfo("output.md"));

var nsOption = new Option<IList<string>>(
    aliases: new[] { "--namespace", "-ns" },
    description: "Namespace filter.",
    getDefaultValue: () => new List<string>());

var inputPathOption = new Option<string>(
    aliases: new[] { "--path", "-p" },
    description: "Path to the folder containing .cs files. Defaults to the current working directory.",
    getDefaultValue: Directory.GetCurrentDirectory);

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

var excludeTestsOption = new Option<bool>(
    name: "--exclude-tests",
    description: "If true, skip .cs files belonging to test projects (detected via Microsoft.NET.Test.Sdk PackageReference or <IsTestProject>true</IsTestProject> in csproj).");

var rootCommand = new RootCommand("Generate mermaid.js class-diagram from C# source code files.")
{
    Name = "mcdgns"
};
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
rootCommand.AddOption(excludeTestsOption);

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
    var excludeTests = context.ParseResult.GetValueForOption(excludeTestsOption);

    Execute(output!, ns!, inputPath!, tns!, ignoreDep, excludeSys, visLevel!, verbose, excludePatterns!, renderNamespaces, excludeTests);
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
    bool renderNamespaces,
    bool excludeTests)
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

        if (excludeTests)
        {
            var testProjectRoots = FindTestProjectRoots(inputPath, allExclusions);
            if (verbose)
            {
                Console.WriteLine($"Detected {testProjectRoots.Count} test project(s):");
                foreach (var root in testProjectRoots)
                {
                    Console.WriteLine($"  {root}");
                }
            }

            var beforeCount = files.Count;
            files = files.Where(f => !IsUnderAnyRoot(f, testProjectRoots)).ToList();
            if (verbose)
            {
                Console.WriteLine($"Excluded {beforeCount - files.Count} file(s) belonging to test projects");
            }
        }

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

static List<string> FindTestProjectRoots(string inputPath, List<string> exclusions)
{
    var roots = new List<string>();
    var csprojs = Directory.GetFiles(inputPath, "*.csproj", SearchOption.AllDirectories)
        .Where(f => !ShouldExcludeFile(f, exclusions));

    foreach (var csproj in csprojs)
    {
        if (IsTestProject(csproj))
        {
            var dir = Path.GetDirectoryName(csproj);
            if (!string.IsNullOrEmpty(dir))
            {
                roots.Add(Path.GetFullPath(dir));
            }
        }
    }

    return roots;
}

static bool IsTestProject(string csprojPath)
{
    try
    {
        var doc = XDocument.Load(csprojPath);

        // <PackageReference Include="Microsoft.NET.Test.Sdk" ... />
        var hasTestSdk = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Any(e => string.Equals(
                (string?)e.Attribute("Include"),
                "Microsoft.NET.Test.Sdk",
                StringComparison.OrdinalIgnoreCase));
        if (hasTestSdk) return true;

        // <IsTestProject>true</IsTestProject>
        var hasIsTestProject = doc.Descendants()
            .Where(e => e.Name.LocalName == "IsTestProject")
            .Any(e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
        if (hasIsTestProject) return true;
    }
    catch
    {
        // Malformed csproj — treat as non-test, do not crash the whole run
    }

    return false;
}

static bool IsUnderAnyRoot(string filePath, List<string> roots)
{
    if (roots.Count == 0) return false;

    var full = Path.GetFullPath(filePath);
    foreach (var root in roots)
    {
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }
    return false;
}