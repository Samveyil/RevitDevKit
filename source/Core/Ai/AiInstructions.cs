using BIMPlugins.ExtStorage;
using BIMPlugins.ExtStorage.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RevitDevKit.Core.Compiler;
using RevitDevKit.Core.IntelliSense;
using RevitDevKit.Core.Output;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace RevitDevKit.Core.Ai
{
    // Instructions for an AI chat (ChatGPT, Claude, Copilot...) on how to write scripts for Script Editor: pasted at the
    // start of a chat, they keep the AI from writing an IExternalCommand and make it use what Script Editor offers.
    // Built from the add-in itself, so they follow it: the members of ScriptBase and of the loaded BIMPlugins.ExtStorage
    // with their signatures and docs, the usings scripts get, the Revit version and whether Автотранзакция is on.
    public static class AiInstructions
    {
        private const string ExtStorageRepository = "https://github.com/Samveyil/BIMPlugins.ExtStorage";
        private const string ExtStoragePackage = "https://www.nuget.org/packages/BIMPlugins.ExtStorage";

        public static string Build(bool autoTransaction)
        {
            var text = new StringBuilder();
            var revit = RevitVersion();
            var compilation = Compilation();

            text.AppendLine("# Script Editor (RevitDevKit): как писать скрипты");
            text.AppendLine();
            text.AppendLine("Ты помогаешь писать C#-скрипты для Script Editor — редактора кода внутри Revit" + (revit == null ? "" : " " + revit) +
                            " (надстройка RevitDevKit). Скрипт выполняется сразу по F5: без сборки DLL, без .addin и без перезапуска Revit. " +
                            "Отвечай на языке пользователя.");
            text.AppendLine();

            text.AppendLine("## Формат кода");
            text.AppendLine("- Пиши фрагмент: просто строки кода, как тело метода. **Не пиши** `IExternalCommand`, класс, метод `Execute` или `Main` и `namespace` — " +
                            "Script Editor сам обернёт фрагмент в метод `void Main()` класса-наследника `ScriptBase` и выполнит его.");
            text.AppendLine("- Во фрагменте можно `return;` (без значения), локальные функции, лямбды, анонимные типы и кортежи. " +
                            "Объявлять во фрагменте классы, `struct`, `enum` и `record` нельзя.");
            text.AppendLine("- `using` в начале фрагмента можно, но основные уже подключены (список ниже).");
            text.AppendLine("- Внешняя DLL подключается строкой `#r \"C:\\путь\\к\\библиотеке.dll\"` в начале фрагмента.");
            text.AppendLine("- Отдай один блок ```csharp, который вставляется в редактор целиком, и коротко поясни, что он делает. " +
                            "Исправляя скрипт, возвращай его целиком, а не кусок.");
            text.AppendLine();

            text.AppendLine("## Транзакции");
            text.AppendLine(autoTransaction
                ? "- Галочка «Автотранзакция» включена: весь скрипт уже выполняется в транзакции. Не создавай `Transaction` и не вызывай `Tx`."
                : "- Галочка «Автотранзакция» выключена. Читать модель можно без транзакции. Любое изменение модели (`SetValue`, `Doc.Delete`, " +
                  "`Create`, перемещение) оборачивай в `Tx(\"Название\", () => { ... })` или `var x = Tx(\"Название\", () => ...)`; " +
                  "`Transaction` вручную не создавай. Без транзакции Revit бросит «Modification of the document is forbidden».");
            text.AppendLine();

            text.AppendLine("## Вывод и отладка");
            text.AppendLine("- Результат пиши в консоль Script Editor: `Print`, `Table`, `Ok`, `Warn`, `Fail`, `Dump`, `Compare`. Не используй `TaskDialog` и `MessageBox`.");
            text.AppendLine("- Текст собирай интерполяцией: `Ok($\"Выделено стен: {ids.Count}\")`.");
            text.AppendLine("- Коллекцию выводи через `Table(items, x => x.Id, x => x.Name)` или `Print(items)`, а не циклом с `Print` на каждый элемент.");
            text.AppendLine("- `Console.WriteLine` и `Debug.WriteLine` тоже попадают в консоль.");
            text.AppendLine("- Геометрию показывай через `Show(...)`: она рисуется поверх модели на видах, в модели ничего не создаётся.");
            text.AppendLine("- Исключение не нужно ловить ради вывода: Script Editor сам покажет его в консоли со строкой скрипта.");
            text.AppendLine();

            text.AppendLine("## Окружение");
            text.AppendLine("- Revit" + (revit == null ? "" : " " + revit) + ", .NET Framework 4.8, C# до версии 9.");
            text.AppendLine("- Код выполняется в потоке Revit API: без `async`/`await` и без `Task.Run` для работы с моделью.");
            text.AppendLine("- Подключённые `using`: " + string.Join(", ", Usings().Select(u => "`" + u + "`")) + ".");
            text.AppendLine();

            text.AppendLine("## Частые ошибки — проверь код перед ответом");
            text.AppendLine("- Документ, вид и выделение уже есть: `Doc`, `UIDoc`, `ActiveView`, `Selection`, `S0`. " +
                            "Не получай их из `commandData` или `uiapp`; короткое имя — `var doc = Doc;`.");
            text.AppendLine("- Элементы бери через `All<Wall>()`, `All<FamilyInstance>(BuiltInCategory.OST_Doors)`, `Instances(...)`, `Types(...)`, " +
                            "элемент по id — `id.ToElement<Wall>()`. `FilteredElementCollector` нужен только для особых фильтров.");
            text.AppendLine("- Проверяй, что нужное есть: `S0` и `Selection` пусты, если ничего не выделено, — тогда `Warn(...)` и `return;`.");
            text.AppendLine("- Длины в Revit API — во внутренних единицах (футы), углы — в радианах. Переводи через `ToMillimeters()` / `FromMillimeters()`, " +
                            "`ToDegrees()` / `FromDegrees()` из BIMPlugins.ExtStorage, а не делением на 304.8.");
            text.AppendLine("- Параметры: `element.ToParameter(...)` (см. список ниже), значение — `GetValue()`, запись — `SetValue(value)`. " +
                            "Имена параметров зависят от языка Revit, поэтому встроенные бери по `BuiltInParameter`: " +
                            "`element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)`.");
            text.AppendLine("- Значение `ElementId` — `id.GetValue()`: `int` до Revit 2024 и `long` с 2024. `ElementId.IntegerValue` с 2024 устарел.");
            text.AppendLine("- Не меняй модель внутри перебора `FilteredElementCollector`: сначала `.ToList()`, потом изменения.");
            text.AppendLine("- C# 9 и .NET Framework 4.8: нет `record`, `init`, `required`, `global using`, file-scoped `namespace`, сырых строк `\"\"\"`, " +
                            "выражений коллекций `[1, 2]`, индексов и диапазонов `^1` и `..`. Нет методов .NET Core: `DistinctBy`, `MaxBy`, `MinBy`, `Chunk`, " +
                            "`TakeLast`, `string.Join(',', ...)`, `Split(\", \")` со строкой (пиши `Split(new[] { \", \" }, StringSplitOptions.None)`), " +
                            "`Contains(char)`.");
            text.AppendLine("- Не выдумывай методы: используй Revit API" + (revit == null ? "" : " " + revit) +
                            ", члены `ScriptBase` и BIMPlugins.ExtStorage из списков ниже. Не уверен, что метод есть, — так и скажи.");
            text.AppendLine();

            text.AppendLine("## Члены ScriptBase — доступны во фрагменте без префикса");
            foreach (var line in ScriptBaseMembers(compilation))
                text.AppendLine(line);
            text.AppendLine();

            text.AppendLine("## BIMPlugins.ExtStorage" + ExtStorageVersion() + " — библиотека расширений Revit API, уже подключена");
            text.AppendLine("Методы-расширения и помощники для элементов, параметров, единиц, геометрии, видов и файлов. Все её пространства имён " +
                            "уже подключены, расширения вызываются на объекте: `wall.ToPoint(LocationType.Start)`, `width.ToMillimeters()`. " +
                            "Прежде чем писать свой помощник, найди готовый здесь. Ниже — всё, что есть в загруженной версии, " +
                            "с описаниями из её документации.");
            text.AppendLine("Исходники: " + ExtStorageRepository + ", пакет NuGet: " + ExtStoragePackage + ".");
            text.AppendLine();
            foreach (var line in ExtStorageApi(compilation))
                text.AppendLine(line);
            text.AppendLine();

            text.AppendLine("## Пример");
            text.AppendLine("```csharp");
            text.AppendLine("var walls = All<Wall>(ActiveView.Id);");
            text.AppendLine("if (walls.Count == 0)");
            text.AppendLine("{");
            text.AppendLine("    Warn(\"На виде нет стен.\");");
            text.AppendLine("    return;");
            text.AppendLine("}");
            text.AppendLine();
            text.AppendLine("Table(walls, w => w.Id, w => w.Name, w => w.Width.ToMillimeters());");
            text.AppendLine("Show(walls, Red, \"стены вида\");");
            if (!autoTransaction)
                text.AppendLine("Tx(\"Комментарий\", () => walls.First().get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).SetValue(\"проверено\"));");
            text.AppendLine("Ok($\"Стен на виде: {walls.Count}\");");
            text.AppendLine("```");

            return text.ToString();
        }

        // The instructions, then the script of the tab and what went wrong in its last run, to ask the AI to fix or change it.
        public static string BuildWithScript(bool autoTransaction, string title, string code, IEnumerable<string> problems)
        {
            var text = new StringBuilder(Build(autoTransaction));
            text.AppendLine();
            text.AppendLine("---");
            text.AppendLine();
            text.AppendLine("## Мой текущий скрипт" + (title.IsNullOrWhiteSpace() ? "" : " «" + title + "»"));
            text.AppendLine("```csharp");
            text.AppendLine((code ?? string.Empty).TrimEnd());
            text.AppendLine("```");

            var list = (problems ?? Enumerable.Empty<string>()).ToList();
            text.AppendLine();
            if (list.Count == 0)
            {
                text.AppendLine("Последний запуск прошёл без ошибок (или скрипт ещё не запускался).");
            }
            else
            {
                text.AppendLine("## Ошибки последнего запуска (номера строк — строки скрипта)");
                foreach (var problem in list)
                    text.AppendLine("- " + problem);
                text.AppendLine();
                text.AppendLine("Найди причину каждой ошибки, исправь и верни скрипт целиком.");
            }

            return text.ToString();
        }

        private static string RevitVersion()
        {
            try
            {
                return ReadRevitVersion();
            }
            catch
            {
                return null; // no Revit, as in tests
            }
        }

        // Apart, so that without Revit API the failure to compile it is caught above, not thrown at the caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadRevitVersion() => RevitAPI.Application.VersionNumber;

        // " 0.7.0": the version of the loaded package, without the Revit year and the commit hash.
        private static string ExtStorageVersion()
        {
            try
            {
                var assembly = typeof(RevitAPI).Assembly;
                var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                              ?? assembly.GetName().Version.ToString();
                version = version.Split('+')[0];

                // 2024.7.0 -> 0.7.0: the packages are built per Revit version with the year as the major number.
                var parts = version.Split('.');
                if (parts.Length >= 3 && parts[0].Length == 4)
                    version = "0." + string.Join(".", parts.Skip(1));

                return " " + version;
            }
            catch
            {
                return "";
            }
        }

        private static IEnumerable<string> Usings() =>
            ScriptSource.DefaultUsings.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(u => u.Trim())
                .Where(u => u.StartsWith("using ", StringComparison.Ordinal))
                .Select(u => u.Substring("using ".Length).Trim());

        // The references scripts compile against, with the docs the editor's tooltips use; null if they cannot be built.
        private static CSharpCompilation Compilation()
        {
            try
            {
                var references = ScriptCompiler.BuildReferences(new List<string>(), new List<CompileMessage>());
                return ScriptCompiler.CreateCompilation("AiInstructions", Enumerable.Empty<SyntaxTree>(), references);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Инструкция для ИИ: не удалось собрать ссылки: " + ex.Message);
                return null;
            }
        }

        private static readonly SymbolDisplayFormat Signature = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                              SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut |
                              SymbolDisplayParameterOptions.IncludeExtensionThis,
            propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

        // With the class name, for static members called as FileMethods.Something(...).
        private static readonly SymbolDisplayFormat StaticSignature =
            Signature.AddMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType);

        private static readonly SymbolDisplayFormat TypeName = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        // "- `void Show(object geometry, Color color, string label = null)` — Draws geometry over the model ...",
        // from the same docs as the editor's tooltips.
        private static IEnumerable<string> ScriptBaseMembers(CSharpCompilation compilation)
        {
            var lines = new List<string>();
            try
            {
                var scriptBase = compilation?.GetTypeByMetadataName("RevitDevKit.ScriptApi.ScriptBase");
                if (scriptBase == null)
                    return lines;

                // Properties first (Doc, UIDoc, S0...), then methods, each in the order they are declared.
                foreach (var member in scriptBase.GetMembers().Where(IsListed).OrderBy(m => m is IPropertySymbol ? 0 : 1))
                {
                    if (member is IMethodSymbol method && method.MethodKind != MethodKind.Ordinary)
                        continue;

                    lines.Add(Line(member, Signature));
                }
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Инструкция для ИИ: не удалось перечислить члены ScriptBase: " + ex.Message);
            }

            return lines;
        }

        // The loaded BIMPlugins.ExtStorage: its extension methods by the type they extend, then its static helpers,
        // its classes and interfaces with their members, and its enums. Its docs come as for the editor's tooltips.
        private static IEnumerable<string> ExtStorageApi(CSharpCompilation compilation)
        {
            var lines = new List<string>();
            try
            {
                var anchor = compilation?.GetTypeByMetadataName(typeof(RevitAPI).FullName);
                if (anchor == null)
                    return lines;

                var types = Types(anchor.ContainingAssembly.GlobalNamespace)
                    .Where(t => t.DeclaredAccessibility == Accessibility.Public)
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var staticMembers = types.Where(t => t.IsStatic)
                    .SelectMany(t => t.GetMembers().Where(IsListed))
                    .Where(m => !(m is IMethodSymbol method) || method.MethodKind == MethodKind.Ordinary)
                    .ToList();

                var extensions = staticMembers.OfType<IMethodSymbol>()
                    .Where(m => m.IsExtensionMethod)
                    .GroupBy(m => Extended(m.Parameters[0].Type))
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

                lines.Add("### Расширения — по типу, который они расширяют");
                foreach (var group in extensions)
                {
                    lines.Add("**`" + group.Key + "`**");
                    lines.AddRange(group.Select(m => Line(m, Signature)));
                }

                var helpers = staticMembers.Where(m => !(m is IMethodSymbol method && method.IsExtensionMethod)).ToList();
                if (helpers.Count > 0)
                {
                    lines.Add("");
                    lines.Add("### Статические помощники — вызываются через имя класса");
                    lines.AddRange(helpers.Select(m => Line(m, StaticSignature)));
                }

                var objects = types.Where(t => !t.IsStatic && (t.TypeKind == TypeKind.Class || t.TypeKind == TypeKind.Interface)).ToList();
                if (objects.Count > 0)
                {
                    lines.Add("");
                    lines.Add("### Классы и интерфейсы");
                    foreach (var type in objects)
                    {
                        var summary = SymbolDocumentation.Summary(type, CancellationToken.None);
                        lines.Add("**`" + (type.TypeKind == TypeKind.Interface ? "interface " : "class ") + type.ToDisplayString(TypeName) + "`**" +
                                  (summary.IsNullOrEmpty() ? "" : " — " + summary));

                        foreach (var member in type.GetMembers().Where(IsListed))
                        {
                            if (member is IMethodSymbol method)
                            {
                                if (method.MethodKind == MethodKind.Constructor)
                                    lines.Add(Line(member, Signature, "new "));
                                else if (method.MethodKind == MethodKind.Ordinary && !method.IsOverride)
                                    lines.Add(Line(member, member.IsStatic ? StaticSignature : Signature));
                            }
                            else
                            {
                                lines.Add(Line(member, member.IsStatic ? StaticSignature : Signature));
                            }
                        }
                    }
                }

                var enums = types.Where(t => t.TypeKind == TypeKind.Enum).ToList();
                if (enums.Count > 0)
                {
                    lines.Add("");
                    lines.Add("### Перечисления");
                    foreach (var e in enums)
                    {
                        var summary = SymbolDocumentation.Summary(e, CancellationToken.None);
                        var values = e.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst).Select(f => "`" + e.Name + "." + f.Name + "`");
                        lines.Add("- `enum " + e.Name + "`: " + string.Join(", ", values) + (summary.IsNullOrEmpty() ? "" : " — " + summary));
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Инструкция для ИИ: не удалось перечислить BIMPlugins.ExtStorage: " + ex.Message);
            }

            return lines;
        }

        // "- `XYZ ToPoint(this Element element, LocationType type = LocationType.Origin)` — Returns ..."
        private static string Line(ISymbol member, SymbolDisplayFormat format, string prefix = "")
        {
            var summary = SymbolDocumentation.Summary(member, CancellationToken.None);
            return "- `" + prefix + Display(member, format) + "`" + (summary.IsNullOrEmpty() ? "" : " — " + summary);
        }

        // Roslyn writes the default of an enum parameter as its number (= -2000151); the AI needs BuiltInCategory.OST_GenericModel.
        private static string Display(ISymbol member, SymbolDisplayFormat format)
        {
            if (!(member is IMethodSymbol method))
                return member.ToDisplayString(format);

            var withoutParameters = format.WithMemberOptions(format.MemberOptions & ~SymbolDisplayMemberOptions.IncludeParameters);
            var head = method.MethodKind == MethodKind.Constructor
                ? method.ContainingType.Name
                : method.ToDisplayString(withoutParameters.WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters));
            var constraints = string.Concat(method.TypeParameters
                .Where(t => t.ConstraintTypes.Any())
                .Select(t => " where " + t.Name + " : " + string.Join(", ", t.ConstraintTypes.Select(c => c.ToDisplayString(TypeName)))));

            return head + "(" + string.Join(", ", method.Parameters.Select(Parameter)) + ")" + constraints;
        }

        private static string Parameter(IParameterSymbol parameter)
        {
            var text = parameter.ToDisplayString(Signature.WithParameterOptions(Signature.ParameterOptions & ~SymbolDisplayParameterOptions.IncludeDefaultValue));
            if (parameter.ContainingSymbol is IMethodSymbol method && method.IsExtensionMethod && parameter.Ordinal == 0 && !text.StartsWith("this ", StringComparison.Ordinal))
                text = "this " + text;
            if (!parameter.HasExplicitDefaultValue)
                return text;

            var value = parameter.ExplicitDefaultValue;
            if (parameter.Type.TypeKind == TypeKind.Enum && value != null)
            {
                var field = parameter.Type.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.IsConst && Equals(f.ConstantValue, value));
                return text + " = " + parameter.Type.Name + "." + (field != null ? field.Name : value.ToString());
            }

            return text + " = " + (value == null ? "null" : value is string s ? "\"" + s + "\"" : value is bool b ? (b ? "true" : "false") :
                Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }

        private static bool IsListed(ISymbol member) =>
            member.DeclaredAccessibility == Accessibility.Public && !member.IsImplicitlyDeclared &&
            member.GetAttributes().All(a => a.AttributeClass?.Name != nameof(ObsoleteAttribute));

        // The heading an extension goes under: its receiver type, or the constraint of a generic one (this T where T : Element).
        private static string Extended(ITypeSymbol type)
        {
            if (type is ITypeParameterSymbol parameter)
            {
                var constraint = parameter.ConstraintTypes.FirstOrDefault();
                return constraint != null ? constraint.ToDisplayString(TypeName) : "любой тип";
            }

            return type.ToDisplayString(TypeName);
        }

        private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
                yield return type;

            foreach (var child in ns.GetNamespaceMembers())
            foreach (var type in Types(child))
                yield return type;
        }
    }
}
