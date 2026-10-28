using Autodesk.Revit.UI;
using BIMPlugins.UI;
using RevitDevKit.Commands;
using RevitDevKit.Core;
using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;

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
                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromPluginFolder;

                ConsoleOutput.Install();
                RevitTaskRunner.Initialize();

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

            pullBtn.CreatePushButton<OpenAssemblyRunnerCmd, AlwaysAvailable>("DLL Runner")
                .SetToolTip("Загрузка внешних DLL и запуск их команд.")
                .SetLongDescription(
                    "Подключите собранную DLL и запустите из неё IExternalCommand. " +
                    "Команда берётся из актуальной DLL на диске: после пересборки достаточно запустить её снова."
                );

            pullBtn.CreatePushButton<ExecutePendingCmd, AlwaysAvailable>("Run Again")
                .SetToolTip("Повторить последний запуск в контексте настоящей команды Revit.");

            pullBtn.CreatePushButton<ShowConsolePaneCmd>("Console")
                .SetToolTip("Показать панель с консолью плагина.");
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
