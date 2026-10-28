using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitDevKit.Core
{
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

        private static readonly Regex UsingLine = new Regex(
            @"^\s*using\s+(static\s+)?[A-Za-z_@][\w.]*(\s*=\s*[\w.<>,\[\]\s]+)?\s*;\s*$",
            RegexOptions.Compiled);

        private static readonly Regex ReferenceDirective = new Regex(
            @"^\s*#r\s+""(?<ref>[^""]+)""\s*$",
            RegexOptions.Compiled);

        private static readonly Regex TopLevelDeclaration = new Regex(
            @"^\s*((public|internal|private|protected|sealed|abstract|static|partial|unsafe)\s+)*(class|struct|interface|record|enum|namespace)\s+[A-Za-z_@]",
            RegexOptions.Compiled | RegexOptions.Multiline);

        public static PreparedSource Prepare(string userCode)
        {
            if (userCode == null)
                userCode = string.Empty;

            var references = new List<string>();
            var lines = userCode.Replace("\r\n", "\n").Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var m = ReferenceDirective.Match(lines[i]);
                if (!m.Success)
                    continue;
                
                references.Add(m.Groups["ref"].Value.Trim());
                lines[i] = string.Empty;
            }

            var withoutDirectives = string.Join("\n", lines);

            if (TopLevelDeclaration.IsMatch(withoutDirectives))
            {
                return new PreparedSource
                {
                    Code = withoutDirectives,
                    LineOffset = 0,
                    ExtraReferences = references
                };
            }

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
                LineOffset = 2,
                ExtraReferences = references
            };
        }
    }
}
