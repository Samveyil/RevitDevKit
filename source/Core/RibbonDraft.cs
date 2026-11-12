using Autodesk.Revit.UI;
using BIMPlugins.UI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Adw = Autodesk.Windows;
using BIMPlugins.ExtStorage.Extensions;

namespace RevitDevKit.Core
{
    // OnStartup of the new build runs against a name registry without any panels, so the add-in
    // builds its ribbon from scratch next to the live one. Live custom tabs stay registered for
    // code that expects them (GetRibbonPanels, "create or select").
    internal sealed class RibbonDraft
    {
        public Dictionary<string, Dictionary<string, RibbonPanel>> Registry { get; } =
            new Dictionary<string, Dictionary<string, RibbonPanel>>
            {
                [RibbonAccess.AddInsTab] = new Dictionary<string, RibbonPanel>(),
                [RibbonAccess.AnalyzeTab] = new Dictionary<string, RibbonPanel>()
            };

        public List<Adw.RibbonTab> NewTabs { get; private set; } = new List<Adw.RibbonTab>();

        // Every panel the draft created through the API, also one it took off the tab and out of the registry
        // after moving its buttons to a panel of its own: those buttons are on the ribbon all the same.
        public List<RibbonPanel> CreatedPanels { get; private set; } = new List<RibbonPanel>();

        // Panels the add-in put on tabs that were already there through AdWindows itself, not the API
        // (a panel of its own on Modify, say): the API registry knows nothing of them.
        public List<Tuple<Adw.RibbonTab, Adw.RibbonPanel>> ForeignPanels { get; private set; } = new List<Tuple<Adw.RibbonTab, Adw.RibbonPanel>>();

        public static RibbonDraft Run(Action startup)
        {
            DraftPatches.Install();

            var ribbon = RibbonAccess.Ribbon;
            var live = RibbonAccess.Registry;
            var tabsBefore = ribbon.Tabs.ToList();
            var panelsBefore = tabsBefore.ToDictionary(t => t, t => new HashSet<Adw.RibbonPanel>(t.Panels));

            var draft = new RibbonDraft();
            foreach (var key in live.Keys.Where(RibbonAccess.IsCustomTab))
            {
                if (ribbon.FindTab(key) != null)
                    draft.Registry[key] = new Dictionary<string, RibbonPanel>();
            }

            Exception failure = null;

            RibbonAccess.Registry = draft.Registry;
            DraftPatches.Created.Clear();
            DraftPatches.IsActive = true;
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
                DraftPatches.IsActive = false;
                RibbonAccess.Registry = live;
                draft.CreatedPanels = DraftPatches.Created.Distinct().ToList();
                DraftPatches.Created.Clear();
            }

            draft.NewTabs = ribbon.Tabs.Where(t => !tabsBefore.Contains(t)).ToList();

            var apiPanels = new HashSet<Adw.RibbonPanel>(draft.Registry.Values.SelectMany(p => p.Values).Select(p => p.ToAWRibbonPanel()));
            draft.ForeignPanels = ribbon.Tabs
                .Where(panelsBefore.ContainsKey)
                .SelectMany(t => t.Panels.Where(p => !panelsBefore[t].Contains(p) && !apiPanels.Contains(p)).Select(p => Tuple.Create(t, p)))
                .ToList();

            if (failure != null)
            {
                draft.Discard();
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return draft;
        }

        public IEnumerable<RibbonItem> Items()
        {
            var panels = Registry.Values.SelectMany(p => p.Values).Concat(CreatedPanels).Distinct();
            foreach (var item in panels.SelectMany(p => RibbonAccess.Items(p).Values))
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
                RibbonAccess.Detach(panel.ToAWRibbonPanel());

            foreach (var tab in NewTabs)
                RibbonAccess.Ribbon.Tabs.Remove(tab);

            foreach (var foreign in ForeignPanels)
                foreign.Item1.Panels.Remove(foreign.Item2);
        }

