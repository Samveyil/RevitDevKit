using Autodesk.Revit.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Adw = Autodesk.Windows;

namespace RevitDevKit.Core
{
    // Private members of RevitAPIUI the preview relies on (the same in Revit 2019–2024).
    // The API checks duplicate tab, panel and button names only against these registries.
    internal static class RibbonAccess
    {
        public const string AddInsTab = "Add-Ins";
        public const string AnalyzeTab = "Analyze";

        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly FieldInfo RegistryField =
            typeof(UIApplication).GetField("m_ItemsNameDictionary", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly FieldInfo PanelItemsField = typeof(RibbonPanel).GetField("m_ItemsNameDictionary", Private);
        private static readonly FieldInfo PanelAdwField = typeof(RibbonPanel).GetField("m_RibbonPanel", Private);
        private static readonly FieldInfo ItemAdwField = typeof(RibbonItem).GetField("m_RibbonItem", Private);
        private static readonly FieldInfo PulldownItemsField = typeof(PulldownButton).GetField("m_ItemsNameDictionary", Private);

        public static void Verify()
        {
            var missing = new[]
                {
                    (RegistryField, "UIApplication.m_ItemsNameDictionary"),
                    (PanelItemsField, "RibbonPanel.m_ItemsNameDictionary"),
                    (PanelAdwField, "RibbonPanel.m_RibbonPanel"),
                    (ItemAdwField, "RibbonItem.m_RibbonItem"),
                    (PulldownItemsField, "PulldownButton.m_ItemsNameDictionary")
                }
                .Where(f => f.Item1 == null)
                .Select(f => f.Item2)
                .ToList();

            if (missing.Count > 0)
                throw new NotSupportedException("В этой версии Revit лента устроена иначе — не найдены " + string.Join(", ", missing) + ".");

            if (Ribbon == null || Registry == null)
                throw new InvalidOperationException("Лента Revit ещё не создана.");
        }

        public static Adw.RibbonControl Ribbon => Adw.ComponentManager.Ribbon;

        public static Dictionary<string, Dictionary<string, RibbonPanel>> Registry
        {
            get => (Dictionary<string, Dictionary<string, RibbonPanel>>)RegistryField.GetValue(null);
            set => RegistryField.SetValue(null, value);
        }

        public static bool IsCustomTab(string key) => key != AddInsTab && key != AnalyzeTab;

        public static Dictionary<string, RibbonItem> Items(RibbonPanel panel) =>
            (Dictionary<string, RibbonItem>)PanelItemsField.GetValue(panel);

        public static Dictionary<string, PushButton> Items(PulldownButton button) =>
            (Dictionary<string, PushButton>)PulldownItemsField.GetValue(button);

        public static Adw.RibbonPanel AdwPanel(RibbonPanel panel) => (Adw.RibbonPanel)PanelAdwField.GetValue(panel);

        public static Adw.RibbonItem AdwItem(RibbonItem item) => (Adw.RibbonItem)ItemAdwField.GetValue(item);

        public static IEnumerable<PushButton> PushButtons(RibbonItem item)
        {
            if (item is PulldownButton pulldown) return Items(pulldown).Values;
            if (item is PushButton button) return new[] { button };
            return Enumerable.Empty<PushButton>();
        }

        public static void Detach(Adw.RibbonPanel panel)
        {
            if (panel == null) return;

            foreach (var tab in Ribbon.Tabs)
            {
                if (tab.Panels.Remove(panel)) return;
            }
        }
    }

    // OnStartup of the new build runs against an empty name registry, so the add-in builds its
    // ribbon from scratch next to the live one. Live custom tabs are renamed for that time:
    // CreateRibbonTab looks tabs up by Id and refuses an existing one.
    internal sealed class RibbonDraft
    {
        private const string HiddenSuffix = "\u0001DevKitLive";

