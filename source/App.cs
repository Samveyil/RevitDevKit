using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.UI;
using RevitDevKit.Commands;
using RevitDevKit.Core.Addins;
using RevitDevKit.Core.Output;
using RevitDevKit.Core.Ribbon;
using RevitDevKit.Core.Running;
using RevitDevKit.Core.Show;
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
                RevitTaskRunner.Initialize();
                ShownGeometryServer.Register();
                LocateAddinFolders(application);

                BuildRibbon(application);
                RegisterConsolePane(application);

                ConsoleOutput.System("DevKit загружен. Revit " +
                                     application.ControlledApplication.VersionNumber + ".");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("DevKit", "Не удалось загрузить надстройку:\n\n" + ex);
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
                .SetToolTip("DevKit — инструменты разработчика: скрипты, запуск команд из DLL, консоль.");

            pullBtn.CreatePushButton<OpenScriptEditorCmd>("Script Editor")
                .SetToolTip("Редактор C#-скриптов.")
                .SetLongDescription(
                    "Пишите код прямо в Revit и запускайте его без пересборки надстройки. " +
                    "Весь вывод — Console.WriteLine, Debug, исключения — попадает в консоль плагина."
                );

            // The button's internal name comes from its command class, so renaming the text keeps users' shortcuts.
            pullBtn.CreatePushButton<OpenAssemblyRunnerCmd, AlwaysAvailable>("Add-in Explorer")
                .SetToolTip("Надстройки и их DLL: запуск команд, лента по свежей сборке, автозагрузка, конфликты версий.")
                .SetLongDescription(
                    "Подключите собранную DLL и запустите из неё IExternalCommand: команда берётся из актуальной DLL на диске. " +
                    "Здесь же видно, как изменится лента надстройки, какие надстройки Revit загружает при запуске " +
                    "и какие DLL у них расходятся по версиям."
                );

            // Renamed from Run Again: the internal name comes from ExecutePendingCmd, so shortcuts stay.
            pullBtn.CreatePushButton<ExecutePendingCmd, AlwaysAvailable>("Repeat Last")
                .SetToolTip("Повторить последний запуск: скрипт из Script Editor или команду из Add-in Explorer.")
                .SetLongDescription(
                    "Команда из DLL берётся из файла на диске, так что после пересборки запустится свежая версия. " +
                    "Скрипт повторяется в том виде, в каком его запустили; правки в редакторе применятся после F5."
                );

            pullBtn.CreatePushButton<DebugLastCmd, AlwaysAvailable>("Repeat Last: Debug")
                .SetToolTip("Повторить последнюю команду из Add-in Explorer под отладчиком: подключить открытую Visual Studio.")
                .SetLongDescription(
                    "Точки останова, поставленные в проекте в Visual Studio, сработают. Visual Studio должна быть открыта " +
                    "с решением надстройки. Скрипты отлаживаются в Script Editor, кнопкой с жуком."
                );

            pullBtn.AddSeparator();

            pullBtn.CreatePushButton<ShowConsolePaneCmd>("Console")
                .SetToolTip("Показать панель с консолью плагина, а если она открыта — скрыть.");
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
                ConsoleOutput.Debug("Папки надстроек Revit не определились: " + ex.Message);
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
                ConsoleOutput.Exception(ex, "Не удалось зарегистрировать панель консоли:");
            }
        }

        // Revit ships its own Roslyn and other libraries. When the loader cannot find an
        // assembly, fall back to the copy next to the add-in - but never to an older version:
        // that would load fine and then fail later with MissingMethodException.
        private static Assembly ResolveFromPluginFolder(object sender, ResolveEventArgs args)
        {
            try
            {
                var requested = new AssemblyName(args.Name);
                var candidate = Path.Combine(PluginDirectory ?? ".", requested.Name + ".dll");

                if (!File.Exists(candidate)) return null;

                var found = AssemblyName.GetAssemblyName(candidate);

                if (requested.Version != null && found.Version != null && found.Version < requested.Version)
                {
                    ConsoleOutput.Error(
                        "Рядом с плагином лежит " + requested.Name + " версии " + found.Version +
                        ", а требуется " + requested.Version + ". Соберите и разложите один и тот же набор сборок.");
                    return null;
                }

                return Assembly.LoadFrom(candidate);
            }
            catch
            {
                return null;
            }
        }
    }

    public class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => applicationData != null;
    }
}
