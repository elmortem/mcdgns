Status: Выполнено

# Дедупликация классов и резолв ссылок через FQN

## Зависимости

Этот ТДД реализуется **после** `260513-1436-TDD-render_namespaces_option.md`. К моменту начала работ предполагается, что:

- В `Class` уже есть поле `public string Namespace { get; set; } = string.Empty;`.
- `SourceGraphBuilder` уже заполняет `Namespace` в `BuildClassFromSyntax`, `BuildClassFromEnum`, `MergePartialTypes`.

## Проблема

`Graph.AddClass` дедуплицирует классы по голому короткому имени `Class.Name`. `Graph.RebuildRelation` ищет базовые типы, интерфейсы и зависимости тоже по короткому имени. `Graph.AddRelation` дедуплицирует связи по парам коротких имён.

Следствия:

- Если в кодовой базе есть два класса с одинаковым коротким именем в разных неймспейсах (`A.Foo` и `B.Foo`), второй молча отбрасывается из графа.
- Если у `C` базовый класс `A.Foo`, а в граф попал только `B.Foo` (или они сосуществуют по короткому имени) — связь `Inheritance` будет проложена к не тому классу.
- Зависимости (`Dependency`-стрелки) могут указывать на одноимённый, но не тот класс.

Корень — в том, что в графе **нет** концепции полного имени, и в `SourceGraphBuilder` ссылки на базы/интерфейсы/типы свойств и методов хранятся как **текст из исходника**, хотя рядом есть резолвленный `INamedTypeSymbol` из Roslyn semantic model.

## Решения

- Ввести в `Class` производное свойство `Fqn` (Namespace + "." + Name; если Namespace пуст — просто Name).
- Дедуп классов в `Graph.AddClass` — по `Fqn`.
- Дедуп связей в `Graph.AddRelation` — по `(From.Fqn, To.Fqn, Type)`.
- Lookup базы / интерфейсов / зависимостей в `Graph.RebuildRelation` — по `Fqn`.
- В `SourceGraphBuilder` ссылки (`Class.BaseType`, `Class.ImplementedInterface`, `Property.TypeParams`, `Method.TypeParams`) хранят **FQN**, если symbol резолвится через `_semanticModel`. Если не резолвится (типичный случай — тип из NuGet-зависимости, на ассембли которой компиляция не сослалась) — fallback на текстовое представление как сейчас. Поведение для unresolved-ссылок остаётся идентичным текущему («слипание» с одноимёнными user-классами допускается).
- Рендер в `MermaidGenerator` остаётся как сейчас — короткое имя `Class.Name`. Лимит mermaid на уникальность видимых имён классов между namespace-блоками — известное ограничение, в этот ТДД не лезем.

## Изменения

### `src/ClassGraph/ClassGraph.cs`

В классе `Class` добавить вычисляемое свойство:

```csharp
public string Fqn => string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";
```

Размещение — сразу после `Namespace`. Делается именно вычисляемым (не хранимым) — инвариант между `Namespace`, `Name` и `Fqn` поддерживается автоматически, никакой синхронизации в местах создания не нужно.

В `Graph.AddClass` заменить:

```csharp
if (!Classes.Any(o => o.Name == @class.Name))
```

на:

```csharp
if (!Classes.Any(o => o.Fqn == @class.Fqn))
```

В `Graph.RebuildRelation` заменить три lookup'а:

```csharp
var baseClass = Classes.Where(o => o.Name == @class.BaseType).FirstOrDefault();
```

на:

```csharp
var baseClass = Classes.FirstOrDefault(o => o.Fqn == @class.BaseType);
```

```csharp
var intfClass = Classes.Where(o => o.Name == intf).FirstOrDefault();
```

на:

```csharp
var intfClass = Classes.FirstOrDefault(o => o.Fqn == intf);
```

```csharp
var depdClass = Classes.Where(o => o.Name == depdType).FirstOrDefault();
```

на:

```csharp
var depdClass = Classes.FirstOrDefault(o => o.Fqn == depdType);
```

Проверка `baseClass.ImplementedInterface.Contains(intf)` остаётся без изменений — после правок `ImplementedInterface` и `intf` оба содержат FQN, сравнение корректно.

В `Graph.AddRelation` заменить:

```csharp
if (!Relations.Any(o => o.From.Name == relation.From.Name && o.To.Name == relation.To.Name && o.Type == relation.Type))
```