        public Dictionary<string, Dictionary<string, RibbonPanel>> Registry { get; } =
            new Dictionary<string, Dictionary<string, RibbonPanel>>
            {
                [RibbonAccess.AddInsTab] = new Dictionary<string, RibbonPanel>(),
                [RibbonAccess.AnalyzeTab] = new Dictionary<string, RibbonPanel>()
            };

        public List<Adw.RibbonTab> NewTabs { get; private set; } = new List<Adw.RibbonTab>();

        public static RibbonDraft Run(Action startup)
        {
            var ribbon = RibbonAccess.Ribbon;
            var live = RibbonAccess.Registry;
            var tabsBefore = ribbon.Tabs.ToList();

            var renamed = live.Keys
                .Where(RibbonAccess.IsCustomTab)
                .Select(ribbon.FindTab)
                .Where(t => t != null)
                .ToList();

            var draft = new RibbonDraft();
            Exception failure = null;

            foreach (var tab in renamed) tab.Id += HiddenSuffix;
            RibbonAccess.Registry = draft.Registry;
            try
            {
                startup();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                RibbonAccess.Registry = live;
                foreach (var tab in renamed) tab.Id = tab.Id.Substring(0, tab.Id.Length - HiddenSuffix.Length);
            }

            draft.NewTabs = ribbon.Tabs.Where(t => !tabsBefore.Contains(t)).ToList();

            if (failure != null)
            {
                draft.Discard();
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return draft;
        }

        public IEnumerable<RibbonItem> Items()
        {
            foreach (var item in Registry.Values.SelectMany(p => p.Values).SelectMany(p => RibbonAccess.Items(p).Values))
            {
                yield return item;

                if (item is PulldownButton pulldown)
                {
                    foreach (var child in RibbonAccess.Items(pulldown).Values)
                        yield return child;
                }
            }
        }

        // DLL file names the new build's commands live in: live buttons pointing to them are its own.
        public HashSet<string> CommandFiles() =>
            new HashSet<string>(
                Items().OfType<PushButton>().Select(b => FileName(b.AssemblyName)).Where(n => n != null),
                StringComparer.OrdinalIgnoreCase);

        public void Discard()
        {
            foreach (var panel in Registry.Values.SelectMany(p => p.Values))
                RibbonAccess.Detach(RibbonAccess.AdwPanel(panel));

            foreach (var tab in NewTabs)
                RibbonAccess.Ribbon.Tabs.Remove(tab);
        }

        internal static string FileName(string path)
        {
            try { return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path); }
            catch { return null; }
        }
    }

    // Carries the draft over to the live ribbon. A button that exists in both keeps its live
    // object and command and gets the new text, tooltips and images; a new button, or one whose
    // type changed, is taken from the draft. Every change is recorded, so Undo brings back
    // the ribbon created from .addin.
    internal sealed class RibbonMerge
    {
        private readonly List<Action> _undo = new List<Action>();
        private readonly HashSet<string> _commandFiles;

        public RibbonMerge(HashSet<string> commandFiles) => _commandFiles = commandFiles;

        // Draft buttons that are now on the live ribbon: their commands must come from the new build.
        public List<PushButton> Adopted { get; } = new List<PushButton>();

        public int Updated { get; private set; }
        public int Added { get; private set; }
        public int Removed { get; private set; }

        public void Undo()
        {
            for (var i = _undo.Count - 1; i >= 0; i--)
            {
                try { _undo[i](); }
                catch (Exception ex) { ConsoleOutput.Debug("Откат ленты: " + ex.Message); }
            }

            _undo.Clear();
        }

