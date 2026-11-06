using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace RevitDevKit.Core
{
    public enum CompletionKind
    {
        Keyword,
        Namespace,
        Class,
        Struct,
        Interface,
        Enum,
        EnumMember,
        Delegate,
        Method,
        Property,
        Field,
        Event,
        Local
    }

    public sealed class CompletionEntry
    {
        public string Name { get; set; }
        public CompletionKind Kind { get; set; }
        public string Description { get; set; }
    }

    internal sealed class ScriptAnalysis
    {
        public PreparedSource Prepared { get; set; }
        public SourceText Text { get; set; }
        public SyntaxNode Root { get; set; }
        public CSharpCompilation Compilation { get; set; }
        public SemanticModel Model { get; set; }

        // Where the caret line starts in the prepared code.
        public int LineStart { get; set; }
    }

    public sealed class ScriptCompletionList
    {
        // Column in the caret line where the name being completed starts.
        public int WordStart { get; set; }
        public IList<CompletionEntry> Entries { get; set; }

        // Names shown first and preselected, e.g. element classes for walls.OfType<.
        public IList<string> Suggestions { get; set; } = new List<string>();

        // Types for the argument of a generic method or type: the editor then closes '<' with '>'.
        public bool IsTypeArguments { get; set; }

        // Speed check, turned off: time per step, written to the debug console by CompletionController.Show.
        // public string Timing { get; set; }
    }

    // IntelliSense for scripts on the compiler's SemanticModel only, without Roslyn Features.
    // The script is wrapped exactly as for compilation, so the same references and ScriptBase members are visible.
    public static class ScriptCompletion
    {
        private static readonly string[] Keywords =
        {
            "as", "base", "bool", "break", "case", "catch", "class", "const", "continue", "default", "do", "double",
            "else", "false", "finally", "for", "foreach", "if", "in", "int", "is", "long", "nameof", "new", "null",
            "object", "out", "private", "public", "ref", "return", "static", "string", "switch", "this", "throw",
            "true", "try", "typeof", "using", "var", "void", "while"
        };

        private static readonly object Sync = new object();
        private static CSharpCompilation _baseCompilation;
        private static string _baseKey;
        private static string _requestKey;
        private static readonly Dictionary<string, Scope> Scopes = new Dictionary<string, Scope>();

        private sealed class Scope
        {
            public List<CompletionEntry> Entries;
            public List<INamedTypeSymbol> Types;
        }

        // line is 1-based, column is the caret's 0-based index in that line.
        // Without an explicit request (Ctrl+Space) the list is offered only after a '.' or on the first letter of a name.
        public static ScriptCompletionList Get(string userCode, int line, int column, bool explicitRequest, CancellationToken token)
        {
            var lineText = LineAt(userCode, line);
            if (lineText == null || column > lineText.Length)
                return null;

            var wordStart = column;
            while (wordStart > 0 && IsIdentifierChar(lineText[wordStart - 1]))
                wordStart--;

            var afterDot = wordStart > 0 && lineText[wordStart - 1] == '.';
            if (column > wordStart && char.IsDigit(lineText[wordStart]))
                return null;
            if (!explicitRequest && !afterDot && column == wordStart)
                return null;

            // var watch = Stopwatch.StartNew();
            if (!afterDot)
            {
                var typeArguments = TypeArguments(userCode, line, lineText, wordStart, token);
                if (typeArguments != null)
                {
                    typeArguments.WordStart = wordStart;
                    // typeArguments.Timing = "типы " + watch.ElapsedMilliseconds + " мс";
                    return typeArguments;
                }
            }

            var script = Analyze(userCode, line, lineText, token);
            if (script == null)
                return null;

            var position = script.LineStart + wordStart;
            var root = script.Root;
            if (IsInCommentOrLiteral(root, position))
                return null;

            // var analyzed = watch.ElapsedMilliseconds;

            List<CompletionEntry> entries;
            if (afterDot)
            {
                var members = MemberSymbols(script.Model, root, position - 1, token);
                if (members == null)
                    return null;
                entries = ToEntries(members, token);
            }
            else
            {
                if (!explicitRequest && IsDeclaredName(root, position))
                    return null;

                entries = script.Prepared.LineOffset > 0
                    ? ScriptScope(script.Model, root, position, script.Text.Lines[0].ToString(), token)
                    : ToEntries(script.Model.LookupSymbols(position), token);
                entries.AddRange(Keywords.Select(k => new CompletionEntry { Name = k, Kind = CompletionKind.Keyword }));
            }

            // var looked = watch.ElapsedMilliseconds;
            token.ThrowIfCancellationRequested();

            if (entries.Count == 0)
                return null;

            entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // var timing = "разбор " + analyzed + ", поиск " + (looked - analyzed) + ", сортировка " + (watch.ElapsedMilliseconds - looked) + " мс";
            return new ScriptCompletionList { WordStart = wordStart, Entries = entries /*, Timing = timing */ };
        }

        // The user's line, without the line break; null past the end of the script.
        internal static string LineAt(string userCode, int line)
        {
            var lines = (userCode ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            return line < 1 || line > lines.Length ? null : lines[line - 1];
        }

        // The script prepared, parsed and bound exactly as for compilation, with the caret line found in the prepared code.
        // Null on #r and hoisted using lines: they are blank in the prepared code, there is nothing to analyze there.
        internal static ScriptAnalysis Analyze(string userCode, int line, string lineText, CancellationToken token)
        {
            var prepared = ScriptSource.Prepare(userCode);
            var tree = CSharpSyntaxTree.ParseText(prepared.Code, ScriptCompiler.CreateParseOptions(), cancellationToken: token);
            var text = tree.GetText(token);

            var preparedLine = line - 1 + prepared.LineOffset;
            if (preparedLine >= text.Lines.Count || text.ToString(text.Lines[preparedLine].Span) != lineText)
                return null;

            var compilation = BaseCompilation(prepared.ExtraReferences).AddSyntaxTrees(tree);
            return new ScriptAnalysis
            {
                Prepared = prepared,
                Text = text,
                Root = tree.GetRoot(token),
                Compilation = compilation,
                Model = compilation.GetSemanticModel(tree),
                LineStart = text.Lines[preparedLine].Start
            };
        }

        // Loads the referenced assemblies, Revit API included, and the script scope,
        // so the first list in the editor does not wait for them.
        public static void Warm()
        {
            Get("Doc.", 1, 4, true, CancellationToken.None);
            Get("D", 1, 1, true, CancellationToken.None);
        }

        // References are bound once per set: a compilation derived with AddSyntaxTrees reuses them.
        // Listing the hundreds of assemblies loaded in Revit is itself slow, so it is redone only when one is added.
        private static CSharpCompilation BaseCompilation(IList<string> extraReferences)
        {
            var requestKey = AppDomain.CurrentDomain.GetAssemblies().Length + "|" + string.Join("|", extraReferences);
            lock (Sync)
            {
                if (_baseCompilation != null && _requestKey == requestKey)
                    return _baseCompilation;
            }

            var references = ScriptCompiler.BuildReferences(extraReferences, new List<CompileMessage>());
            var key = string.Join("|", references.Select(r => r.Display));

            lock (Sync)
            {
                _requestKey = requestKey;
                if (_baseCompilation == null || _baseKey != key)
                {
                    _baseCompilation = ScriptCompiler.CreateCompilation("RevitScriptCompletion", new SyntaxTree[0], references);
                    _baseKey = key;
                    Scopes.Clear();
                }
                return _baseCompilation;
            }
        }

        // Everything a script sees apart from its own locals (ScriptBase members, types and namespaces from the usings)
        // stays the same while typing, and looking it up is the slow part with hundreds of assemblies loaded in Revit.
        // So it is looked up once per set of usings (tabs may differ), and on each keystroke only the script's own names.
        private static List<CompletionEntry> ScriptScope(SemanticModel model, SyntaxNode root, int position, string usings, CancellationToken token)
        {
            var scope = CachedScope(model, position, usings, token);

            var entries = ToEntries(LocalsInScope(model, root, position), token);
            var shadowed = new HashSet<string>(entries.Select(e => e.Name));
            entries.AddRange(scope.Entries.Where(e => !shadowed.Contains(e.Name)));
            return entries;
        }

        private static Scope CachedScope(SemanticModel model, int position, string usings, CancellationToken token)
        {
            lock (Sync)
            {
                if (Scopes.TryGetValue(usings, out var cached))
                    return cached;
            }

            var all = model.LookupSymbols(position).Where(s => !IsLocal(s)).ToList();
            var scope = new Scope { Entries = ToEntries(all, token), Types = all.OfType<INamedTypeSymbol>().ToList() };

            lock (Sync)
                Scopes[usings] = scope;
            return scope;
        }

        // Inside <...> of a generic method or type (All<|, walls.OfType<W|, Dictionary<string, |) the list holds
        // only the types that fit the type parameter's constraints: Element subclasses for All<T> where T : Element.
        // The name before '<' is bound on a copy of the line cut at '<': with nothing after '>', "All<W>" followed by
        // the next line would parse as comparisons.
        private static ScriptCompletionList TypeArguments(string userCode, int line, string lineText, int wordStart, CancellationToken token)
        {
            var open = TypeArgumentListStart(lineText, wordStart, out var argumentIndex);
            if (open < 0)
                return null;

            var nameEnd = open;
            while (nameEnd > 0 && lineText[nameEnd - 1] == ' ')
                nameEnd--;
            if (nameEnd == 0 || !IsIdentifierChar(lineText[nameEnd - 1]))
                return null;

            var probeLine = lineText.Substring(0, nameEnd);
            var lines = userCode.Replace("\r\n", "\n").Split('\n');
            lines[line - 1] = probeLine;

            var script = Analyze(string.Join("\n", lines), line, probeLine, token);
            if (script == null)
                return null;

            var position = script.LineStart + nameEnd;
            if (!(script.Root.FindToken(position - 1).Parent is SimpleNameSyntax name) || name.Span.End != position)
                return null;

            var parameters = TypeParameters(script.Model, name, argumentIndex, position, token);
            if (parameters.Count == 0)
                return null;

            // Wrapped scripts reuse the cached scope, entries included, so a thousand types are not described again.
            var scope = script.Prepared.LineOffset > 0 ? CachedScope(script.Model, position, script.Text.Lines[0].ToString(), token) : null;
            var types = scope?.Types ?? script.Model.LookupSymbols(position).OfType<INamedTypeSymbol>().ToList();

            var fitting = types.Where(t => parameters.Any(p => Satisfies(script.Compilation, t, p))).ToList();
            var names = new HashSet<string>(fitting.Select(t => t.Name));
            var entries = scope != null
                ? scope.Entries.Where(e => names.Contains(e.Name) && e.Kind != CompletionKind.Namespace).ToList()
                : ToEntries(fitting, token);
            entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            return entries.Count == 0 ? null : new ScriptCompletionList
            {
                Entries = entries,
                Suggestions = PreferredTypes(script, name, parameters, fitting).Where(names.Contains).ToList(),
                IsTypeArguments = true
            };
        }

        // The '<' of the type argument list the word is in, scanning back over names, dots, commas and nested <...>.
        private static int TypeArgumentListStart(string lineText, int wordStart, out int argumentIndex)
        {
            argumentIndex = 0;
            var depth = 0;
            for (var i = wordStart - 1; i >= 0; i--)
            {
                var c = lineText[i];
                if (c == '>')
                {
                    depth++;
                }
                else if (c == '<')
                {
                    if (depth == 0)
                        return i;
                    depth--;
                }
                else if (c == ',')
                {
                    if (depth == 0)
                        argumentIndex++;
                }
                else if (!IsIdentifierChar(c) && c != '.' && c != ' ' && c != '[' && c != ']' && c != '?')
                {
                    return -1;
                }
            }

            return -1;
        }

        private static List<ITypeParameterSymbol> TypeParameters(SemanticModel model, SimpleNameSyntax name, int index, int position, CancellationToken token)
        {
            var candidates = new List<ISymbol>();
            INamespaceOrTypeSymbol container = null;

            if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name)
            {
                candidates.AddRange(model.GetMemberGroup(access, token));
                container = model.GetSymbolInfo(access.Expression, token).Symbol as INamespaceOrTypeSymbol;
            }
            else if (name.Parent is QualifiedNameSyntax qualified && qualified.Right == name)
            {
                container = model.GetSymbolInfo(qualified.Left, token).Symbol as INamespaceOrTypeSymbol;
            }
            else
            {
                candidates.AddRange(model.GetMemberGroup(name, token));
            }

            // Generic types too: List<, Dictionary<string, .
            candidates.AddRange(model.LookupSymbols(position, container, name.Identifier.ValueText).OfType<INamedTypeSymbol>());

            return candidates
                .Select(c => c is IMethodSymbol method && method.Arity > index ? method.TypeParameters[index]
                    : c is INamedTypeSymbol type && type.Arity > index ? type.TypeParameters[index]
                    : null)
                .Where(p => p != null)
                .ToList();
        }

        private static bool Satisfies(CSharpCompilation compilation, INamedTypeSymbol type, ITypeParameterSymbol parameter)
        {
            if (type.IsStatic || type.TypeKind == TypeKind.Error)
                return false;
            if (parameter.HasReferenceTypeConstraint && !type.IsReferenceType)
                return false;
            if (parameter.HasValueTypeConstraint && !type.IsValueType)
                return false;
            if (parameter.HasConstructorConstraint && !type.IsValueType
                && (type.IsAbstract || !type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)))
                return false;

            // A constraint on another type parameter (where T : U) cannot be checked here and is taken as met.
            return parameter.ConstraintTypes.All(constraint =>
                constraint.TypeKind == TypeKind.TypeParameter || compilation.ClassifyConversion(type, constraint).IsImplicit);
        }

        // For an unconstrained parameter called on a collection (walls.OfType<, elements.Cast<) the element type
        // and its subclasses come first: for a list of Element that is Wall, Floor, FamilyInstance and so on.
        private static IEnumerable<string> PreferredTypes(ScriptAnalysis script, SimpleNameSyntax name, List<ITypeParameterSymbol> parameters, List<INamedTypeSymbol> fitting)
        {
            if (parameters.Any(p => p.ConstraintTypes.Length > 0) || !(name.Parent is MemberAccessExpressionSyntax access))
                return Enumerable.Empty<string>();

            var element = ElementType(script.Model.GetTypeInfo(access.Expression).Type);
            if (element == null || element.SpecialType == SpecialType.System_Object)
                return Enumerable.Empty<string>();

            return fitting
                .Where(t => script.Compilation.ClassifyConversion(t, element).IsImplicit)
                .Select(t => t.Name)
                .Distinct()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        }

        private static ITypeSymbol ElementType(ITypeSymbol collection)
        {
            if (!(collection is INamedTypeSymbol named))
                return null;

            var enumerable = new[] { named }.Concat(named.AllInterfaces)
                .FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
            return enumerable?.TypeArguments[0];
        }

        // The script's own variables, parameters and local functions visible at the position.
        internal static List<ISymbol> LocalsInScope(SemanticModel model, SyntaxNode root, int position)
        {
            return DeclaredNames(root)
                .SelectMany(name => model.LookupSymbols(position, name: name))
                .Where(IsLocal)
                .ToList();
        }

        private static bool IsLocal(ISymbol symbol)
        {
            return symbol is ILocalSymbol
                || symbol is IParameterSymbol
                || symbol is IRangeVariableSymbol
                || symbol is IMethodSymbol method && method.MethodKind == MethodKind.LocalFunction;
        }

        // Names the script declares anywhere; LookupSymbols then keeps those in scope at the caret.
        private static HashSet<string> DeclaredNames(SyntaxNode root)
        {
            var names = new HashSet<string>();
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case VariableDeclaratorSyntax variable: names.Add(variable.Identifier.ValueText); break;
                    case ParameterSyntax parameter: names.Add(parameter.Identifier.ValueText); break;
                    case ForEachStatementSyntax forEach: names.Add(forEach.Identifier.ValueText); break;
                    case SingleVariableDesignationSyntax designation: names.Add(designation.Identifier.ValueText); break;
                    case CatchDeclarationSyntax catchDeclaration: names.Add(catchDeclaration.Identifier.ValueText); break;
                    case LocalFunctionStatementSyntax function: names.Add(function.Identifier.ValueText); break;
                    case FromClauseSyntax from: names.Add(from.Identifier.ValueText); break;
                    case LetClauseSyntax let: names.Add(let.Identifier.ValueText); break;
                }
            }

            names.Remove(string.Empty);
            return names;
        }

        private static List<CompletionEntry> ToEntries(IEnumerable<ISymbol> symbols, CancellationToken token)
        {
            return symbols
                .Where(IsShown)
                .GroupBy(s => s.Name)
                .Select(group => ToEntry(group.First(), group.Count(), token))
                .ToList();
        }

        private static IEnumerable<ISymbol> MemberSymbols(SemanticModel model, SyntaxNode root, int dotPosition, CancellationToken token)
        {
            var dot = root.FindToken(dotPosition);
            if (!dot.IsKind(SyntaxKind.DotToken))
                return null;

            switch (dot.Parent)
            {
                case MemberAccessExpressionSyntax access:
                    return MembersOf(model, access.Expression, dotPosition, token);
                case QualifiedNameSyntax qualified:
                    return MembersOf(model, qualified.Left, dotPosition, token);
                case MemberBindingExpressionSyntax binding:
                    var conditional = binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>();
                    return conditional == null ? null : MembersOf(model, conditional.Expression, dotPosition, token);
                default:
                    return null;
            }
        }

        private static IEnumerable<ISymbol> MembersOf(SemanticModel model, ExpressionSyntax expression, int position, CancellationToken token)
        {
            if (expression.IsKind(SyntaxKind.NumericLiteralExpression))
                return null;

            var symbol = model.GetSymbolInfo(expression, token).Symbol;

            if (symbol is INamespaceSymbol ns)
                return ns.GetMembers().Where(m => model.IsAccessible(position, m));

            if (symbol is INamedTypeSymbol staticType)
                return model.LookupStaticMembers(position, staticType);

            var type = model.GetTypeInfo(expression, token).Type;
            if (type == null || type.TypeKind == TypeKind.Error)
                return null;

            // x?.  works on the value of a Nullable<T>
            if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && type is INamedTypeSymbol nullable)
                type = nullable.TypeArguments[0];

            return model.LookupSymbols(position, type, includeReducedExtensionMethods: true)
                .Where(s => !s.IsStatic && !(s is ITypeSymbol));
        }

        internal static bool IsShown(ISymbol symbol)
        {
            return symbol.CanBeReferencedByName
                && !symbol.IsImplicitlyDeclared
                && !symbol.Name.StartsWith("__", StringComparison.Ordinal);
        }

        private static bool IsInCommentOrLiteral(SyntaxNode root, int position)
        {
            if (position == 0)
                return false;

            var trivia = root.FindTrivia(position - 1);
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                return true;

            var token = root.FindToken(position - 1);
            return token.Span.Contains(position - 1) && (token.IsKind(SyntaxKind.StringLiteralToken)
                || token.IsKind(SyntaxKind.CharacterLiteralToken)
                || token.IsKind(SyntaxKind.InterpolatedStringTextToken));
        }

        // A name being declared (var x, Wall wall, a parameter) gets no list while typing.
        private static bool IsDeclaredName(SyntaxNode root, int position)
        {
            var token = root.FindToken(position);
            if (!token.IsKind(SyntaxKind.IdentifierToken) || token.SpanStart != position)
                return false;

            switch (token.Parent)
            {
                case VariableDeclaratorSyntax declarator: return declarator.Identifier == token;
                case ParameterSyntax parameter: return parameter.Identifier == token;
                case ForEachStatementSyntax forEach: return forEach.Identifier == token;
                case SingleVariableDesignationSyntax _: return true;
                case CatchDeclarationSyntax catchDeclaration: return catchDeclaration.Identifier == token;
                case MethodDeclarationSyntax method: return method.Identifier == token;
                case PropertyDeclarationSyntax property: return property.Identifier == token;
                case BaseTypeDeclarationSyntax type: return type.Identifier == token;
                default: return false;
            }
        }

        internal static CompletionEntry ToEntry(ISymbol symbol, int overloads, CancellationToken token)
        {
            var description = symbol is INamespaceSymbol || symbol is ITypeSymbol
                ? KindWord(symbol) + " " + symbol.ToDisplayString()
                : symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

            if (overloads > 1)
                description += " (+" + (overloads - 1) + " перегр.)";

            var summary = SymbolDocumentation.Summary(symbol, token);
            if (summary != null)
                description += "\n" + summary;

            return new CompletionEntry { Name = symbol.Name, Kind = KindOf(symbol), Description = description };
        }

        internal static CompletionKind KindOf(ISymbol symbol)
        {
            switch (symbol)
            {
                case INamespaceSymbol _: return CompletionKind.Namespace;
                case INamedTypeSymbol type:
                    switch (type.TypeKind)
                    {
                        case TypeKind.Interface: return CompletionKind.Interface;
                        case TypeKind.Struct: return CompletionKind.Struct;
                        case TypeKind.Enum: return CompletionKind.Enum;
                        case TypeKind.Delegate: return CompletionKind.Delegate;
                        default: return CompletionKind.Class;
                    }
                case IMethodSymbol _: return CompletionKind.Method;
                case IPropertySymbol _: return CompletionKind.Property;
                case IEventSymbol _: return CompletionKind.Event;
                case IFieldSymbol field:
                    return field.ContainingType?.TypeKind == TypeKind.Enum ? CompletionKind.EnumMember : CompletionKind.Field;
                case ILocalSymbol _:
                case IParameterSymbol _:
                case IRangeVariableSymbol _:
                    return CompletionKind.Local;
                default: return CompletionKind.Class;
            }
        }

        private static string KindWord(ISymbol symbol)
        {
            switch (KindOf(symbol))
            {
                case CompletionKind.Namespace: return "namespace";
                case CompletionKind.Interface: return "interface";
                case CompletionKind.Struct: return "struct";
                case CompletionKind.Enum: return "enum";
                case CompletionKind.Delegate: return "delegate";
                default: return "class";
            }
        }

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