        internal static string FileName(string path)
        {
            try { return path.IsNullOrEmpty() ? null : Path.GetFileName(path); }
            catch { return null; }
        }
    }

    // The draft OnStartup runs in a Revit that already has the add-in's tabs and dockable panes.
    // While it runs, creating a tab or panel that exists is skipped (the panel is returned), and
    // RegisterDockablePane is skipped: Revit refuses to register a pane ID twice.
    internal static class DraftPatches
    {
        private static Harmony _harmony;

        public static bool IsActive { get; set; }

        // Panels CreateRibbonPanel returned while the draft ran.
        public static List<RibbonPanel> Created { get; } = new List<RibbonPanel>();

        public static void Install()
        {
            if (_harmony != null) return;
            _harmony = new Harmony("RevitDevKit.RibbonPreview");

            var app = typeof(UIControlledApplication);
            Patch(app.GetMethod(nameof(UIControlledApplication.CreateRibbonTab), new[] { typeof(string) }), nameof(CreateRibbonTab));
            Patch(app.GetMethod(nameof(UIControlledApplication.CreateRibbonPanel), new[] { typeof(string), typeof(string) }), nameof(CreatePanelInTab), nameof(PanelCreated));
            Patch(app.GetMethod(nameof(UIControlledApplication.CreateRibbonPanel), new[] { typeof(string) }), nameof(CreatePanelInAddIns), nameof(PanelCreated));
            Patch(app.GetMethod(nameof(UIControlledApplication.CreateRibbonPanel), new[] { typeof(Tab), typeof(string) }), nameof(CreatePanelInBuiltInTab), nameof(PanelCreated));
            Patch(app.GetMethod(nameof(UIControlledApplication.RegisterDockablePane)), nameof(RegisterDockablePane));
        }

        private static void Patch(MethodInfo original, string prefix, string postfix = null)
        {
            try
            {
                _harmony.Patch(original,
                    prefix: new HarmonyMethod(typeof(DraftPatches).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(DraftPatches).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception ex)
            {
                ConsoleOutput.Warning("Не удалось перехватить " + (original?.Name ?? prefix) + ": " + ex.Message);
            }
        }

        private static void PanelCreated(RibbonPanel __result)
        {
            if (IsActive && __result != null) Created.Add(__result);
        }

        // Prefixes: returning false skips the original method.

        private static bool CreateRibbonTab(string __0)
        {
            if (!IsActive || RibbonAccess.Ribbon.FindTab(__0) == null) return true;

            var registry = RibbonAccess.Registry;
            if (!registry.ContainsKey(__0))
                registry[__0] = new Dictionary<string, RibbonPanel>();

            return false;
        }

        private static bool CreatePanelInTab(string __0, string __1, ref RibbonPanel __result) =>
            !TryGetPanel(__0, __1, ref __result);

        private static bool CreatePanelInAddIns(string __0, ref RibbonPanel __result) =>
            !TryGetPanel(RibbonAccess.AddInsTab, __0, ref __result);

        private static bool CreatePanelInBuiltInTab(Tab __0, string __1, ref RibbonPanel __result) =>
            !TryGetPanel(__0 == Tab.Analyze ? RibbonAccess.AnalyzeTab : RibbonAccess.AddInsTab, __1, ref __result);

        private static bool TryGetPanel(string tab, string panel, ref RibbonPanel result)
        {
            if (!IsActive) return false;
            if (!RibbonAccess.Registry.TryGetValue(tab, out var panels) || !panels.TryGetValue(panel, out var existing)) return false;

            result = existing;
            return true;
        }

        private static bool RegisterDockablePane(string __1)
        {
            if (!IsActive) return true;

            ConsoleOutput.Debug("RegisterDockablePane «" + __1 + "» пропущен: панель уже зарегистрирована при запуске Revit.");
            return false;
        }
    }
}