        public void Apply(RibbonDraft draft)
        {
            var ribbon = RibbonAccess.Ribbon;
            var live = RibbonAccess.Registry;

            foreach (var tab in draft.Registry)
            {
                if (tab.Value.Count == 0) continue;

                var draftTab = draft.NewTabs.FirstOrDefault(t => t.Id == tab.Key);
                var liveTab = ribbon.Tabs.FirstOrDefault(t => t.Id == tab.Key && t != draftTab);

                if (!live.TryGetValue(tab.Key, out var livePanels) || liveTab == null)
                {
                    if (draftTab != null) AdoptTab(live, tab.Key, tab.Value, draftTab);
                    continue;
                }

                var names = tab.Value.Keys.ToList();
                for (var i = 0; i < names.Count; i++)
                {
                    var draftPanel = tab.Value[names[i]];

                    if (livePanels.TryGetValue(names[i], out var livePanel))
                    {
                        MergePanel(livePanel, draftPanel);
                        RibbonAccess.Detach(RibbonAccess.AdwPanel(draftPanel));
                        continue;
                    }

                    var next = names.Skip(i + 1)
                        .Select(n => livePanels.TryGetValue(n, out var p) ? p : null)
                        .FirstOrDefault(p => p != null);

                    AdoptPanel(liveTab, livePanels, names[i], draftPanel, next);
                }

                if (draftTab != null) ribbon.Tabs.Remove(draftTab);
            }

            HideRemovedPanels(draft);
        }

        private void AdoptTab(Dictionary<string, Dictionary<string, RibbonPanel>> live, string key,
            Dictionary<string, RibbonPanel> panels, Adw.RibbonTab tab)
        {
            live[key] = panels;

            foreach (var item in panels.Values.SelectMany(p => RibbonAccess.Items(p).Values))
                Adopt(item);

            _undo.Add(() =>
            {
                live.Remove(key);
                RibbonAccess.Ribbon.Tabs.Remove(tab);
            });
        }

        private void AdoptPanel(Adw.RibbonTab liveTab, Dictionary<string, RibbonPanel> livePanels, string name,
            RibbonPanel panel, RibbonPanel next)
        {
            var adw = RibbonAccess.AdwPanel(panel);
            RibbonAccess.Detach(adw);

            var index = next == null ? -1 : liveTab.Panels.IndexOf(RibbonAccess.AdwPanel(next));
            liveTab.Panels.Insert(index < 0 ? liveTab.Panels.Count : index, adw);
            livePanels[name] = panel;

            foreach (var item in RibbonAccess.Items(panel).Values)
                Adopt(item);

            _undo.Add(() =>
            {
                liveTab.Panels.Remove(adw);
                livePanels.Remove(name);
            });
        }

        private void MergePanel(RibbonPanel live, RibbonPanel draft)
        {
            var liveSource = RibbonAccess.AdwPanel(live).Source.Items;
            var draftSource = RibbonAccess.AdwPanel(draft).Source.Items;
            var liveItems = RibbonAccess.Items(live);
            var draftItems = RibbonAccess.Items(draft);

            var oldOrder = liveSource.ToList();
            var oldItems = liveItems.ToList();
            _undo.Add(() =>
            {
                Replace(liveSource, oldOrder);
                Replace(liveItems, oldItems);
            });

            var draftByAdw = draftItems.Values.ToDictionary(RibbonAccess.AdwItem);
            var liveRows = new Dictionary<Adw.RibbonItem, Adw.RibbonRowPanel>();
            foreach (var row in liveSource.OfType<Adw.RibbonRowPanel>())
            {
                foreach (var child in row.Items)
                    liveRows[child] = row;
            }

            var chosen = new Dictionary<string, RibbonItem>();
            var order = new List<Adw.RibbonItem>();

            foreach (var element in draftSource.ToList())
            {
                if (element is Adw.RibbonRowPanel row)
                    order.Add(MergeRow(row, draftByAdw, liveItems, liveRows, chosen));
                else if (draftByAdw.TryGetValue(element, out var item))
                    order.Add(MergeItem(item, liveItems, liveRows, chosen));
                else
                    order.Add(element);
            }

            // Buttons the new build no longer creates go away; buttons of other add-ins stay.
            foreach (var item in liveItems.Values)
            {
                if (chosen.Values.Contains(item)) continue;

                if (IsOurs(item))
                {
                    if (!chosen.ContainsKey(item.Name)) Removed++;
                    continue;
                }

                var adw = RibbonAccess.AdwItem(item);
                var element = liveRows.TryGetValue(adw, out var row) ? row : adw;
                if (!order.Contains(element)) order.Add(element);
                if (!chosen.ContainsKey(item.Name)) chosen[item.Name] = item;
            }

            draftSource.Clear();
            Replace(liveSource, order);
            Replace(liveItems, chosen);
        }

