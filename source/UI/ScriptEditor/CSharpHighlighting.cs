using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using RevitDevKit.Core;
using System;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;

namespace RevitDevKit.UI.ScriptEditor
{
    // AvalonEdit's C# highlighting marks a name as a method call only when '(' follows it, so a generic call such as
    // .Cast<Element>() or All<Wall>() stays plain. The editor uses its own copy of that definition with one more rule
    // for "Name<...>(": the shared definition is left alone, other add-ins in Revit may use the same AvalonEdit.
    internal static class CSharpHighlighting
    {
        private const string Resource = "ICSharpCode.AvalonEdit.Highlighting.Resources.CSharp-Mode.xshd";

        // A name, then a type argument list (three levels of nesting at most) and '('. Inside <...> only what type
        // arguments are made of, so a comparison like "a < b && c > (d)" is not taken for one.
        private const string TypeArguments = @"[\w\s.,\[\]?]*";
        private const string GenericCall =
            @"\b[\d\w_]+(?=\s*<" + TypeArguments + @"(?:<" + TypeArguments + @"(?:<" + TypeArguments + @">" + TypeArguments + @")*>" + TypeArguments + @")*>\s*\()";

        private static IHighlightingDefinition _definition;

        public static IHighlightingDefinition Definition => _definition ?? (_definition = Load());

        // Revit may have an older AvalonEdit loaded (4.3 from Dynamo): whatever goes wrong there, even a missing
        // member, which fails when Create is compiled, leaves the editor with the usual highlighting.
        private static IHighlightingDefinition Load()
        {
            try
            {
                return Create() ?? Shared();
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Подсветка обобщённых вызовов недоступна: " + ex.Message);
                return Shared();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IHighlightingDefinition Create()
        {
            IHighlightingDefinition definition;
            using (var stream = typeof(HighlightingManager).Assembly.GetManifestResourceStream(Resource))
            {
                if (stream == null)
                    return null;

                using (var reader = XmlReader.Create(stream))
                    definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }

            var methodCall = definition.GetNamedColor("MethodCall");
            if (methodCall == null)
                return null;

            definition.MainRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new Regex(GenericCall, RegexOptions.CultureInvariant),
                Color = methodCall
            });
            return definition;
        }

        private static IHighlightingDefinition Shared() => HighlightingManager.Instance.GetDefinition("C#");
    }
}
