using BIMPlugins.ExtStorage;
using BIMPlugins.ExtStorage.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RevitDevKit.Core.Compiler;
using RevitDevKit.Core.IntelliSense;
using RevitDevKit.Core.Output;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RevitDevKit.Core.Ai
{
    // Instructions for an AI chat (ChatGPT, Claude, Copilot...) on how to write scripts for Script Editor: pasted at the
    // start of a chat, they keep the AI from writing an IExternalCommand and make it use what Script Editor offers.
    // Built from the add-in itself, so they follow it: the members of ScriptBase with their docs, the loaded
    // BIMPlugins.ExtStorage, the usings scripts get, the Revit version and whether Автотранзакция is on.
    // In English, which costs fewer tokens than Russian; the AI is asked to answer in the user's language.
    // The standard text names the ExtStorage methods and gives the signatures of the common ones; the detailed one
    // gives every ExtStorage member with its signature and description, about three times the tokens.
    public static class AiInstructions
    {
        private const string ExtStorageRepository = "https://github.com/Samveyil/BIMPlugins.ExtStorage";
        private const string ExtStoragePackage = "https://www.nuget.org/packages/BIMPlugins.ExtStorage";

        // The ExtStorage methods the standard text gives with signatures: those scripts need most, whose parameters an AI guesses wrong.
        private static readonly HashSet<string> KeyMethods = new HashSet<string>
        {
            "ToPoint", "ToLine", "ToParameter", "GetValue", "SetValue", "ToElement", "ToElementType",
            "ToMillimeters", "FromMillimeters", "ToDegrees", "FromDegrees"
        };

        public static string Build(bool autoTransaction, bool detailed = false)
        {
            var text = new StringBuilder();
            var revit = RevitVersion();
            var revitName = "Revit" + (revit == null ? "" : " " + revit);
            var compilation = Compilation();

            text.AppendLine("# Script Editor (RevitDevKit): writing scripts");
            text.AppendLine();
            text.AppendLine("You help write C# scripts for Script Editor, a code editor inside " + revitName + " (the RevitDevKit add-in). " +
                            "A script runs at once on F5: no DLL build, no .addin, no Revit restart. " +
                            "**Always answer in the user's language**, even though these instructions are in English.");
            text.AppendLine();

            text.AppendLine("## Code format");
            text.AppendLine("- Write a fragment: plain statements, like a method body. **Do not write** `IExternalCommand`, a class, `Execute`, `Main` " +
                            "or a `namespace`: Script Editor wraps the fragment in `void Main()` of a class derived from `ScriptBase`.");
            text.AppendLine("- Allowed: `return;` (no value), local functions, lambdas, anonymous types, tuples. Not allowed: declaring classes, structs, enums, records.");
            text.AppendLine("- `using` lines may go at the top, but the main namespaces are already imported (see Environment).");
            text.AppendLine("- An external DLL is referenced by `#r \"C:\\path\\to\\library.dll\"` at the top.");
            text.AppendLine("- Reply with one ```csharp block that is pasted into the editor as is, plus a short explanation. When fixing a script, return all of it.");
            text.AppendLine();

            text.AppendLine("## Transactions");
            text.AppendLine(autoTransaction
                ? "- The Автотранзакция (auto transaction) option is on: the whole script already runs in a transaction. Do not create a `Transaction` or call `Tx`."
                : "- The Автотранзакция (auto transaction) option is off. Reading the model needs no transaction. Wrap every change " +
                  "(`SetValue`, `Doc.Delete`, `Create`, moving) in `Tx(\"Name\", () => { ... })` or `var x = Tx(\"Name\", () => ...)`; " +
                  "never create a `Transaction` yourself. Without one Revit throws \"Modification of the document is forbidden\".");
            text.AppendLine();

            text.AppendLine("## Output");
            text.AppendLine("- Write results to the Script Editor console: `Print`, `Table`, `Ok`, `Warn`, `Fail`, `Dump`, `Compare`. No `TaskDialog`, no `MessageBox`.");
            text.AppendLine("- Build messages with interpolation: `Ok($\"Selected walls: {ids.Count}\")`.");
            text.AppendLine("- Show a collection with `Table(items, x => x.Id, x => x.Name)` or `Print(items)`, not a `Print` per item.");
            text.AppendLine("- `Console.WriteLine` and `Debug.WriteLine` also go to the console.");
            text.AppendLine("- Show geometry with `Show(...)`: it is drawn over the model in the views, nothing is created in the model.");
            text.AppendLine("- No need to catch exceptions just to report them: Script Editor shows them with the script line.");
            text.AppendLine();

            text.AppendLine("## Environment");
            text.AppendLine("- " + revitName + ", .NET Framework 4.8, C# 9.");
            text.AppendLine("- The code runs on the Revit API thread: no `async`/`await`, no `Task.Run` for model work.");
            text.AppendLine("- Imported: " + Usings() + ".");
            text.AppendLine();

            text.AppendLine("## Common mistakes: check the code before answering");
            text.AppendLine("- The document, view and selection are there: `Doc`, `UIDoc`, `ActiveView`, `Selection`, `S0`. " +
                            "Do not get them from `commandData` or `uiapp`; for a short name, `var doc = Doc;`.");
            text.AppendLine("- Get elements with `All<Wall>()`, `All<FamilyInstance>(BuiltInCategory.OST_Doors)`, `Instances(...)`, `Types(...)`; " +
                            "by id, `id.ToElement<Wall>()`. Use `FilteredElementCollector` only for special filters.");
            text.AppendLine("- Check that things exist: `S0` is null and `Selection` is empty when nothing is selected; then `Warn(...)` and `return;`.");
            text.AppendLine("- Revit API lengths are in internal units (feet), angles in radians. Convert with `ToMillimeters()` / `FromMillimeters()`, " +
                            "`ToDegrees()` / `FromDegrees()` (BIMPlugins.ExtStorage), not by dividing by 304.8.");
            text.AppendLine("- Parameter names depend on the Revit language, so get built-in ones by `BuiltInParameter`: " +
                            "`element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)`. Read with `GetValue()`, write with `SetValue(value)`.");
            text.AppendLine("- An `ElementId` value is `id.GetValue()`: `int` before Revit 2024, `long` from 2024. `IntegerValue` is obsolete from 2024.");
            text.AppendLine("- Do not change the model while iterating a `FilteredElementCollector`: call `.ToList()` first.");
            text.AppendLine("- C# 9 on .NET Framework 4.8 has no `record`, `init`, `required`, `global using`, file-scoped `namespace`, raw strings `\"\"\"`, " +
                            "collection expressions `[1, 2]`, indices and ranges `^1`, `..`, and no .NET Core methods: `DistinctBy`, `MaxBy`, `MinBy`, " +
                            "`Chunk`, `TakeLast`, `string.Join(',', ...)`, `Split(\", \")` with a string (use `Split(new[] { \", \" }, StringSplitOptions.None)`), `Contains(char)`.");
            text.AppendLine("- Do not invent methods: use the " + revitName + " API and the members listed below. " +
                            (detailed ? "" : "If you do not know the signature of a BIMPlugins.ExtStorage method, use the plain Revit API instead. ") +
                            "If unsure that a method exists, say so.");
            text.AppendLine();

            text.AppendLine("## ScriptBase members: available in the fragment without a prefix");
            foreach (var line in ScriptBaseMembers(compilation))
                text.AppendLine(line);
            text.AppendLine();

            text.AppendLine("## BIMPlugins.ExtStorage" + ExtStorageVersion() + ": Revit API extensions, already referenced");
            text.AppendLine("Extension methods and helpers for elements, parameters, units, geometry, views and files; all its namespaces are imported. " +
                            "Extensions are called on the object: `wall.ToPoint(LocationType.StartPoint)`, `width.ToMillimeters()`. " +
                            "Source: " + ExtStorageRepository + ", NuGet: " + ExtStoragePackage + ".");
            text.AppendLine();
            foreach (var line in detailed ? ExtStorageDetailed(compilation) : ExtStorageStandard(compilation))
                text.AppendLine(line);
            text.AppendLine();

            text.AppendLine("## Example");
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
        public static string BuildWithScript(bool autoTransaction, string title, string code, IEnumerable<string> problems, bool detailed = false)
        {
            var text = new StringBuilder(Build(autoTransaction, detailed));
            text.AppendLine();
            text.AppendLine("---");
            text.AppendLine();
            text.AppendLine("## My current script" + (title.IsNullOrWhiteSpace() ? "" : " \"" + title + "\""));
            text.AppendLine("```csharp");
            text.AppendLine((code ?? string.Empty).TrimEnd());
            text.AppendLine("```");

            var list = (problems ?? Enumerable.Empty<string>()).ToList();
            text.AppendLine();
            if (list.Count == 0)
            {
                text.AppendLine("The last run had no errors (or the script has not been run yet).");
            }
            else
            {
                text.AppendLine("## Errors of the last run (line numbers are script lines)");
                foreach (var problem in list)
                    text.AppendLine("- " + problem);
                text.AppendLine();
                text.AppendLine("Find the cause of each error, fix it and return the whole script.");
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

        // "`System`, `System.Linq`, ..., all `BIMPlugins.ExtStorage` namespaces": DevKit's own are left out, as scripts use them through ScriptBase.
        private static string Usings()
        {
            var all = ScriptSource.DefaultUsings.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(u => u.Trim())
                .Where(u => u.StartsWith("using ", StringComparison.Ordinal))
                .Select(u => u.Substring("using ".Length).Trim())
                .ToList();

            var listed = all.Where(u => !u.StartsWith("BIMPlugins.", StringComparison.Ordinal) && !u.StartsWith("RevitDevKit.", StringComparison.Ordinal))
                .Select(u => "`" + u + "`");
            return string.Join(", ", listed) + (all.Any(u => u.StartsWith("BIMPlugins.ExtStorage", StringComparison.Ordinal))
                ? ", all `BIMPlugins.ExtStorage` namespaces"
                : "");
        }

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

        private static readonly Regex SelectedByIndex = new Regex(@"^S\d$", RegexOptions.Compiled);

        // "- `void Show(object geometry, Color color, string label = null)` — Draws geometry over the model ...", from the same docs
        // as the editor's tooltips. Overloads share a line, as do the colors and S0, S1, S2.
        private static IEnumerable<string> ScriptBaseMembers(CSharpCompilation compilation)
        {
            var lines = new List<string>();
            try
            {
                var scriptBase = compilation?.GetTypeByMetadataName("RevitDevKit.ScriptApi.ScriptBase");
                if (scriptBase == null)
                    return lines;

                var members = scriptBase.GetMembers().Where(IsListed).ToList();
                var properties = members.OfType<IPropertySymbol>().ToList();

                var colors = properties.Where(p => p.Type.Name == "Color").ToList();
                var selected = properties.Where(p => SelectedByIndex.IsMatch(p.Name)).ToList();
                foreach (var property in properties)
                {
                    if (SymbolEqualityComparer.Default.Equals(property, colors.FirstOrDefault()))
                        lines.Add("- `Color " + string.Join(", ", colors.Select(c => c.Name)) + "`: colors for `Show(geometry, Red)`.");
                    else if (SymbolEqualityComparer.Default.Equals(property, selected.FirstOrDefault()))
                        lines.Add("- `Element " + string.Join(", ", selected.Select(s => s.Name)) + "`: the first, second, third selected element; null if not selected.");
                    else if (!colors.Contains(property, SymbolEqualityComparer.Default) && !selected.Contains(property, SymbolEqualityComparer.Default))
                        lines.Add(Line(property, Signature));
                }

                var methods = members.OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary);
                foreach (var overloads in methods.GroupBy(m => m.Name))
                    lines.Add(Overloads(overloads.ToList()));
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Инструкция для ИИ: не удалось перечислить члены ScriptBase: " + ex.Message);
            }

            return lines;
        }

        // "- `IList<T> All<T>` where T : Element: `()`, `(ElementId viewId)` — Returns ...": overloads that differ only in parameters share
        // the return type; others (Tx and Tx<TResult>) are given in full.
        private static string Overloads(IList<IMethodSymbol> overloads)
        {
            if (overloads.Count == 1)
                return Line(overloads[0], Signature);

            var summary = overloads.Select(m => SymbolDocumentation.Summary(m, CancellationToken.None)).FirstOrDefault(s => !s.IsNullOrEmpty());
            var heads = overloads.Select(m => Head(m, Signature) + Constraints(m)).Distinct().ToList();

            var text = heads.Count == 1
                ? "- `" + heads[0] + "`: " + string.Join(", ", overloads.Select(m => "`(" + Parameters(m) + ")`"))
                : "- " + string.Join(", ", overloads.Select(m => "`" + Display(m, Signature) + "`"));
            return text + (summary.IsNullOrEmpty() ? "" : " — " + summary);
        }

        // The loaded BIMPlugins.ExtStorage in short: the names of its extensions by the type they extend, the signatures
        // of the common ones, its helpers, classes and enums.
        private static IEnumerable<string> ExtStorageStandard(CSharpCompilation compilation)
        {
            var lines = new List<string>();
            try
            {
                var api = ExtStorage(compilation);
                if (api == null)
                    return lines;

                lines.Add("### Extensions, by the type they extend");
                foreach (var group in api.Extensions)
                    lines.Add("- `" + group.Key + "`: " + string.Join(", ", group.Select(m => m.Name).Distinct()));

                lines.Add("");
                lines.Add("### Signatures of the common ones");
                foreach (var method in api.Extensions.SelectMany(g => g).Where(m => KeyMethods.Contains(m.Name)))
                    lines.Add("- `" + Display(method, Signature) + "`");

                if (api.Helpers.Count > 0)
                {
                    lines.Add("");
                    lines.Add("### Static helpers, called by class name");
                    foreach (var type in api.Helpers.GroupBy(m => m.ContainingType.Name))
                        lines.Add("- `" + type.Key + "`: " + string.Join(", ", type.Select(m => m.Name).Distinct()));
                }

                if (api.Classes.Count > 0)
                {
                    lines.Add("");
                    lines.Add("### Classes");
                    foreach (var type in api.Classes)
                    {
                        var summary = SymbolDocumentation.Summary(type, CancellationToken.None);
                        var statics = type.GetMembers().Where(m => IsListed(m) && m.IsStatic && !(m is IMethodSymbol)).Select(m => type.Name + "." + m.Name).ToList();
                        lines.Add("- `" + type.ToDisplayString(TypeName) + "`" + (summary.IsNullOrEmpty() ? "" : " — " + summary) +
                                  (statics.Count == 0 ? "" : " Instances: " + string.Join(", ", statics.Select(s => "`" + s + "`")) + "."));
                    }
                }

                lines.AddRange(Enums(api.Enums));
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Инструкция для ИИ: не удалось перечислить BIMPlugins.ExtStorage: " + ex.Message);
            }

            return lines;
        }

        // The loaded BIMPlugins.ExtStorage in full: every member with its signature and description.
        private static IEnumerable<string> ExtStorageDetailed(CSharpCompilation compilation)
        {
            var lines = new List<string>();
            try
            {
                var api = ExtStorage(compilation);
                if (api == null)
                    return lines;

                lines.Add("### Extensions, by the type they extend");
                foreach (var group in api.Extensions)
                {
                    lines.Add("**`" + group.Key + "`**");
                    lines.AddRange(group.Select(m => Line(m, Signature)));
                }

                if (api.Helpers.Count > 0)
                {
                    lines.Add("");
                    lines.Add("### Static helpers, called by class name");
                    lines.AddRange(api.Helpers.Select(m => Line(m, StaticSignature)));
                }

                if (api.Classes.Count > 0)
                {
                    lines.Add("");
                    lines.Add("### Classes and interfaces");
                    foreach (var type in api.Classes)
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

                lines.AddRange(Enums(api.Enums));
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Инструкция для ИИ: не удалось перечислить BIMPlugins.ExtStorage: " + ex.Message);
            }

            return lines;
        }

        private static IEnumerable<string> Enums(IList<INamedTypeSymbol> enums)
        {
            if (enums.Count == 0)
                yield break;

            yield return "";
            yield return "### Enums";
            foreach (var e in enums)
            {
                var summary = SymbolDocumentation.Summary(e, CancellationToken.None);
                var values = e.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst).Select(f => "`" + e.Name + "." + f.Name + "`");
                yield return "- `enum " + e.Name + "`: " + string.Join(", ", values) + (summary.IsNullOrEmpty() ? "" : " — " + summary);
            }
        }

        private sealed class ExtStorageApi
        {
            public List<IGrouping<string, IMethodSymbol>> Extensions;
            public List<ISymbol> Helpers;
            public List<INamedTypeSymbol> Classes;
            public List<INamedTypeSymbol> Enums;
        }

        // The public API of the loaded BIMPlugins.ExtStorage, as scripts compile against it: its extension methods
        // by the type they extend, the other static members, its classes and interfaces, its enums.
        private static ExtStorageApi ExtStorage(CSharpCompilation compilation)
        {
            var anchor = compilation?.GetTypeByMetadataName(typeof(RevitAPI).FullName);
            if (anchor == null)
                return null;

            var types = Types(anchor.ContainingAssembly.GlobalNamespace)
                .Where(t => t.DeclaredAccessibility == Accessibility.Public)
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var staticMembers = types.Where(t => t.IsStatic)
                .SelectMany(t => t.GetMembers().Where(IsListed))
                .Where(m => !(m is IMethodSymbol method) || method.MethodKind == MethodKind.Ordinary)
                .ToList();

            return new ExtStorageApi
            {
                Extensions = staticMembers.OfType<IMethodSymbol>()
                    .Where(m => m.IsExtensionMethod)
                    .GroupBy(m => Extended(m.Parameters[0].Type))
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Helpers = staticMembers.Where(m => !(m is IMethodSymbol method && method.IsExtensionMethod)).ToList(),
                Classes = types.Where(t => !t.IsStatic && (t.TypeKind == TypeKind.Class || t.TypeKind == TypeKind.Interface)).ToList(),
                Enums = types.Where(t => t.TypeKind == TypeKind.Enum).ToList()
            };
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

            return Head(method, format) + "(" + Parameters(method) + ")" + Constraints(method);
        }

        // "IList<T> All<T>", "NaturalComparer" for a constructor.
        private static string Head(IMethodSymbol method, SymbolDisplayFormat format) =>
            method.MethodKind == MethodKind.Constructor
                ? method.ContainingType.Name
                : method.ToDisplayString(format
                    .WithMemberOptions(format.MemberOptions & ~SymbolDisplayMemberOptions.IncludeParameters)
                    .WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters));

        private static string Constraints(IMethodSymbol method) =>
            string.Concat(method.TypeParameters
                .Where(t => t.ConstraintTypes.Any())
                .Select(t => " where " + t.Name + " : " + string.Join(", ", t.ConstraintTypes.Select(c => c.ToDisplayString(TypeName)))));

        private static string Parameters(IMethodSymbol method) => string.Join(", ", method.Parameters.Select(Parameter));

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
                Convert.ToString(value, CultureInfo.InvariantCulture));
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
                return constraint != null ? constraint.ToDisplayString(TypeName) : "any type";
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