        private Adw.RibbonItem MergeItem(RibbonItem draft, Dictionary<string, RibbonItem> liveItems,
            Dictionary<Adw.RibbonItem, Adw.RibbonRowPanel> liveRows, Dictionary<string, RibbonItem> chosen)
        {
            if (liveItems.TryGetValue(draft.Name, out var live) && live.GetType() == draft.GetType() &&
                !liveRows.ContainsKey(RibbonAccess.AdwItem(live)))
            {
                Update(live, draft);
                chosen[draft.Name] = live;
                return RibbonAccess.AdwItem(live);
            }

            Adopt(draft);
            chosen[draft.Name] = draft;
            return RibbonAccess.AdwItem(draft);
        }

        // A stack is kept only when the same buttons are stacked the same way; otherwise the
        // whole stack is taken from the draft.
        private Adw.RibbonItem MergeRow(Adw.RibbonRowPanel row, Dictionary<Adw.RibbonItem, RibbonItem> draftByAdw,
            Dictionary<string, RibbonItem> liveItems, Dictionary<Adw.RibbonItem, Adw.RibbonRowPanel> liveRows,
            Dictionary<string, RibbonItem> chosen)
        {
            var drafts = row.Items
                .Where(e => !(e is Adw.RibbonRowBreak))
                .Select(e => draftByAdw.TryGetValue(e, out var d) ? d : null)
                .ToList();

            if (drafts.All(d => d != null))
            {
                var lives = drafts
                    .Select(d => liveItems.TryGetValue(d.Name, out var l) && l.GetType() == d.GetType() ? l : null)
                    .ToList();

                var liveRow = lives.All(l => l != null) && liveRows.TryGetValue(RibbonAccess.AdwItem(lives[0]), out var r) ? r : null;

                if (liveRow != null &&
                    liveRow.Items.Where(e => !(e is Adw.RibbonRowBreak)).SequenceEqual(lives.Select(RibbonAccess.AdwItem)))
                {
                    for (var i = 0; i < lives.Count; i++)
                    {
                        Update(lives[i], drafts[i]);
                        chosen[drafts[i].Name] = lives[i];
                    }

                    return liveRow;
                }
            }

            foreach (var draft in drafts.Where(d => d != null))
            {
                Adopt(draft);
                chosen[draft.Name] = draft;
            }

            return row;
        }

        private void MergePulldown(PulldownButton live, PulldownButton draft)
        {
            var liveList = ((Adw.RibbonListButton)RibbonAccess.AdwItem(live)).Items;
            var draftList = ((Adw.RibbonListButton)RibbonAccess.AdwItem(draft)).Items;
            var liveItems = RibbonAccess.Items(live);
            var draftItems = RibbonAccess.Items(draft);

            var oldOrder = liveList.ToList();
            var oldItems = liveItems.ToList();
            _undo.Add(() =>
            {
                Replace(liveList, oldOrder);
                Replace(liveItems, oldItems);
            });

            var draftByAdw = draftItems.Values.ToDictionary(b => RibbonAccess.AdwItem(b));
            var chosen = new Dictionary<string, PushButton>();
            var order = new List<Adw.RibbonItem>();

            foreach (var element in draftList.ToList())
            {
                if (!draftByAdw.TryGetValue(element, out var child))
                {
                    order.Add(element);
                    continue;
                }

                if (liveItems.TryGetValue(child.Name, out var existing))
                {
                    Update(existing, child);
                    chosen[child.Name] = existing;
                    order.Add(RibbonAccess.AdwItem(existing));
                }
                else
                {
                    Adopt(child);
                    chosen[child.Name] = child;
                    order.Add(RibbonAccess.AdwItem(child));
                }
            }

            Removed += liveItems.Keys.Count(k => !chosen.ContainsKey(k));

            draftList.Clear();
            Replace(liveList, order);
            Replace(liveItems, chosen);
        }

