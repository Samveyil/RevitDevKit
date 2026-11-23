using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Text.RegularExpressions;
using System.Xml;

namespace RevitDevKit.UI.ScriptEditor
{
    // AvalonEdit's C# highlighting marks a name as a method call only when '(' follows it, so a generic call such as
    // .Cast<Element>() or All<Wall>() stays plain. The editor uses its own copy of that definition with one more rule
    // for "Name<...>(": AvalonEdit's registered definition is left as it is.
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

        private static IHighlightingDefinition Load() => Create() ?? Shared();

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
