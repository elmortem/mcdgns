Status: Выполнено

# Опция `--render-namespaces` для группировки классов по неймспейсам

## Проблема

Текущий MCDG (`fwullschleger/mcdg`, форк → `mcdgns`) генерирует mermaid `classDiagram` плоским списком — все классы выводятся одним блоком, информация о неймспейсе исходного типа теряется. Mermaid поддерживает синтаксис `namespace X { ... }`, но генератор его не использует.

Цель — добавить опциональный режим, в котором классы группируются в `namespace`-блоки по своему неймспейсу. Существующее поведение (плоский вывод) остаётся дефолтом для обратной совместимости.

## Решения

- CLI-опция: `--render-namespaces` / `-rns`, тип `bool`, дефолт `false`.
- Классы без неймспейса (top-level в C#) при включённой опции попадают в группу с ключом `global` и оборачиваются в `namespace global { ... }`. Это позволяет в потребителях (CodeViz и др.) скрывать/подсвечивать «глобальные» классы по имени неймспейса так же, как обычные.
- Дедупликацию классов в `Graph.AddClass` по голому `Name` **не трогаем** в этом ТДД (см. постскриптум).
- Реляции (`<|--`, `<|..`, `<--`) выводятся плоско после namespace-блоков, как и сейчас. Mermaid резолвит имена классов глобально, перенос объявления внутрь `namespace`-блока на синтаксис реляций не влияет.
- Сортировка namespace-блоков — `OrderBy(key, StringComparer.Ordinal)` для детерминированного вывода. Внутри namespace-блока порядок классов — как в `graph.Classes` (сохраняем существующий порядок обхода).
- При `--render-namespaces=false` `MermaidGenerator.Generate` ведёт себя побайтно идентично текущему поведению.

## Изменения

### `src/ClassGraph/ClassGraph.cs`

В классе `Class` добавить публичное свойство:

```csharp
public string Namespace { get; set; } = string.Empty;
```

Размещение — сразу после `public string Name { get; set; }`. Конструктор `Class(string name)` не трогаем — неймспейс выставляется через property initializer на месте создания.

Остальное в файле — без изменений. `Graph.AddClass` дедуплицирует по `Name` как раньше.

### `src/ClassGraph/SourceGraphBuilder.cs`

Изменить сигнатуры приватных методов, чтобы они получали неймспейс:

```csharp
private Class BuildClassFromSyntax(TypeDeclarationSyntax typeDecl, string ns)
private Class BuildClassFromEnum(EnumDeclarationSyntax enumDecl, string ns)
```

В обоих методах при создании `Class` добавить `Namespace = ns`:

```csharp
var c = new Class(typeDecl.Identifier.Text)
{
	IsInterface = typeDecl is InterfaceDeclarationSyntax,
	Kind = DetermineTypeKind(typeDecl),
	Namespace = ns
};
```

Аналогично для `BuildClassFromEnum`:

```csharp
var c = new Class(enumDecl.Identifier.Text)
{
	IsInterface = false,
	Kind = TypeKind.Enum,
	Namespace = ns
};
```

`MergePartialTypes` оставляет существующую сигнатуру, но в начале метода извлекает неймспейс из первого partial-объявления (по семантике C# все partials одного типа обязаны быть в одном неймспейсе):

```csharp
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
	...
}
```

В методе `Build`, в местах вызова `BuildClassFromSyntax` и `BuildClassFromEnum`, передать уже вычисленный `ns`. Сейчас в коде:

```csharp
var ns = GetNamespace(typeDecl);
if (nsList.Any() && !nsList.Contains(ns))
	continue;
...
var @class = BuildClassFromSyntax(typeDecl);
```

Заменить вызов на `BuildClassFromSyntax(typeDecl, ns)`. Аналогично для ветки `enumDecl`:

```csharp
var ns = GetNamespace(enumDecl);
if (nsList.Any() && !nsList.Contains(ns))
	continue;

var @class = BuildClassFromEnum(enumDecl, ns);
```

Никаких других изменений в `SourceGraphBuilder` не нужно.

### `src/ClassGraph/MermaidGenerator.cs`

Добавить публичное свойство:

```csharp
public bool RenderNamespaces { get; set; } = false;
```

Размещение — рядом с private static-полями `MDFrame` / `ClassFrame`, но как обычное instance-свойство в начале класса.

Метод `Generate(Graph graph)` дополнить веткой для группировки. Текущая логика остаётся как fallback при `RenderNamespaces == false`. Полная форма метода:

```csharp
public string Generate(Graph graph)
{
	var allRelation = new List<string>();
	foreach (var relation in graph.Relations)
	{
		var relationString = GenerateRelation(relation);
		allRelation.Add(relationString);
	}
	var relationSection = string.Join("\r\n", allRelation);

	string classSection;
	if (RenderNamespaces)
	{
		classSection = GenerateGroupedByNamespace(graph);
	}
	else
	{
		var allClass = new List<string>();
		foreach (var @class in graph.Classes)
		{
			var classString = GenerateClass(@class);
			allClass.Add(classString);
		}
		classSection = string.Join("\r\n\r\n", allClass);
	}

	var sections = classSection;
	if (!string.IsNullOrEmpty(relationSection))
	{
		sections += "\r\n\r\n" + relationSection;
	}

	return string.Format(MDFrame, sections, string.Empty);
}
```

Новый приватный метод `GenerateGroupedByNamespace`:

```csharp
private string GenerateGroupedByNamespace(Graph graph)
{
	var groups = graph.Classes
		.GroupBy(c => string.IsNullOrWhiteSpace(c.Namespace) ? "global" : c.Namespace)
		.OrderBy(g => g.Key, StringComparer.Ordinal);

	var blocks = new List<string>();
	foreach (var group in groups)
	{
		var classBlocks = new List<string>();
		foreach (var @class in group)
		{
			var classBlock = GenerateClass(@class);
			classBlocks.Add(IndentBlock(classBlock, "  "));
		}

		var inner = string.Join("\r\n\r\n", classBlocks);
		blocks.Add($"namespace {group.Key} {{\r\n{inner}\r\n}}");
	}

	return string.Join("\r\n\r\n", blocks);
}

private static string IndentBlock(string block, string indent)
{
	var lines = block.Split("\r\n");
	for (var i = 0; i < lines.Length; i++)
	{
		lines[i] = indent + lines[i];
	}
	return string.Join("\r\n", lines);
}
```

`GenerateClass`, `GenerateClassProperty`, `GenerateClassMethod`, `GenerateRelation`, `GetTypeAnnotation`, `GetTypeString`, `GetRelationNotion`, `GetVisibilityNotion`, поля `MDFrame` / `ClassFrame` — без изменений.

### `src/MermaidClassDiagramGenerator/Program.cs`

Добавить опцию (рядом с остальными `Option<>` объявлениями):

```csharp
var renderNamespacesOption = new Option<bool>(
	aliases: new[] { "--render-namespaces", "-rns" },
	description: "If true, wrap classes in mermaid namespace blocks by their C# namespace. Top-level classes go under 'namespace global'.");
```

Зарегистрировать:

```csharp
rootCommand.AddOption(renderNamespacesOption);
```

В лямбде `SetHandler` достать значение:

```csharp
var renderNamespaces = context.ParseResult.GetValueForOption(renderNamespacesOption);
```

Прокинуть в `Execute`. Сигнатура `Execute` расширяется одним параметром:

```csharp
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
```

В `Execute`, при создании генератора:

```csharp
var generator = new MermaidGenerator
{
	RenderNamespaces = renderNamespaces
};
var text = generator.Generate(graph);
```

(Сейчас `new MermaidGenerator()` — без initializer; добавляем initializer.)

### `README.md`

В блок `Options` в секции Usage добавить строку:

```
  -rns, --render-namespaces       If true, wrap classes in mermaid namespace blocks (top-level classes go under 'namespace global').
```

## Порядок выполнения

- Внести изменения в `ClassGraph.cs` (одно свойство `Namespace`).
- Внести изменения в `SourceGraphBuilder.cs` (две сигнатуры методов + инициализация `Namespace` в трёх местах: `BuildClassFromSyntax`, `BuildClassFromEnum`, `MergePartialTypes`; два места вызова в `Build`).
- Внести изменения в `MermaidGenerator.cs` (свойство `RenderNamespaces`, развилка в `Generate`, новый `GenerateGroupedByNamespace` + `IndentBlock`).
- Внести изменения в `Program.cs` (новая опция, регистрация, проброс в `Execute`, использование при создании генератора).
- Обновить `README.md`.
- Собрать решение командой:
	```
	dotnet build src/MermaidClassDiagramGenerator/MermaidClassDiagramGenerator.csproj
	```
- Прогнать локальный запуск на произвольной C#-папке (например, на самом `mermaid3d`):
	```
	dotnet run --project src/MermaidClassDiagramGenerator/MermaidClassDiagramGenerator.csproj -- --path "D:\Hobby\Repositories\mermaid3d" --output check.md
	dotnet run --project src/MermaidClassDiagramGenerator/MermaidClassDiagramGenerator.csproj -- --path "D:\Hobby\Repositories\mermaid3d" --output check-ns.md --render-namespaces
	```
- Ручная проверка:
	- `check.md` побайтно совпадает с тем, что генерировал бинарь до правок (плоский список классов).
	- `check-ns.md` содержит блоки `namespace CodeViz.Core.Scene { ... }`, `namespace CodeViz.Core.Layout { ... }`, и т.п. Top-level классы (если такие есть) — внутри `namespace global { ... }`.
	- Реляции (`<|--`, `<|..`, `<--`) — после блоков, имена корректно резолвятся (mermaid должен рендериться без ошибок в mermaid live editor).

## Постскриптум: известные проблемы вне scope этого ТДД

- `Graph.AddClass` дедуплицирует классы по голому `Name`. Если в проекте есть два класса с одинаковым именем в разных неймспейсах — в граф попадёт только первый, второй молча отбросится. Аналогично `RebuildRelation` матчит `BaseType` / интерфейсы / зависимости только по короткому имени, что даёт ложные срабатывания между одноимёнными классами. После добавления поля `Namespace` в `Class` это станет исправимым (ключ дедупликации `(Namespace, Name)`; матчинг базовых типов через семантическую модель Roslyn, а не текстовую). Это отдельный ТДД.

---

После завершения правок:

- Поменяй статус в начале документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить документацию форка (README, возможно версия пакета и метаданные `.csproj`) в связи с этим изменением.
