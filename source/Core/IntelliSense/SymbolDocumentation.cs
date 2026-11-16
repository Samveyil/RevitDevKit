using BIMPlugins.ExtStorage.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RevitDevKit.Core.Compiler;
using RevitDevKit.Core.Output;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        // Package docs DevKit carries inside its DLL, for references whose XML file is not next to them.
        private const string ResourcePrefix = "RevitDevKit.Docs.";

        private readonly string _path;
        private readonly Func<Stream> _open;
        private readonly object _sync = new object();
        private Dictionary<string, string> _members;

        public XmlDocumentation(string path) : this(path, () => File.OpenRead(path))
        {
        }

        private XmlDocumentation(string path, Func<Stream> open)
        {
            _path = path;
            _open = open;
        }

        // The XML file embedded in the DLL under its file name, such as BIMPlugins.ExtStorage.xml; null if there is none.
        public static XmlDocumentation Embedded(string fileName)
        {
            var assembly = typeof(XmlDocumentation).Assembly;
            var name = ResourcePrefix + fileName;
            return assembly.GetManifestResourceInfo(name) == null
                ? null
                : new XmlDocumentation(name, () => assembly.GetManifestResourceStream(name));
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
                    using (var stream = _open())
                    using (var reader = XmlReader.Create(stream, settings))
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

    // Docs of DevKit's own script API (ScriptBase), for tooltips without RevitDevKit.xml next to the DLL.
    // The compiler writes doc comments only to that separate file, and it cannot go into the DLL being built,
    // so the DLL carries ScriptBase.cs itself as a resource. On the first tooltip the source is parsed and bound
    // against the scripts' references, and every member's comment is kept under its documentation ID.
    internal sealed class EmbeddedDocumentation : DocumentationProvider
    {
        public const string ResourceName = "RevitDevKit.ScriptBase.cs";

        private readonly Func<IEnumerable<MetadataReference>> _references;
        private readonly object _sync = new object();
        private Dictionary<string, string> _members;

        public EmbeddedDocumentation(Func<IEnumerable<MetadataReference>> references)
        {
            _references = references;
        }

        public static bool IsAvailable => typeof(EmbeddedDocumentation).Assembly.GetManifestResourceInfo(ResourceName) != null;

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
                    string source;
                    using (var stream = typeof(EmbeddedDocumentation).Assembly.GetManifestResourceStream(ResourceName))
                    using (var reader = new StreamReader(stream))
                        source = reader.ReadToEnd();

                    var tree = CSharpSyntaxTree.ParseText(source,
                        ScriptCompiler.CreateParseOptions().WithDocumentationMode(DocumentationMode.Parse));
                    var compilation = ScriptCompiler.CreateCompilation("RevitDevKitDocs", new[] { tree }, _references());
                    var model = compilation.GetSemanticModel(tree);

                    foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
                    {
                        var symbol = model.GetDeclaredSymbol(declaration);
                        var id = symbol?.GetDocumentationCommentId();
                        var xml = symbol?.GetDocumentationCommentXml();
                        if (!id.IsNullOrEmpty() && !xml.IsNullOrWhiteSpace())
                            members[id] = xml.Contains("<inheritdoc") ? ExpandInheritdoc(xml, compilation) : xml;
                    }
                }
                catch (Exception ex)
                {
                    ConsoleOutput.Debug("Не удалось прочитать встроенную документацию DevKit: " + ex.Message);
                }

                return _members = members;
            }
        }

        // <inheritdoc cref="..."/> takes the tags of the cref's docs that the member does not have itself, as Visual Studio
        // shows it. The compiler leaves the tag as it is, and the cref's docs come from its assembly's XML (see ScriptCompiler).
        private static string ExpandInheritdoc(string xml, Compilation compilation)
        {
            var member = XElement.Parse(xml);
            foreach (var inherit in member.Elements("inheritdoc").ToList())
            {
                var cref = (string)inherit.Attribute("cref");
                inherit.Remove();

                var target = cref == null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, compilation);
                var inherited = target?.GetDocumentationCommentXml();
                if (inherited.IsNullOrWhiteSpace())
                    continue;

                foreach (var element in XElement.Parse(inherited).Elements())
                {
                    var name = (string)element.Attribute("name");
                    if (!member.Elements(element.Name).Any(e => (string)e.Attribute("name") == name))
                        member.Add(element);
                }
            }

            return member.ToString();
        }

        public override bool Equals(object obj) => ReferenceEquals(this, obj);

        public override int GetHashCode() => ResourceName.GetHashCode();
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
