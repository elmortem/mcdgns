Mermaid Class Diagram Generator with Namespaces (mcdgns)
==================================================

## What is mcdgns

mcdgns (Mermaid Class Diagram Generator with Namespaces) is a dotnet tool for generating mermaid.js class diagrams directly from C# source code (`.cs` files).

Unlike other tools that require compiled assemblies (DLLs), `mcdgns` uses Roslyn to parse your source code files recursively from a directory. This allows you to generate diagrams for projects that may not currently build or to quickly visualize a folder of scripts.

## Credits

This project is a source-code analysis port of [mcdg](https://github.com/fwullschleger/mcdg) (port of [dll2mmd](https://github.com/rtfs/dll2mmd)).
The core graph generation logic and structure were originally written by [rtfs](https://github.com/rtfs), and this project adapts that logic to work with the `Microsoft.CodeAnalysis` (Roslyn) API instead of reflection.

## Installing mcdgns

1. Install .Net SDK 6.0 or later.
2. Install mcdgns as a global dotnet tool.

    ```shell
    $ dotnet tool install --global mcdgns
    You can invoke the tool using the following command: mcdgns
    Tool 'mcdgns' (version '1.0.1') was successfully installed.
    ```

   *Alternatively, if running from source:*
    ```shell
    $ dotnet run --project src/MermaidClassDiagramGenerator/MermaidClassDiagramGenerator.csproj -- [options]
    ```

## Usage

```shell
Description:
  Generate mermaid.js class-diagram from C# source code files.

Usage:
  mcdgns [options]

Options:
  -o, --output <output>           Output file. [default: output.md]
  -ns, --namespace <namespace>    Namespace filter. []
  -p, --path <path>               Path to the folder containing .cs files. [default: current working directory]
  -t, --type-names <type-names>   Specific classes to include. []
  --ignore-dependency             If true, skip dependency arrows.
  -rns, --render-namespaces       If true, wrap classes in mermaid namespace blocks (top-level classes go under 'namespace global').
  --version                       Show version information
  -?, -h, --help                  Show help and usage information
```