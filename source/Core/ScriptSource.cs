using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitDevKit.Core
{
    public sealed class PreparedSource
    {
        public string Code { get; set; }

        public int LineOffset { get; set; }

        public IList<string> ExtraReferences { get; set; }
    }

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

        // Stops a script run under a debugger; does nothing when none is attached (Repeat Last, say).
        private const string Break = "if (System.Diagnostics.Debugger.IsAttached) System.Diagnostics.Debugger.Break();";

        // debugFile: under a debugger the lines map to this file (#line), the script's own or a snapshot of it,
        // and the run stops on the script's first line or at the start of its entry method.
        public static PreparedSource Prepare(string userCode, string debugFile = null)
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
                    Code = debugFile == null ? withoutDirectives : Line(debugFile) + BreakInEntry(withoutDirectives),
                    LineOffset = debugFile == null ? 0 : 1,
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
            if (debugFile != null)
            {
                // The break shows as the script's first line, which then runs on F10.
                sb.Append(Line(debugFile)).Append(Break).Append('\n');
                sb.Append(Line(debugFile));
            }

            sb.Append(body);
            sb.Append("\n} } }\n");

            return new PreparedSource
            {
                Code = sb.ToString(),
                LineOffset = debugFile == null ? 2 : 5,
                ExtraReferences = references
            };
        }

        private static string Line(string file) => "#line 1 \"" + file + "\"\n";

        // The first Execute, Main or Run with a body stops at its opening brace; the break stays on that line,
        // so the numbers of the lines do not change.
        private static string BreakInEntry(string code)
        {
            var methods = CSharpSyntaxTree.ParseText(code).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Body != null).ToList();
            var entry = new[] { "Execute", "Main", "Run" }
                .Select(name => methods.FirstOrDefault(m => m.Identifier.ValueText == name))
                .FirstOrDefault(m => m != null);
            if (entry == null) return code;

            var at = entry.Body.OpenBraceToken.Span.End;
            return code.Substring(0, at) + " " + Break + code.Substring(at);
        }
    }
}