        // The command of a live button is left alone: only what the button looks like changes.
        private void Update(RibbonItem live, RibbonItem draft)
        {
            Updated++;

            Copy(live, draft, i => i.ItemText, (i, v) => i.ItemText = v);
            Copy(live, draft, i => i.ToolTip, (i, v) => i.ToolTip = v);
            Copy(live, draft, i => i.LongDescription, (i, v) => i.LongDescription = v);
            Copy(live, draft, i => i.ToolTipImage, (i, v) => i.ToolTipImage = v);
            Copy(live, draft, i => i.Enabled, (i, v) => i.Enabled = v);
            Copy(live, draft, i => i.Visible, (i, v) => i.Visible = v);

            if (live is RibbonButton liveButton && draft is RibbonButton draftButton)
            {
                Copy(liveButton, draftButton, i => i.Image, (i, v) => i.Image = v);
                Copy(liveButton, draftButton, i => i.LargeImage, (i, v) => i.LargeImage = v);
            }

            if (live is PulldownButton livePulldown && draft is PulldownButton draftPulldown)
                MergePulldown(livePulldown, draftPulldown);
        }

        private void Copy<TItem, TValue>(TItem live, TItem draft, Func<TItem, TValue> get, Action<TItem, TValue> set)
        {
            TValue old, value;
            try
            {
                old = get(live);
                value = get(draft);
            }
            catch
            {
                return;
            }

            if (Equals(old, value)) return;

            try
            {
                set(live, value);
                _undo.Add(() => set(live, old));
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Лента: " + ex.Message);
            }
        }

        private void Adopt(RibbonItem item)
        {
            Added++;
            Adopted.AddRange(RibbonAccess.PushButtons(item));
        }

        private bool IsOurs(RibbonItem item)
        {
            if (item is PushButton button)
                return _commandFiles.Contains(RibbonDraft.FileName(button.AssemblyName) ?? string.Empty);

            if (item is PulldownButton pulldown)
                return RibbonAccess.Items(pulldown).Values.Any(IsOurs);

            return false;
        }

        // Whole panels the new build no longer creates are hidden.
        private void HideRemovedPanels(RibbonDraft draft)
        {
            foreach (var tab in RibbonAccess.Registry.ToList())
            {
                draft.Registry.TryGetValue(tab.Key, out var draftPanels);

                foreach (var entry in tab.Value.ToList())
                {
                    if (draftPanels != null && draftPanels.ContainsKey(entry.Key)) continue;

                    var panel = entry.Value;
                    var items = RibbonAccess.Items(panel).Values.ToList();
                    if (items.Count == 0 || !items.All(IsOurs) || !panel.Visible) continue;

                    panel.Visible = false;
                    Removed += items.Count;
                    _undo.Add(() => panel.Visible = true);
                }
            }
        }

        private static void Replace(IList<Adw.RibbonItem> target, IEnumerable<Adw.RibbonItem> items)
        {
            var list = items.ToList();
            target.Clear();
            foreach (var item in list) target.Add(item);
        }

        private static void Replace<T>(Dictionary<string, T> target, IEnumerable<KeyValuePair<string, T>> items)
        {
            var list = items.ToList();
            target.Clear();
            foreach (var item in list) target[item.Key] = item.Value;
        }
    }

    // WPF resolves pack://application:,,,/Name;component/... to an assembly by name and caches
    // the answer, so a rebuilt add-in would get the resources of the copy Revit loaded: old
    // icons, and an exception for an icon that is new.
    internal static class PackImages
    {
        private static readonly FieldInfo ResourceManagersField = typeof(System.Windows.Application).Assembly
            .GetType("MS.Internal.AppModel.ResourceContainer")?
            .GetField("_registeredResourceManagers", BindingFlags.NonPublic | BindingFlags.Static);

