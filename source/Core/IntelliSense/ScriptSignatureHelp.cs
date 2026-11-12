using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RevitDevKit.Core.IntelliSense
{
    public sealed class SignatureParameter
    {
        public string Name { get; set; }

        // Type and name, as in the declaration: "Document document".
        public string Display { get; set; }
        public string Documentation { get; set; }
    }

    public sealed class Signature
    {
        // "FilteredElementCollector(" or "Element Document.GetElement(".
        public string Prefix { get; set; }
        public IList<SignatureParameter> Parameters { get; set; }
        public string Documentation { get; set; }

        // The last parameter is params T[]: every further argument goes to it.
        public bool HasParamsArray { get; set; }
    }

    public sealed class ScriptSignatures
    {
        public IList<Signature> Overloads { get; set; }
        public int SelectedOverload { get; set; }
        public int ArgumentIndex { get; set; }

        // The argument list in the user's code: 1-based lines, 0-based columns inside the parentheses.
        public int StartLine { get; set; }
        public int StartColumn { get; set; }
        public int EndLine { get; set; }
        public int EndColumn { get; set; }

        // Variables and properties in scope that fit the current argument, e.g. Doc for a Document parameter.
        public IList<string> Suggestions { get; set; }
    }

    // Overloads of the method or constructor being called at the caret, as Visual Studio shows after '(' and ','.
    public static class ScriptSignatureHelp
    {
        public static ScriptSignatures Get(string userCode, int line, int column, CancellationToken token)
        {
            var lineText = ScriptCompletion.LineAt(userCode, line);
            if (lineText == null || column > lineText.Length)
                return null;

            var script = ScriptCompletion.Analyze(userCode, line, lineText, token);
            if (script == null)
                return null;

            var position = script.LineStart + column;
            var arguments = ArgumentListAt(script.Root, position);
            if (arguments == null)
                return null;

            var model = script.Model;
            // Shortest first, as in Visual Studio: with no arguments typed yet the simplest overload is shown.
            var overloads = Overloads(model, arguments, position, token).OrderBy(m => m.Parameters.Length).ToList();
            if (overloads.Count == 0)
                return null;

            var argumentIndex = arguments.Arguments.GetSeparators().Count(s => s.SpanStart < position);
            var resolved = model.GetSymbolInfo(arguments.Parent, token).Symbol;
            var selected = overloads.FindIndex(m => m.Equals(resolved));
            if (selected < 0)
                selected = Math.Max(0, overloads.FindIndex(m => ParameterType(m, argumentIndex) != null));

            var start = UserPosition(script, arguments.OpenParenToken.Span.End);
            var end = UserPosition(script, arguments.CloseParenToken.IsMissing ? position : arguments.CloseParenToken.SpanStart);

            return new ScriptSignatures
            {
                Overloads = overloads.Select(m => ToSignature(m, token)).ToList(),
                SelectedOverload = selected,
                ArgumentIndex = argumentIndex,
                StartLine = start.Line,
                StartColumn = start.Character,
                EndLine = end.Line,
                EndColumn = end.Character,
                Suggestions = Suggestions(script, overloads, argumentIndex, position, token)
            };
        }

        // The innermost call or 'new' whose parentheses contain the position.
        private static ArgumentListSyntax ArgumentListAt(SyntaxNode root, int position)
        {
            var token = root.FindToken(Math.Max(0, position - 1));
            foreach (var list in token.Parent.AncestorsAndSelf().OfType<ArgumentListSyntax>())
            {
                if (position <= list.OpenParenToken.SpanStart)
                    continue;
                if (!list.CloseParenToken.IsMissing && position > list.CloseParenToken.SpanStart)
                    continue;
                if (list.Parent is InvocationExpressionSyntax || list.Parent is ObjectCreationExpressionSyntax)
                    return list;
            }

            return null;
        }

        private static List<IMethodSymbol> Overloads(SemanticModel model, ArgumentListSyntax arguments, int position, CancellationToken token)
        {
            switch (arguments.Parent)
            {
                case InvocationExpressionSyntax invocation:
                    return model.GetMemberGroup(invocation.Expression, token).OfType<IMethodSymbol>().ToList();

                case ObjectCreationExpressionSyntax creation:
                    var type = model.GetTypeInfo(creation, token).Type as INamedTypeSymbol
                               ?? model.GetSymbolInfo(creation.Type, token).Symbol as INamedTypeSymbol;
                    return type == null
                        ? new List<IMethodSymbol>()
                        : type.InstanceConstructors.Where(c => model.IsAccessible(position, c)).ToList();

                default:
                    return new List<IMethodSymbol>();
            }
        }

        private static Signature ToSignature(IMethodSymbol method, CancellationToken token)
        {
            var format = SymbolDisplayFormat.MinimallyQualifiedFormat;
            string prefix;
            if (method.MethodKind == MethodKind.Constructor)
            {
                prefix = method.ContainingType.ToDisplayString(format);
            }
            else
            {
                var owner = method.ReducedFrom != null ? method.ReceiverType : method.ContainingType;
                prefix = method.ReturnType.ToDisplayString(format) + " " + owner.ToDisplayString(format) + "." + method.Name;
                if (method.IsGenericMethod)
                    prefix += "<" + string.Join(", ", method.TypeArguments.Select(t => t.ToDisplayString(format))) + ">";
            }

            return new Signature
            {
                Prefix = prefix + "(",
                Parameters = method.Parameters.Select(p => new SignatureParameter
                {
                    Name = p.Name,
                    Display = p.ToDisplayString(format),
                    Documentation = SymbolDocumentation.Parameter(method, p.Name, token)
                }).ToList(),
                HasParamsArray = method.Parameters.Length > 0 && method.Parameters[method.Parameters.Length - 1].IsParams,
                Documentation = SymbolDocumentation.Summary(method, token)
            };
        }

        // The type an argument at this index must have, or null when the overload takes fewer arguments.
        private static ITypeSymbol ParameterType(IMethodSymbol method, int index)
        {
            var parameters = method.Parameters;
            if (parameters.Length == 0)
                return null;

            var last = parameters[parameters.Length - 1];
            if (last.IsParams && index >= parameters.Length - 1)
                return (last.Type as IArrayTypeSymbol)?.ElementType;

            return index < parameters.Length ? parameters[index].Type : null;
        }

        // Locals and the script's properties (Doc, UIDoc, ActiveView, S0...) that convert to the argument's type,
        // plus the enum itself for an enum parameter. Parameters of type object or T would match everything, so they get none.
        private static IList<string> Suggestions(ScriptAnalysis script, List<IMethodSymbol> overloads, int argumentIndex, int position, CancellationToken token)
        {
            var expected = overloads
                .Select(m => ParameterType(m, argumentIndex))
                .Where(t => t != null && t.SpecialType != SpecialType.System_Object
                            && t.TypeKind != TypeKind.TypeParameter && t.TypeKind != TypeKind.Error)
                .ToList();

            var names = new List<string>();
            if (expected.Count == 0)
                return names;

            void Consider(ISymbol symbol, ITypeSymbol type)
            {
                if (type != null && !names.Contains(symbol.Name)
                    && expected.Any(e => script.Compilation.ClassifyConversion(type, e).IsImplicit))
                    names.Add(symbol.Name);
            }

            foreach (var local in ScriptCompletion.LocalsInScope(script.Model, script.Root, position))
                Consider(local, TypeOf(local));

            var container = script.Model.GetEnclosingSymbol(position, token)?.ContainingType;
            for (var type = container; type != null; type = type.BaseType)
            {
                foreach (var member in type.GetMembers())
                {
                    if ((member is IPropertySymbol property && !property.IsIndexer || member is IFieldSymbol)
                        && ScriptCompletion.IsShown(member) && script.Model.IsAccessible(position, member))
                        Consider(member, TypeOf(member));
                }
            }

            foreach (var enumType in expected.Where(t => t.TypeKind == TypeKind.Enum))
            {
                if (!names.Contains(enumType.Name))
                    names.Add(enumType.Name);
            }

            return names;
        }

        private static ITypeSymbol TypeOf(ISymbol symbol)
        {
            switch (symbol)
            {
                case ILocalSymbol local: return local.Type;
                case IParameterSymbol parameter: return parameter.Type;
                case IPropertySymbol property: return property.Type;
                case IFieldSymbol field: return field.Type;
                default: return null;
            }
        }

        // Maps a position in the prepared code back to the user's line and column.
        private static LinePosition UserPosition(ScriptAnalysis script, int position)
        {
            var linePosition = script.Text.Lines.GetLinePosition(position);
            return new LinePosition(linePosition.Line - script.Prepared.LineOffset + 1, linePosition.Character);
        }
    }
}
