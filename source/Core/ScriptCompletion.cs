using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
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

    public sealed class ScriptCompletionList
    {
        // Column in the caret line where the name being completed starts.
        public int WordStart { get; set; }
        public IList<CompletionEntry> Entries { get; set; }
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

        // line is 1-based, column is the caret's 0-based index in that line.
        // Without an explicit request (Ctrl+Space) the list is offered only after a '.' or on the first letter of a name.
        public static ScriptCompletionList Get(string userCode, int line, int column, bool explicitRequest, CancellationToken token)
        {
            var lines = (userCode ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            if (line < 1 || line > lines.Length || column > lines[line - 1].Length)
                return null;

            var lineText = lines[line - 1];
            var wordStart = column;
            while (wordStart > 0 && IsIdentifierChar(lineText[wordStart - 1]))
                wordStart--;

            var afterDot = wordStart > 0 && lineText[wordStart - 1] == '.';
            if (column > wordStart && char.IsDigit(lineText[wordStart]))
                return null;
            if (!explicitRequest && !afterDot && column == wordStart)
                return null;

            var prepared = ScriptSource.Prepare(userCode);
            var tree = CSharpSyntaxTree.ParseText(prepared.Code, ScriptCompiler.CreateParseOptions(), cancellationToken: token);
            var text = tree.GetText(token);

            // #r and hoisted using lines are blank in the prepared code, there is nothing to complete on them.
            var preparedLine = line - 1 + prepared.LineOffset;
            if (preparedLine >= text.Lines.Count || text.ToString(text.Lines[preparedLine].Span) != lineText)
                return null;

            var position = text.Lines[preparedLine].Start + wordStart;
            var root = tree.GetRoot(token);
            if (IsInCommentOrLiteral(root, position))
                return null;

            var compilation = BaseCompilation(prepared.ExtraReferences).AddSyntaxTrees(tree);
            var model = compilation.GetSemanticModel(tree);

            IEnumerable<ISymbol> symbols;
            if (afterDot)
            {
                symbols = MemberSymbols(model, root, position - 1, token);
            }
            else
            {
                if (!explicitRequest && IsDeclaredName(root, position))
                    return null;
                symbols = model.LookupSymbols(position);
            }

            if (symbols == null)
                return null;

            token.ThrowIfCancellationRequested();

            var entries = symbols
                .Where(IsShown)
                .GroupBy(s => s.Name)
                .Select(group => ToEntry(group.First(), group.Count()))
                .ToList();

            if (!afterDot)
                entries.AddRange(Keywords.Select(k => new CompletionEntry { Name = k, Kind = CompletionKind.Keyword }));

            if (entries.Count == 0)
                return null;

            entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new ScriptCompletionList { WordStart = wordStart, Entries = entries };
        }

        // Loads the referenced assemblies, Revit API included, so the first list in the editor does not wait for them.
        public static void Warm()
        {
            Get("Doc.", 1, 4, true, CancellationToken.None);
        }

        // References are bound once per set: a compilation derived with AddSyntaxTrees reuses them.
        private static CSharpCompilation BaseCompilation(IList<string> extraReferences)
        {
            var references = ScriptCompiler.BuildReferences(extraReferences, new List<CompileMessage>());
            var key = string.Join("|", references.Select(r => r.Display));

            lock (Sync)
            {
                if (_baseCompilation == null || _baseKey != key)
                {
                    _baseCompilation = ScriptCompiler.CreateCompilation("RevitScriptCompletion", new SyntaxTree[0], references);
                    _baseKey = key;
                }
                return _baseCompilation;
            }
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

        private static bool IsShown(ISymbol symbol)
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

        private static CompletionEntry ToEntry(ISymbol symbol, int overloads)
        {
            var description = symbol is INamespaceSymbol || symbol is ITypeSymbol
                ? KindWord(symbol) + " " + symbol.ToDisplayString()
                : symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

            if (overloads > 1)
                description += "\n(перегрузок: " + overloads + ")";

            return new CompletionEntry { Name = symbol.Name, Kind = KindOf(symbol), Description = description };
        }

        private static CompletionKind KindOf(ISymbol symbol)
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