на:

```csharp
if (!Relations.Any(o => o.From.Fqn == relation.From.Fqn && o.To.Fqn == relation.To.Fqn && o.Type == relation.Type))
```

Остальное в файле — без изменений. `Class.AddProperty` / `Class.AddMethod` дедупят по имени члена внутри одного класса; это правильное поведение, не трогаем.

### `src/ClassGraph/SourceGraphBuilder.cs`

Добавить приватный хелпер:

```csharp
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
```

Размещение — рядом с `ShouldExcludeType` / `GetNamespace`. Семантика: если symbol резолвится и это `INamedTypeSymbol` — вернуть FQN (без generic-параметров: `namedType.Name` возвращает имя без `<T>`). Если не резолвится — fallback.

В `ProcessBaseList` изменить инициализацию `typeName` так, чтобы она проходила через резолв:

```csharp
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
```

Ключевое отличие от текущего кода:

- В семантической ветке `typeName` вычисляется как FQN (или fallback на `namedType.Name`, если namespace global).
- В эвристической ветке используется текст из исходника (`fallbackName`) — это и есть «Q4 = (a)»: unresolved-ссылки остаются как есть.

В `ExtractTypeDependencies`: в ветках `IdentifierNameSyntax` и `QualifiedNameSyntax` подменить плоское извлечение текста на вызов `ResolveTypeReference`:

```csharp
if (typeSyntax is IdentifierNameSyntax identifier)
{
	var typeName = ResolveTypeReference(identifier, identifier.Identifier.Text);
	AddTypeDependency(typeName, member);
}

if (typeSyntax is QualifiedNameSyntax qualifiedName)
{
	var fallback = qualifiedName.Right.Identifier.Text;
	var typeName = ResolveTypeReference(qualifiedName, fallback);
	AddTypeDependency(typeName, member);
}
```

В `ExtractTypeFromArgument`: симметричные изменения в тех же двух ветках:

```csharp
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
```

В `else`-ветке `ExtractTypeFromArgument` (fallback на `arg.ToString()`) — оставить как есть.

`AddTypeDependency` менять **не нужно**: его логика отсечения примитивов и system types работает независимо от того, короткое имя или FQN на входе. Более того, для FQN проверка `_systemNamespaces.Any(ns => typeName.StartsWith(ns))` теперь сработает **точнее** — это полезный побочный эффект.

### `src/ClassGraph/MermaidGenerator.cs`

Без изменений. Имена классов в выводе — короткие (`Class.Name`), как и сейчас. Реляции в mermaid резолвятся по имени — после правок у одноимённых классов в разных неймспейсах останется конфликт при рендере, но это лимит mermaid и вне scope этого ТДД.

### `src/MermaidClassDiagramGenerator/Program.cs`

Без изменений.

### `README.md`

Без изменений.

## Порядок выполнения

- Внести изменения в `ClassGraph.cs` (свойство `Fqn`, четыре места замены `Name` на `Fqn`).
- Внести изменения в `SourceGraphBuilder.cs` (новый хелпер `ResolveTypeReference`, переписанный `ProcessBaseList`, две правки в `ExtractTypeDependencies`, две — в `ExtractTypeFromArgument`).
- Собрать решение:
	```
	dotnet build src/MermaidClassDiagramGenerator/MermaidClassDiagramGenerator.csproj
	```
- Ручная проверка на проекте с одноимёнными классами в разных неймспейсах (можно временно создать в тестовой папке два файла: `A/Foo.cs` с `namespace A; public class Foo { public B.Foo Other { get; set; } }` и `B/Foo.cs` с `namespace B; public class Foo { }`):
	```
	dotnet run --project src/MermaidClassDiagramGenerator/MermaidClassDiagramGenerator.csproj -- --path "<test-folder>" --output check.md --render-namespaces
	```
	- В `check.md` ожидаются два `class Foo` — по одному в `namespace A` и `namespace B`.
	- Стрелка зависимости от `A.Foo` к `B.Foo` присутствует.
- Регрессия: на проекте `mermaid3d` (где сейчас одноимённых классов между неймспейсами нет) и без `--render-namespaces` вывод побайтно идентичен предыдущему.

---

После завершения правок:

- Поменяй статус в начале документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить документацию форка в связи с этим изменением.
