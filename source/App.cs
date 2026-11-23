using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.UI;
using RevitDevKit.Commands;
using RevitDevKit.Core;
using RevitDevKit.Core.Addins;
using RevitDevKit.Core.Compiler;
using RevitDevKit.Core.Output;
using RevitDevKit.Core.Ribbon;
using RevitDevKit.Core.Running;
using RevitDevKit.Core.Show;
using RevitDevKit.Localization;
using System;
using System.IO;
using System.Reflection;

namespace RevitDevKit
{
    public class App : IExternalApplication
    {
        internal const string PanelName = "DevKit";

        public static string PluginDirectory
        {
            get { return Path.GetDirectoryName(typeof(App).Assembly.Location); }
        }

        public static string DataDirectory
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RevitDevKit"
                );
                
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Fresh builds resolve their own dependencies first, so it is registered before ours.
                RibbonPreview.Initialize(application);
                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromPluginFolder;

                ConsoleOutput.Install();

                // Revit's language first, so that a problem with settings.json is told in it; then as the settings say.
                var revitIsRussian = application.ControlledApplication.Language == Autodesk.Revit.ApplicationServices.LanguageType.Russian;
                Language.Apply(revitIsRussian, Language.Auto);
                DevKitSettings.Load();
                Language.Apply(revitIsRussian, DevKitSettings.LanguageSetting);

                RevitTaskRunner.Initialize();
                ShownGeometryServer.Register();
                LocateAddinFolders(application);

                BuildRibbon(application);
                RegisterConsolePane(application);

                ConsoleOutput.System(string.Format(RibbonStrings.Loaded, application.ControlledApplication.VersionNumber));
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("DevKit", RibbonStrings.LoadFailed + "\n\n" + ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            UI.ScriptEditor.ScriptEditorWindow.CloseIfOpen();
            UI.DllRunner.AssemblyRunnerWindow.CloseIfOpen();
            RibbonPreview.Shutdown();
            ShownGeometryServer.Unregister();
            ConsoleOutput.Uninstall();
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromPluginFolder;
            return Result.Succeeded;
        }

        private static void BuildRibbon(UIControlledApplication application)
        {
            var panel = application.CreateOrSelectPanel(PanelName);

            var pullBtn = panel.CreatePulldownButton(PanelName)
                .SetLargeImage("DevKit_32.png")
                .SetImage("DevKit_16.png")
                .SetToolTip(RibbonStrings.PanelToolTip)
                .SetLongDescription(RibbonStrings.PanelDescription);

            // The buttons' names stay English in every language; their tooltips follow DevKit's language.
            pullBtn.CreatePushButton<OpenScriptEditorCmd>("Script Editor")
                .SetToolTip(RibbonStrings.ScriptEditorToolTip)
                .SetLongDescription(RibbonStrings.ScriptEditorDescription);

            // The button's internal name comes from its command class, so renaming the text keeps users' shortcuts.
            pullBtn.CreatePushButton<OpenAssemblyRunnerCmd, AlwaysAvailable>("Add-in Explorer")
                .SetToolTip(RibbonStrings.ExplorerToolTip)
                .SetLongDescription(RibbonStrings.ExplorerDescription);

            // Renamed from Run Again: the internal name comes from ExecutePendingCmd, so shortcuts stay.
            pullBtn.CreatePushButton<ExecutePendingCmd, AlwaysAvailable>("Repeat Last")
                .SetToolTip(RibbonStrings.RepeatToolTip)
                .SetLongDescription(RibbonStrings.RepeatDescription);

            pullBtn.CreatePushButton<DebugLastCmd, AlwaysAvailable>("Repeat Last: Debug")
                .SetToolTip(RibbonStrings.DebugToolTip)
                .SetLongDescription(RibbonStrings.DebugDescription);

            pullBtn.AddSeparator();

            pullBtn.CreatePushButton<ShowConsolePaneCmd>("Console")
                .SetToolTip(RibbonStrings.ConsoleToolTip)
                .SetLongDescription(RibbonStrings.ConsoleDescription);
        }

        // The folders Revit loads .addin files from, for the Startup and Dependencies tabs of Add-in Explorer.
        private static void LocateAddinFolders(UIControlledApplication application)
        {
            var revit = application.ControlledApplication;
            try
            {
                AddinManifests.Locate(revit.CurrentUserAddinsLocation, revit.AllUsersAddinsLocation, revit.VersionNumber);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug(string.Format(RibbonStrings.AddinFoldersFailed, ex.Message));
                AddinManifests.Locate(null, null, revit.VersionNumber);
            }
        }

        private static void RegisterConsolePane(UIControlledApplication application)
        {
            try
            {
                UI.Console.ConsolePane.Register(application);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, RibbonStrings.ConsolePaneRegisterFailed);
            }
        }

        // Revit ships its own Roslyn and other libraries. When the loader cannot find an assembly DevKit needs, it falls
        // back to the copy next to DevKit - but never to an older version: that would load fine and then fail later
        // with MissingMethodException. AssemblyResolve is raised for every add-in in Revit, so a request from another
        // add-in is left to that add-in: DevKit never hands it its own copy.
        private static Assembly ResolveFromPluginFolder(object sender, ResolveEventArgs args)
        {
            try
            {
                var requester = RequesterOf(args.RequestingAssembly);
                if (requester == Requester.Foreign) return null;

                var requested = new AssemblyName(args.Name);
                var candidate = Path.Combine(PluginDirectory ?? ".", requested.Name + ".dll");

                if (!File.Exists(candidate)) return null;

                var found = AssemblyName.GetAssemblyName(candidate);

                if (requested.Version != null && found.Version != null && found.Version < requested.Version)
                {
                    if (requester == Requester.DevKit)
                        ConsoleOutput.Error(string.Format(RibbonStrings.OlderAssembly, requested.Name, found.Version, requested.Version));
                    return null;
                }

                return Assembly.LoadFrom(candidate);
            }
            catch
            {
                return null;
            }
        }

        private enum Requester
        {
            // An assembly from DevKit's folder, or a script DevKit compiled.
            DevKit,

            // Nobody named, or a DLL Add-in Explorer loaded from bytes: DevKit's copies have always served these.
            Unknown,

            // An assembly another add-in or Revit loaded from its own folder.
            Foreign
        }

        private static Requester RequesterOf(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic) return Requester.Unknown;

            if (ScriptAssemblyLoader.TryGetOrigin(assembly, out var origin))
                return string.IsNullOrEmpty(origin) ? Requester.DevKit : Requester.Unknown;

            var location = assembly.Location;
            if (string.IsNullOrEmpty(location)) return Requester.Unknown;

            return string.Equals(Path.GetDirectoryName(location), PluginDirectory, StringComparison.OrdinalIgnoreCase)
                ? Requester.DevKit
                : Requester.Foreign;
        }
    }

    public class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => applicationData != null;
    }
}
