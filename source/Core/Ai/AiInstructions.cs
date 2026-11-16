using BIMPlugins.ExtStorage;
using BIMPlugins.ExtStorage.Extensions;
using Microsoft.CodeAnalysis;
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
    // Built from the add-in itself, so they follow it: the members of ScriptBase with their docs, the usings scripts get,
    // the extensions of the loaded BIMPlugins.ExtStorage, the Revit version and whether Автотранзакция is on.
    public static class AiInstructions
    {
        public static string Build(bool autoTransaction)
        {
            var text = new StringBuilder();
            var revit = RevitVersion();

            text.AppendLine("# Script Editor (RevitDevKit): как писать скрипты");
            text.AppendLine();
            text.AppendLine("Ты помогаешь писать C#-скрипты для Script Editor — редактора кода внутри Revit" + (revit == null ? "" : " " + revit) +
                            " (надстройка RevitDevKit). Скрипт выполняется сразу по F5: без сборки DLL, без .addin и без перезапуска Revit. " +
                            "Отвечай на языке пользователя.");
            text.AppendLine();

            text.AppendLine("## Формат кода");
            text.AppendLine("- Пиши фрагмент: просто строки кода, как тело метода. **Не пиши** `IExternalCommand`, класс, метод `Execute` или `Main` и `namespace` — " +
                            "Script Editor сам обернёт фрагмент в класс-наследник `ScriptBase` и выполнит его.");
            text.AppendLine("- Во фрагменте можно `return;`, локальные функции и лямбды. Объявлять классы и `enum` во фрагменте нельзя.");
            text.AppendLine("- `using` в начале фрагмента можно, но основные уже подключены (список ниже).");
            text.AppendLine("- Внешняя DLL подключается строкой `#r \"C:\\путь\\к\\библиотеке.dll\"` в начале фрагмента.");
            text.AppendLine("- Отдай один блок ```csharp, который вставляется в редактор целиком, и коротко поясни, что он делает.");
            text.AppendLine();

            text.AppendLine("## Транзакции");
            text.AppendLine(autoTransaction
                ? "- Галочка «Автотранзакция» включена: весь скрипт уже выполняется в транзакции. Не создавай `Transaction` и не вызывай `Tx`."
                : "- Галочка «Автотранзакция» выключена. Читать модель можно без транзакции. Изменения модели оборачивай в " +
                  "`Tx(\"Название\", () => { ... })` или `var x = Tx(\"Название\", () => ...)`; `Transaction` вручную не создавай.");
            text.AppendLine();

            text.AppendLine("## Вывод и отладка");
            text.AppendLine("- Результат пиши в консоль Script Editor: `Print`, `Table`, `Ok`, `Warn`, `Fail`, `Dump`, `Compare`. Не используй `TaskDialog` и `MessageBox`.");
            text.AppendLine("- `Console.WriteLine` и `Debug.WriteLine` тоже попадают в консоль.");
            text.AppendLine("- Геометрию показывай через `Show(...)`: она рисуется поверх модели на видах, в модели ничего не создаётся.");
            text.AppendLine("- Исключение не нужно ловить ради вывода: Script Editor сам покажет его в консоли со строкой скрипта.");
            text.AppendLine();

            text.AppendLine("## Окружение");
            text.AppendLine("- Revit" + (revit == null ? "" : " " + revit) + ", .NET Framework 4.8, C# до версии 9 (без `record` и `init`).");
            text.AppendLine("- Код выполняется в потоке Revit API: без `async`/`await` и без `Task.Run` для работы с моделью.");
            text.AppendLine("- Значение `ElementId` получай через `id.GetValue()`: это `int` до Revit 2024 и `long` с 2024.");
            text.AppendLine("- Подключённые `using`: " + string.Join(", ", Usings().Select(u => "`" + u + "`")) + ".");
            text.AppendLine();

            text.AppendLine("## Члены ScriptBase — доступны во фрагменте без префикса");
            foreach (var line in ScriptBaseMembers())
                text.AppendLine(line);
            text.AppendLine();

            text.AppendLine("## BIMPlugins.ExtStorage — подключена, используй её расширения");
            foreach (var line in ExtStorageApi())
                text.AppendLine(line);
            text.AppendLine();

            text.AppendLine("## Пример");
            text.AppendLine("```csharp");
            text.AppendLine("var walls = All<Wall>(ActiveView.Id);");
            text.AppendLine("Table(walls, w => w.Id, w => w.Name, w => w.Width.ToMillimeters());");
            text.AppendLine("Show(walls, Red, \"стены вида\");");
            if (!autoTransaction)
                text.AppendLine("Tx(\"Комментарий\", () => walls.First().ToParameter(\"Комментарии\").SetValue(\"проверено\"));");
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

        private static IEnumerable<string> Usings() =>
            ScriptSource.DefaultUsings.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(u => u.Trim())
                .Where(u => u.StartsWith("using ", StringComparison.Ordinal))
                .Select(u => u.Substring("using ".Length).Trim());

        private static readonly SymbolDisplayFormat Signature = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                              SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut,
            propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

        // "- `void Show(object geometry, Color color, string label = null)` — Draws geometry over the model ...",
        // from the same docs as the editor's tooltips.
        private static IEnumerable<string> ScriptBaseMembers()
        {
            var lines = new List<string>();
            try
            {
                var references = ScriptCompiler.BuildReferences(new List<string>(), new List<CompileMessage>());
                var compilation = ScriptCompiler.CreateCompilation("AiInstructions", Enumerable.Empty<SyntaxTree>(), references);
                var scriptBase = compilation.GetTypeByMetadataName("RevitDevKit.ScriptApi.ScriptBase");
                if (scriptBase == null)
                    return lines;

                // Properties first (Doc, UIDoc, S0...), then methods, each in the order they are declared.
                foreach (var member in scriptBase.GetMembers().OrderBy(m => m is IPropertySymbol ? 0 : 1))
                {
                    if (member.DeclaredAccessibility != Accessibility.Public || member.IsImplicitlyDeclared ||
                        member is IMethodSymbol method && method.MethodKind != MethodKind.Ordinary)
                        continue;

                    var summary = SymbolDocumentation.Summary(member, CancellationToken.None);
                    lines.Add("- `" + member.ToDisplayString(Signature) + "`" + (summary.IsNullOrEmpty() ? "" : " — " + summary));
                }
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Инструкция для ИИ: не удалось перечислить члены ScriptBase: " + ex.Message);
            }

            return lines;
        }

        // The extension methods of the loaded BIMPlugins.ExtStorage, by the type they extend, then its helpers and enums.
        private static IEnumerable<string> ExtStorageApi()
        {
            var lines = new List<string>();
            try
            {
                var types = PublicTypes(typeof(RevitAPI).Assembly);

                // Type by type: one that does not load (a Revit type missing from this version) leaves the others.
                var extensions = types
                    .Where(t => t.IsAbstract && t.IsSealed && t.IsDefined(typeof(ExtensionAttribute), false))
                    .SelectMany(t => Safe(() => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m => m.IsDefined(typeof(ExtensionAttribute), false))
                        .Select(m => new { Extended = TypeName(m.GetParameters()[0].ParameterType), m.Name })
                        .ToList()))
                    .GroupBy(m => m.Extended)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

                foreach (var group in extensions)
                    lines.Add("- `" + group.Key + "`: " + string.Join(", ", group.Select(m => m.Name).Distinct().Select(n => "`" + n + "`")));

                var helpers = types
                    .Where(t => t.IsAbstract && t.IsSealed && !t.IsDefined(typeof(ExtensionAttribute), false))
                    .OrderBy(t => t.Name);
                foreach (var helper in helpers)
                {
                    var members = Safe(() => helper.GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name)
                        .Concat(helper.GetProperties(BindingFlags.Public | BindingFlags.Static).Select(p => p.Name))
                        .Where(n => !n.StartsWith("get_", StringComparison.Ordinal))
                        .Distinct()
                        .ToList());
                    if (members.Count > 0)
                        lines.Add("- `" + helper.Name + "`: " + string.Join(", ", members.Select(n => "`" + helper.Name + "." + n + "`")));
                }

                foreach (var e in types.Where(t => t.IsEnum).OrderBy(t => t.Name))
                    lines.Add("- `enum " + e.Name + "`: " + string.Join(", ", Enum.GetNames(e).Select(n => "`" + e.Name + "." + n + "`")));

                var classes = types.Where(t => t.IsClass && !t.IsAbstract).Select(t => "`" + t.Name + "`").ToList();
                if (classes.Count > 0)
                    lines.Add("- Классы: " + string.Join(", ", classes) + ".");
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Инструкция для ИИ: не удалось перечислить BIMPlugins.ExtStorage: " + ex.Message);
            }

            return lines;
        }

        private static List<Type> PublicTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes().Where(t => t.IsPublic).ToList();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null && t.IsPublic).ToList();
            }
        }

        private static List<T> Safe<T>(Func<List<T>> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return new List<T>();
            }
        }

        // "Element", "ICollection<ElementId>", "double".
        private static string TypeName(Type type)
        {
            if (type == typeof(double)) return "double";
            if (type == typeof(string)) return "string";
            if (type == typeof(object)) return "object";
            if (!type.IsGenericType) return type.Name;

            var name = type.Name.Substring(0, type.Name.IndexOf('`'));
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
        }
    }
}
