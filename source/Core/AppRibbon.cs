using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Adw = Autodesk.Windows;

namespace RevitDevKit.Core
{
    // What a hosted add-in added to the ribbon, so a reload can remove it and create it again.
    // The API cannot delete ribbon items and refuses duplicate names, so removal also clears
    // the private name registries of RevitAPIUI (same fields in Revit 2019–2024).
    internal sealed class RibbonFootprint
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly FieldInfo PanelsField =
            typeof(UIApplication).GetField("m_ItemsNameDictionary", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly FieldInfo PanelItemsField = typeof(RibbonPanel).GetField("m_ItemsNameDictionary", Private);
        private static readonly FieldInfo AdwPanelField = typeof(RibbonPanel).GetField("m_RibbonPanel", Private);

        private readonly List<(string Tab, string Name, RibbonPanel Panel)> _panels = new List<(string, string, RibbonPanel)>();
        private readonly List<(RibbonPanel Panel, RibbonItem Item)> _items = new List<(RibbonPanel, RibbonItem)>();
        private readonly List<string> _tabs = new List<string>();

        public int PanelCount => _panels.Count;

        public sealed class State
        {
            internal Dictionary<string, Dictionary<string, HashSet<string>>> Panels { get; } =
                new Dictionary<string, Dictionary<string, HashSet<string>>>();

            internal HashSet<string> Tabs { get; } = new HashSet<string>();
        }

        public static State Snapshot()
        {
            var state = new State();

            foreach (var tab in Registry())
                state.Panels[tab.Key] = tab.Value.ToDictionary(p => p.Key, p => new HashSet<string>(ItemNames(p.Value)));

            foreach (var tab in AdwTabs())
                state.Tabs.Add(tab.Id);

            return state;
        }

        public static RibbonFootprint Diff(State before)
        {
            var result = new RibbonFootprint();

            foreach (var tab in Registry())
            {
                before.Panels.TryGetValue(tab.Key, out var oldPanels);

                foreach (var panel in tab.Value)
                {
                    if (oldPanels == null || !oldPanels.TryGetValue(panel.Key, out var oldItems))
                    {
                        result._panels.Add((tab.Key, panel.Key, panel.Value));
                        continue;
                    }

                    foreach (var item in panel.Value.GetItems().Where(i => !oldItems.Contains(i.Name)))
                        result._items.Add((panel.Value, item));
                }
            }

            result._tabs.AddRange(AdwTabs().Select(t => t.Id).Where(id => !before.Tabs.Contains(id)));
            return result;
        }

        public IEnumerable<PushButton> PushButtons()
        {
            var items = _panels.SelectMany(p => p.Panel.GetItems()).Concat(_items.Select(i => i.Item));

            foreach (var item in items)
            {
                if (item is PulldownButton pulldown)
                {
                    foreach (var child in pulldown.GetItems())
                        yield return child;
                }
                else if (item is PushButton button)
                {
                    yield return button;
                }
            }
        }

        public void Remove()
        {
            foreach (var (panel, item) in _items)
            {
                Try(() => item.Visible = false);
                Try(() => PanelItems(panel)?.Remove(item.Name));
            }

            var registry = Registry();

            foreach (var (tab, name, panel) in _panels)
            {
                Try(() => panel.Visible = false);
                Try(() => DetachFromTab(AdwPanelField?.GetValue(panel) as Adw.RibbonPanel));

                if (registry.TryGetValue(tab, out var panels))
                {
                    panels.Remove(name);
                    if (panels.Count == 0 && _tabs.Contains(tab))
                        registry.Remove(tab);
                }
            }

            foreach (var id in _tabs)
            {
                Try(() =>
                {
                    var tab = AdwTabs().FirstOrDefault(t => t.Id == id);
                    if (tab != null) Adw.ComponentManager.Ribbon.Tabs.Remove(tab);
                });
            }

            _items.Clear();
            _panels.Clear();
            _tabs.Clear();
        }

        private static Dictionary<string, Dictionary<string, RibbonPanel>> Registry()
        {
            if (PanelsField?.GetValue(null) is Dictionary<string, Dictionary<string, RibbonPanel>> registry)
                return registry;

            return new Dictionary<string, Dictionary<string, RibbonPanel>>();
        }

        private static IEnumerable<string> ItemNames(RibbonPanel panel)
        {
            try { return panel.GetItems().Select(i => i.Name).ToList(); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static Dictionary<string, RibbonItem> PanelItems(RibbonPanel panel) =>
            PanelItemsField?.GetValue(panel) as Dictionary<string, RibbonItem>;

        private static IEnumerable<Adw.RibbonTab> AdwTabs() =>
            Adw.ComponentManager.Ribbon?.Tabs.ToList() ?? new List<Adw.RibbonTab>();

        private static void DetachFromTab(Adw.RibbonPanel panel)
        {
            if (panel == null) return;

            foreach (var tab in AdwTabs())
            {
                if (tab.Panels.Remove(panel)) return;
            }
        }

        private static void Try(Action action)
        {
            try { action(); }
            catch (Exception ex) { ConsoleOutput.Debug("Лента: " + ex.Message); }
        }
    }

    // Revit loads a button's command from the DLL path stored in it and never loads that file
    // again. So buttons of a hosted add-in point to a small generated proxy assembly instead,
    // and every proxy class forwards to the command type of the currently loaded copy.
    public static class ProxyCommands
    {
        private static readonly List<Binding> Bindings = new List<Binding>();
        private static int _generation;

        private sealed class Binding
        {
            public Type Command;
            public Type Availability;
            public IExternalCommandAvailability AvailabilityInstance;
        }

        internal static int Rebind(IEnumerable<PushButton> buttons, AppHost.ShadowFolder folder, string originalDirectory)
        {
            var targets = new List<(PushButton Button, int Id, bool HasAvailability)>();

            foreach (var button in buttons)
            {
                var path = ShadowPath(button.AssemblyName, folder, originalDirectory);
                if (path == null) continue;

                try
                {
                    var assembly = folder.Load(path);
                    var command = assembly.GetType(button.ClassName);
                    if (command == null)
                    {
                        ConsoleOutput.Warning("Кнопка «" + button.Name + "»: класс " + button.ClassName + " не найден в " + Path.GetFileName(path) + ".");
                        continue;
                    }

                    var availability = string.IsNullOrEmpty(button.AvailabilityClassName)
                        ? null
                        : assembly.GetType(button.AvailabilityClassName);

                    Bindings.Add(new Binding { Command = command, Availability = availability });
                    targets.Add((button, Bindings.Count - 1, availability != null));
                }
                catch (Exception ex)
                {
                    ConsoleOutput.Warning("Кнопка «" + button.Name + "» не перенаправлена: " + ex.Message);
                }
            }

            if (targets.Count == 0) return 0;

            // The name must never repeat: LoadFrom returns an already loaded assembly with the same name.
            var ns = "RevitDevKit.Proxies.G" + ++_generation + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var proxyPath = Path.Combine(folder.Directory, ns + ".dll");

            Emit(ns, targets.Select(t => t.Id), proxyPath);

            foreach (var (button, id, hasAvailability) in targets)
            {
                button.AssemblyName = proxyPath;
                button.ClassName = ns + "." + ClassName(id);
                if (hasAvailability)
                    button.AvailabilityClassName = ns + "." + ClassName(id);
            }

            return targets.Count;
        }

        public static Result Execute(int id, ExternalCommandData data, ref string message, ElementSet elements)
        {
            var binding = Bindings[id];

            try
            {
                var command = (IExternalCommand)Activator.CreateInstance(binding.Command);
                return command.Execute(data, ref message, elements);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                var real = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                ConsoleOutput.Exception(real, binding.Command.FullName + " упала:");
                message = real.Message;
                return Result.Failed;
            }
        }

        public static bool IsAvailable(int id, UIApplication app, CategorySet categories)
        {
            var binding = Bindings[id];

            try
            {
                binding.AvailabilityInstance ??= (IExternalCommandAvailability)Activator.CreateInstance(binding.Availability);
                return binding.AvailabilityInstance.IsCommandAvailable(app, categories);
            }
            catch
            {
                return false;
            }
        }

        // Buttons usually point to the shadow copy (Assembly.Location), but a path built from
        // the original build folder is mapped to the copy too, so the original is never locked.
        private static string ShadowPath(string assemblyPath, AppHost.ShadowFolder folder, string originalDirectory)
        {
            if (string.IsNullOrEmpty(assemblyPath)) return null;
            if (folder.Owns(assemblyPath)) return Path.GetFullPath(assemblyPath);

            var dir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
            if (!string.Equals(dir, originalDirectory, StringComparison.OrdinalIgnoreCase)) return null;

            var mapped = Path.Combine(folder.Directory, Path.GetFileName(assemblyPath));
            return File.Exists(mapped) ? mapped : null;
        }

        private static string ClassName(int id) => "Command" + id;

        private static void Emit(string ns, IEnumerable<int> ids, string path)
        {
            var source = new StringBuilder();
            source.AppendLine("namespace " + ns + " {");

            foreach (var id in ids)
            {
                source.AppendLine("[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]");
                source.AppendLine("public sealed class " + ClassName(id) + " : Autodesk.Revit.UI.IExternalCommand, Autodesk.Revit.UI.IExternalCommandAvailability {");
                source.AppendLine("  public Autodesk.Revit.UI.Result Execute(Autodesk.Revit.UI.ExternalCommandData d, ref string m, Autodesk.Revit.DB.ElementSet e)");
                source.AppendLine("    => RevitDevKit.Core.ProxyCommands.Execute(" + id + ", d, ref m, e);");
                source.AppendLine("  public bool IsCommandAvailable(Autodesk.Revit.UI.UIApplication a, Autodesk.Revit.DB.CategorySet c)");
                source.AppendLine("    => RevitDevKit.Core.ProxyCommands.IsAvailable(" + id + ", a, c);");
                source.AppendLine("}");
            }

            source.AppendLine("}");

            var references = new[]
                {
                    typeof(object).Assembly, typeof(Document).Assembly, typeof(UIApplication).Assembly, typeof(ProxyCommands).Assembly
                }
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));

            var compilation = CSharpCompilation.Create(ns, new[] { CSharpSyntaxTree.ParseText(source.ToString()) }, references, null);
            compilation = compilation.WithOptions(compilation.Options
                .WithOutputKind(OutputKind.DynamicallyLinkedLibrary)
                .WithPlatform(Platform.X64));

            var result = compilation.Emit(path);
            if (!result.Success)
            {
                throw new InvalidOperationException("Не удалось собрать прокси-команды:\n" +
                    string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            }
        }
    }
}
