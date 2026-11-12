using BIMPlugins.ExtStorage.Extensions;
using Microsoft.CodeAnalysis;
using RevitDevKit.Core.Output;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace RevitDevKit.Core.IntelliSense
{
    // An XML documentation file for a metadata reference. Roslyn's own XmlDocumentationProvider lives in
    // Microsoft.CodeAnalysis.Workspaces, which DevKit does not ship, so the file is read here: once, on the first tooltip.
    internal sealed class XmlDocumentation : DocumentationProvider
    {
        private readonly string _path;
        private readonly object _sync = new object();
        private Dictionary<string, string> _members;

        public XmlDocumentation(string path)
        {
            _path = path;
        }

        protected override string GetDocumentationForSymbol(string documentationMemberID, CultureInfo preferredCulture,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return Members().TryGetValue(documentationMemberID, out var xml) ? xml : string.Empty;
        }

        private Dictionary<string, string> Members()
        {
            lock (_sync)
            {
                if (_members != null)
                    return _members;

                var members = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                    using (var reader = XmlReader.Create(_path, settings))
                    {
                        while (!reader.EOF)
                        {
                            if (reader.NodeType == XmlNodeType.Element && reader.Name == "member")
                            {
                                var name = reader.GetAttribute("name");
                                var xml = reader.ReadOuterXml();
                                if (name != null)
                                    members[name] = xml;
                            }
                            else
                            {
                                reader.Read();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ConsoleOutput.Debug("Не удалось прочитать документацию " + _path + ": " + ex.Message);
                }

                return _members = members;
            }
        }

        public override bool Equals(object obj) => obj is XmlDocumentation other && other._path == _path;

        public override int GetHashCode() => _path.GetHashCode();
    }

    // Plain text from XML documentation comments, for the editor's tooltips.
    // Empty when the assembly comes without its .xml file.
    internal static class SymbolDocumentation
    {
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        public static string Summary(ISymbol symbol, CancellationToken token)
        {
            return Text(Comment(symbol, token)?.Element("summary"));
        }

        public static string Parameter(IMethodSymbol method, string name, CancellationToken token)
        {
            var parameter = Comment(method, token)?.Elements("param")
                .FirstOrDefault(p => (string)p.Attribute("name") == name);
            return Text(parameter);
        }

        private static XElement Comment(ISymbol symbol, CancellationToken token)
        {
            // Docs belong to the declaration: an extension method called on an instance, or a generic method with type arguments, has none of its own.
            if (symbol is IMethodSymbol method && method.ReducedFrom != null)
                symbol = method.ReducedFrom;
            symbol = symbol.OriginalDefinition;

            try
            {
                var xml = symbol.GetDocumentationCommentXml(cancellationToken: token);
                return xml.IsNullOrWhiteSpace() ? null : XElement.Parse(xml);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return null;
            }
        }

        private static string Text(XElement element)
        {
            if (element == null)
                return null;

            var text = new StringBuilder();
            foreach (var node in element.Nodes())
            {
                if (node is XText plain)
                {
                    text.Append(plain.Value);
                }
                else if (node is XElement child)
                {
                    // <see cref="T:Autodesk.Revit.DB.Document"/> reads as Document, <paramref name="x"/> as x.
                    var reference = (string)child.Attribute("cref") ?? (string)child.Attribute("name") ?? (string)child.Attribute("langword");
                    text.Append(reference != null ? ShortName(reference) : child.Value);
                }
            }

            var result = Spaces.Replace(text.ToString(), " ").Trim();
            return result.Length == 0 ? null : result;
        }

        private static string ShortName(string reference)
        {
            var name = reference.Length > 2 && reference[1] == ':' ? reference.Substring(2) : reference;
            var parameters = name.IndexOf('(');
            if (parameters >= 0)
                name = name.Substring(0, parameters);

            name = name.Substring(name.LastIndexOf('.') + 1);
            var generic = name.IndexOf('`');
            return generic >= 0 ? name.Substring(0, generic) : name;
        }
    }
}