        private const string Component = ";component/";

        // While the draft runs, pack URIs of these assemblies resolve anew - to the newest loaded
        // copy, i.e. the fresh build. Afterwards the live add-in gets its own resources back.
        public static IDisposable Isolate(IEnumerable<string> assemblyNames)
        {
            var managers = ResourceManagersField?.GetValue(null) as IDictionary;
            if (managers == null) return new Restore(null, null, null);

            var names = assemblyNames.Select(n => n.ToLowerInvariant()).ToList();
            var saved = Matching(managers, names);

            foreach (var entry in saved)
                managers.Remove(entry.Key);

            return new Restore(managers, names, saved);
        }

        // Keys are the lower-cased assembly name, followed by the version if the URI has one.
        private static List<DictionaryEntry> Matching(IDictionary managers, List<string> names)
        {
            var result = new List<DictionaryEntry>();

            foreach (DictionaryEntry entry in managers)
            {
                if (entry.Key is string key && names.Any(n => key == n || key.StartsWith(n) && char.IsDigit(key[n.Length])))
                    result.Add(entry);
            }

            return result;
        }

        // WPF also caches decoded images by URI: an icon file that kept its name would stay old.
        public static void Refresh(IEnumerable<RibbonItem> items, RibbonPreview.ShadowFolder folder)
        {
            foreach (var item in items)
            {
                try
                {
                    if (item is RibbonButton button)
                    {
                        var image = Reload(button.Image, folder);
                        if (image != null) button.Image = image;

                        var large = Reload(button.LargeImage, folder);
                        if (large != null) button.LargeImage = large;
                    }

                    var tip = Reload(item.ToolTipImage, folder);
                    if (tip != null) item.ToolTipImage = tip;
                }
                catch (Exception ex)
                {
                    ConsoleOutput.Debug("Иконка «" + item.Name + "» не перечитана: " + ex.Message);
                }
            }
        }

        private static ImageSource Reload(ImageSource source, RibbonPreview.ShadowFolder folder)
        {
            if (!(source is BitmapImage bitmap) || bitmap.UriSource == null || !bitmap.UriSource.IsAbsoluteUri) return null;
            if (!string.Equals(bitmap.UriSource.Scheme, "pack", StringComparison.OrdinalIgnoreCase)) return null;

            var path = Uri.UnescapeDataString(bitmap.UriSource.AbsolutePath).TrimStart('/');
            var at = path.IndexOf(Component, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;

            var assembly = folder.TryLoad(path.Substring(0, at).Split(';')[0]);
            if (assembly == null) return null;

            var part = path.Substring(at + Component.Length);

            using (var resources = assembly.GetManifestResourceStream(assembly.GetName().Name + ".g.resources"))
            {
                if (resources == null) return null;

                using (var reader = new ResourceReader(resources))
                {
                    foreach (DictionaryEntry entry in reader)
                    {
                        if (!(entry.Key is string key) ||
                            !string.Equals(Uri.UnescapeDataString(key), part, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!(entry.Value is Stream stream)) return null;

                        var copy = new MemoryStream();
                        stream.CopyTo(copy);
                        copy.Position = 0;

                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.StreamSource = copy;
                        image.EndInit();
                        image.Freeze();
                        return image;
                    }
                }
            }

            return null;
        }

        private sealed class Restore : IDisposable
        {
            private readonly IDictionary _managers;
            private readonly List<string> _names;
            private readonly List<DictionaryEntry> _saved;

            public Restore(IDictionary managers, List<string> names, List<DictionaryEntry> saved)
            {
                _managers = managers;
                _names = names;
                _saved = saved;
            }

            public void Dispose()
            {
                if (_managers == null) return;

                foreach (var entry in Matching(_managers, _names))
                    _managers.Remove(entry.Key);

                foreach (var entry in _saved)
                    _managers[entry.Key] = entry.Value;
            }
        }
    }
}
