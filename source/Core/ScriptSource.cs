using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitDevKit.Core
{
    /// <summary>
    /// Результат подготовки исходника к компиляции.
    /// </summary>
    public sealed class PreparedSource
    {
        /// <summary>Готовый к компиляции код.</summary>
        public string Code { get; set; }

        /// <summary>Сколько строк добавлено сверху (для пересчёта номеров в ошибках).</summary>
        public int LineOffset { get; set; }

        /// <summary>true — пользователь написал короткий фрагмент, мы обернули его в класс.</summary>
        public bool WasWrapped { get; set; }

        /// <summary>Ссылки, запрошенные директивами #r.</summary>
        public IList<string> ExtraReferences { get; set; }
    }

    /// <summary>
    /// Разбирает пользовательский код: вытаскивает #r, решает — фрагмент это или
    /// полноценный файл, и при необходимости оборачивает фрагмент в класс-скрипт.
    /// </summary>
    public static class ScriptSource
    {
        public const string EntryTypeName = "RevitDevKit.UserScripts.__ScriptEntry";

        private const string DefaultUsings =
            "using System; using System.Collections; using System.Collections.Generic; using System.Linq; " +
            "using System.Text; using System.IO; " +
            "using Autodesk.Revit.DB; using Autodesk.Revit.DB.Structure; " +
            "using Autodesk.Revit.UI; using Autodesk.Revit.UI.Selection; " +
            "using RevitDevKit.Core; using RevitDevKit.Scripting; " +
            "using BIMPlugins.ExtStorage.Extensions; using BIMPlugins.ExtStorage.Extensions.UtilsExtensions;";

        // using System.Linq;  /  using static System.Math;  /  using Doc = Autodesk.Revit.DB.Document;
        // Намеренно не совпадает с оператором using (...) { }
        private static readonly Regex UsingLine = new Regex(
            @"^\s*using\s+(static\s+)?[A-Za-z_@][\w.]*(\s*=\s*[\w.<>,\[\]\s]+)?\s*;\s*$",
            RegexOptions.Compiled);

        private static readonly Regex ReferenceDirective = new Regex(
            @"^\s*#r\s+""(?<ref>[^""]+)""\s*$",
            RegexOptions.Compiled);

        // Объявление типа или namespace => это полноценный файл, а не фрагмент.
        private static readonly Regex TopLevelDeclaration = new Regex(
            @"^\s*((public|internal|private|protected|sealed|abstract|static|partial|unsafe)\s+)*(class|struct|interface|record|enum|namespace)\s+[A-Za-z_@]",
            RegexOptions.Compiled | RegexOptions.Multiline);

        public static PreparedSource Prepare(string userCode)
        {
            if (userCode == null)
                userCode = string.Empty;

            var references = new List<string>();
            var lines = userCode.Replace("\r\n", "\n").Split('\n');

            // 1. Вынимаем #r, сохраняя нумерацию строк.
            for (var i = 0; i < lines.Length; i++)
            {
                var m = ReferenceDirective.Match(lines[i]);
                if (!m.Success)
                    continue;
                
                references.Add(m.Groups["ref"].Value.Trim());
                lines[i] = string.Empty;
            }

            var withoutDirectives = string.Join("\n", lines);

            // 2. Полноценный файл — компилируем как есть.
            if (TopLevelDeclaration.IsMatch(withoutDirectives))
            {
                return new PreparedSource
                {
                    Code = withoutDirectives,
                    LineOffset = 0,
                    WasWrapped = false,
                    ExtraReferences = references
                };
            }

            // 3. Фрагмент: поднимаем using-директивы наверх, остальное кладём в метод.
            var hoisted = new List<string>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (!UsingLine.IsMatch(lines[i]))
                    continue;
                
                hoisted.Add(lines[i].Trim());
                lines[i] = string.Empty;
            }

            var body = string.Join("\n", lines);

            var sb = new StringBuilder();
            sb.Append(DefaultUsings);
            foreach (var u in hoisted)
                sb.Append(' ').Append(u);
            
            sb.Append('\n');
            sb.Append("namespace RevitDevKit.UserScripts { public class __ScriptEntry : RevitDevKit.Scripting.ScriptBase { public void Main() {\n");
            sb.Append(body);
            sb.Append("\n} } }\n");

            return new PreparedSource
            {
                Code = sb.ToString(),
                LineOffset = 2, // строка с using-ами + строка с объявлением класса
                WasWrapped = true,
                ExtraReferences = references
            };
        }
    }
}