using Autodesk.Revit.UI;
using BIMPlugins.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
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

    // Carries the draft over to the live ribbon. A button that exists in both keeps its live
    // object and command and gets the new text, tooltips and images; a new button, or one whose
    // type changed, is taken from the draft. Every change is recorded, so Undo brings back
    // the ribbon created from .addin.
    internal sealed class RibbonMerge
    {
        private readonly List<Action> _undo = new List<Action>();
        private readonly Func<PushButton, bool> _isOwnCommand;

        // isOwnCommand tells a live button of this add-in from buttons of other add-ins.
        public RibbonMerge(Func<PushButton, bool> isOwnCommand) => _isOwnCommand = isOwnCommand;

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
                        RibbonAccess.Detach(draftPanel.ToAWRibbonPanel());
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
            MergeForeignPanels(draft);
        }

        // A panel the add-in made through AdWindows replaces nothing by name: its counterpart is the live panel
        // on the same tab with the same buttons. That one is kept, as the add-in holds it to show and hide it,
        // and gets the new buttons; a panel without a counterpart is new and goes away on Undo.
        private void MergeForeignPanels(RibbonDraft draft)
        {
            var apiPanels = new HashSet<Adw.RibbonPanel>(RibbonAccess.Registry.Values.SelectMany(p => p.Values).Select(p => p.ToAWRibbonPanel()));
            var draftPanels = new HashSet<Adw.RibbonPanel>(draft.ForeignPanels.Select(f => f.Item2));
            var draftButtons = draft.Items().OfType<PushButton>().ToList();
            var taken = new HashSet<Adw.RibbonPanel>();

            foreach (var foreign in draft.ForeignPanels)
            {
                var tab = foreign.Item1;
                var panel = foreign.Item2;
                var live = tab.Panels
                    .Where(p => !draftPanels.Contains(p) && !apiPanels.Contains(p) && !taken.Contains(p))
                    .Select(p => new { Panel = p, Score = Likeness(p, panel) })
                    .Where(c => c.Score > 0)
                    .OrderByDescending(c => c.Score)
                    .Select(c => c.Panel)
                    .FirstOrDefault();

                if (live == null)
                {
                    foreach (var item in Flatten(panel.Source.Items))
                        AdoptAdw(item, draftButtons);
                    _undo.Add(() => tab.Panels.Remove(panel));
                    continue;
                }

                taken.Add(live);
                MergeForeignPanel(live, panel, draftButtons);
                tab.Panels.Remove(panel);
            }
        }

        // How sure it is the same panel: buttons with the same Id, then the same title.
        private static int Likeness(Adw.RibbonPanel live, Adw.RibbonPanel draft)
        {
            var ids = new HashSet<string>(Flatten(live.Source.Items).Select(i => i.Id).Where(id => !string.IsNullOrEmpty(id)));
            var score = 2 * Flatten(draft.Source.Items).Count(i => !string.IsNullOrEmpty(i.Id) && ids.Contains(i.Id));
            if (!string.IsNullOrEmpty(draft.Source.Title) && draft.Source.Title == live.Source.Title) score++;
            return score;
        }

        private static IEnumerable<Adw.RibbonItem> Flatten(IEnumerable<Adw.RibbonItem> items)
        {
            foreach (var item in items)
            {
                if (item is Adw.RibbonRowPanel row)
                {
                    foreach (var child in Flatten(row.Items)) yield return child;
                }
                else if (!(item is Adw.RibbonSeparator) && !(item is Adw.RibbonRowBreak))
                {
                    yield return item;
                }
            }
        }

        // Buttons are paired by Id, or by place and type when the add-in gave none (a clone may have none).
        private void MergeForeignPanel(Adw.RibbonPanel live, Adw.RibbonPanel draft, List<PushButton> draftButtons)
        {
            var liveItems = live.Source.Items;
            var oldOrder = liveItems.ToList();
            var oldVisible = oldOrder.ToDictionary(i => i, i => i.IsVisible);
            _undo.Add(() =>
            {
                Replace(liveItems, oldOrder);
                foreach (var entry in oldVisible) entry.Key.IsVisible = entry.Value;
            });

            var order = new List<Adw.RibbonItem>();
            var draftItems = draft.Source.Items.ToList();
            for (var i = 0; i < draftItems.Count; i++)
            {
                var item = draftItems[i];
                Adw.RibbonItem match;
                if (!string.IsNullOrEmpty(item.Id))
                    match = oldOrder.FirstOrDefault(l => l.Id == item.Id && l.GetType() == item.GetType() && !order.Contains(l));
                else
                    match = i < oldOrder.Count && string.IsNullOrEmpty(oldOrder[i].Id) && oldOrder[i].GetType() == item.GetType() && !order.Contains(oldOrder[i])
                        ? oldOrder[i] : null;

                if (match == null || item is Adw.RibbonRowPanel)
                {
                    foreach (var added in Flatten(new[] { item }))
                        AdoptAdw(added, draftButtons);
                    order.Add(item);
                    continue;
                }

                if (!ReferenceEquals(match, item))
                {
                    Updated++;
                    CopyAdw(match, item);
                }

                order.Add(match);
            }

            // Buttons the new build no longer puts on this panel are hidden, not lost: Undo shows them again.
            foreach (var item in oldOrder.Where(l => !order.Contains(l) && !(l is Adw.RibbonSeparator)).ToList())
            {
                if (!item.IsVisible) continue;
                item.IsVisible = false;
                Removed++;
            }

            foreach (var item in oldOrder.Where(l => !order.Contains(l)).ToList())
                order.Add(item);

            draft.Source.Items.Clear();
            Replace(liveItems, order);
        }

        private void AdoptAdw(Adw.RibbonItem item, List<PushButton> draftButtons)
        {
            Added++;
            Adopted.AddRange(draftButtons.Where(b => ReferenceEquals(b.ToAWRibbonItem(), item)));
        }

        // What an add-in sets on an AdWindows item: its text, tooltip and images besides the appearance.
        private static readonly string[] ContentProperties = { "Text", "ToolTip", "Description", "Image", "LargeImage", "IsEnabled", "IsVisible" };

        private void CopyAdw(Adw.RibbonItem live, Adw.RibbonItem draft)
        {
            foreach (var name in ContentProperties)
            {
                PropertyInfo property;
                try { property = live.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance); }
                catch (AmbiguousMatchException) { continue; }

                if (property == null || !property.CanRead || !property.CanWrite) continue;
                Copy(live, draft, i => property.GetValue(i), (i, v) => property.SetValue(i, v));
            }

            CopyAppearance(live, draft);
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
            var adw = panel.ToAWRibbonPanel();
            RibbonAccess.Detach(adw);

            var index = next == null ? -1 : liveTab.Panels.IndexOf(next.ToAWRibbonPanel());
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
            var liveSource = live.ToAWRibbonPanel().Source.Items;
            var draftSource = draft.ToAWRibbonPanel().Source.Items;
            var liveItems = RibbonAccess.Items(live);
            var draftItems = RibbonAccess.Items(draft);

            var oldOrder = liveSource.ToList();
            var oldItems = liveItems.ToList();
            _undo.Add(() =>
            {
                Replace(liveSource, oldOrder);
                Replace(liveItems, oldItems);
            });

            var draftByAdw = draftItems.Values.ToDictionary(d => d.ToAWRibbonItem());
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
            // A button whose name the new build took for another type is replaced, not kept.
            foreach (var item in liveItems.Values)
            {
                if (chosen.Values.Contains(item) || chosen.ContainsKey(item.Name)) continue;

                if (IsOurs(item))
                {
                    Removed++;
                    continue;
                }

                var adw =item.ToAWRibbonItem();
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
                !liveRows.ContainsKey(live.ToAWRibbonItem()))
            {
                Update(live, draft);
                chosen[draft.Name] = live;
                return live.ToAWRibbonItem();
            }

            Adopt(draft);
            chosen[draft.Name] = draft;
            return draft.ToAWRibbonItem();
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

                var liveRow = lives.All(l => l != null) && liveRows.TryGetValue(lives[0].ToAWRibbonItem(), out var r) ? r : null;

                if (liveRow != null &&
                    liveRow.Items.Where(e => !(e is Adw.RibbonRowBreak)).SequenceEqual(lives.Select(l => l.ToAWRibbonItem())))
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
            var liveList = ((Adw.RibbonListButton)live.ToAWRibbonItem()).Items;
            var draftList = ((Adw.RibbonListButton)draft.ToAWRibbonItem()).Items;
            var liveItems = RibbonAccess.Items(live);
            var draftItems = RibbonAccess.Items(draft);

            var oldOrder = liveList.ToList();
            var oldItems = liveItems.ToList();
            _undo.Add(() =>
            {
                Replace(liveList, oldOrder);
                Replace(liveItems, oldItems);
            });

            var draftByAdw = draftItems.Values.ToDictionary(b => b.ToAWRibbonItem());
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
                    order.Add(existing.ToAWRibbonItem());
                }
                else
                {
                    Adopt(child);
                    chosen[child.Name] = child;
                    order.Add(child.ToAWRibbonItem());
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

            CopyAppearance(live.ToAWRibbonItem(), draft.ToAWRibbonItem());

            if (live is PulldownButton livePulldown && draft is PulldownButton draftPulldown)
                MergePulldown(livePulldown, draftPulldown);
        }

        // Not in Revit's API: add-ins set them on the AdWindows item directly (ShowText and the like).
        private static readonly string[] AppearanceProperties =
        {
            "ShowText", "ShowImage", "Size", "Orientation", "ResizeStyle",
            "Width", "MinWidth", "Height", "MinHeight", "MaxHeight",
            "Description", "KeyTip", "Highlight", "GroupLocation",
            "IsToolTipEnabled", "ShowToolTipOnDisabled", "HelpSource", "HelpTopic",
            "IsSplit", "IsSynchronizedWithCurrentItem", "SynchronizeOption", "IsGrouping",
            "ListButtonStyle", "ListStyle", "ListImageSize", "HighlightDropDown", "AllowOrientation"
        };

        private void CopyAppearance(Adw.RibbonItem live, Adw.RibbonItem draft)
        {
            if (live == null || draft == null || live.GetType() != draft.GetType()) return;

            foreach (var name in AppearanceProperties)
            {
                PropertyInfo property;
                try { property = live.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance); }
                catch (AmbiguousMatchException) { continue; }

                if (property == null || !property.CanRead || !property.CanWrite) continue;

                Copy(live, draft, i => property.GetValue(i), (i, v) => property.SetValue(i, v));
            }
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
                return _isOwnCommand(button);

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

    // WPF resolves pack://application:,,,/Name;component/... (icons, and the BAML behind every
    // InitializeComponent) through one resource wrapper per assembly name. Cached resource parts
    // keep that wrapper, and LoadComponent refuses BAML from another copy than the component's.
    // So the wrappers are never replaced, only pointed at the right copy of the assembly.
    internal static class PackResources
    {
        private static readonly FieldInfo ResourceManagersField = typeof(System.Windows.Application).Assembly
            .GetType("MS.Internal.AppModel.ResourceContainer")?
            .GetField("_registeredResourceManagers", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly Type WrapperType = ResourceManagersField?.FieldType.GetGenericArguments()[1];
        private static readonly PropertyInfo WrapperAssembly =
            WrapperType?.GetProperty("Assembly", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

        private const string Component = ";component/";

        private static HarmonyLib.Harmony _harmony;

        // The installed add-in and a fresh build may have the same name (Dbg_BIMPlugins), and then two copies
        // share one wrapper: whichever it points to, the other copy's windows fail with "the component does not
        // have a resource identified by the URI". So each time a component loads its XAML, the wrapper of its
        // name is pointed at the component's own assembly first.
        public static void FollowComponents()
        {
            if (_harmony != null || ResourceManagersField == null || WrapperAssembly == null) return;

            try
            {
                var loadComponent = typeof(System.Windows.Application).GetMethod(nameof(System.Windows.Application.LoadComponent),
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(object), typeof(Uri) }, null);
                _harmony = new HarmonyLib.Harmony("RevitDevKit.PackResources");
                _harmony.Patch(loadComponent, prefix: new HarmonyLib.HarmonyMethod(typeof(PackResources).GetMethod(nameof(BeforeLoadComponent), BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception ex)
            {
                ConsoleOutput.Warning("Не удалось перехватить Application.LoadComponent: " + ex.Message);
            }
        }

        private static void BeforeLoadComponent(object component, Uri resourceLocator)
        {
            try
            {
                var assembly = component?.GetType().Assembly;
                if (assembly == null || resourceLocator == null) return;

                var path = Uri.UnescapeDataString(resourceLocator.IsAbsoluteUri ? resourceLocator.AbsolutePath : resourceLocator.OriginalString).TrimStart('/');
                var at = path.IndexOf(Component, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return;

                var name = assembly.GetName().Name;
                if (!string.Equals(path.Substring(0, at).Split(';')[0], name, StringComparison.OrdinalIgnoreCase)) return;

                var managers = ResourceManagersField.GetValue(null) as IDictionary;
                if (managers == null) return;

                var key = name.ToLowerInvariant();
                if (!managers.Contains(key))
                    managers[key] = Activator.CreateInstance(WrapperType, BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { assembly }, null);

                Point(managers, name, assembly);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Ресурсы WPF для " + component?.GetType().FullName + " не перенаправлены: " + ex.Message);
            }
        }

        // While the draft runs, the resources of the fresh build come from its copy. Afterwards an
        // assembly Revit loaded under the same name gets its own resources back.
        public static IDisposable Pin(RibbonPreview.ShadowFolder folder)
        {
            var managers = ResourceManagersField?.GetValue(null) as IDictionary;
            if (managers == null || WrapperAssembly == null) return new Unpin(null, null);

            try
            {
                foreach (var assembly in folder.Loaded)
                {
                    var key = assembly.GetName().Name.ToLowerInvariant();
                    if (!managers.Contains(key))
                        managers[key] = Activator.CreateInstance(WrapperType, BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { assembly }, null);

                    Point(managers, assembly.GetName().Name, assembly);
                }
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Ресурсы WPF не перенаправлены: " + ex.Message);
            }

            return new Unpin(managers, folder);
        }

        // Keys are the lower-cased assembly name, followed by the version if the URI has one.
        private static void Point(IDictionary managers, string name, Assembly assembly)
        {
            var lower = name.ToLowerInvariant();

            foreach (DictionaryEntry entry in managers)
            {
                if (entry.Key is string key && (key == lower || key.StartsWith(lower) && char.IsDigit(key[lower.Length])))
                    WrapperAssembly.SetValue(entry.Value, assembly);
            }
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

        private sealed class Unpin : IDisposable
        {
            private readonly IDictionary _managers;
            private readonly RibbonPreview.ShadowFolder _folder;

            public Unpin(IDictionary managers, RibbonPreview.ShadowFolder folder)
            {
                _managers = managers;
                _folder = folder;
            }

            public void Dispose()
            {
                if (_managers == null) return;

                try
                {
                    foreach (var name in _folder.Loaded.Select(a => a.GetName().Name).Distinct())
                    {
                        var live = RibbonPreview.LiveAssembly(name);
                        if (live != null) Point(_managers, name, live);
                    }
                }
                catch (Exception ex)
                {
                    ConsoleOutput.Debug("Ресурсы WPF не возвращены: " + ex.Message);
                }
            }
        }
    }
}
