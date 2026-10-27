using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using RevitScriptRunner.Core;

namespace RevitScriptRunner
{
    /// <summary>
    /// Точка входа надстройки: лента, перехват консоли, мост в поток Revit.
    /// </summary>
    public class App : IExternalApplication
    {
        internal const string TabName = "Script Runner";
        internal const string PanelName = "C#";

        public static string PluginDirectory
        {
            get { return Path.GetDirectoryName(typeof(App).Assembly.Location); }
        }

        /// <summary>Пользовательские данные: сохранённые скрипты, настройки.</summary>
        public static string DataDirectory
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RevitScriptRunner");
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

                ConsoleOutput.System("Script Runner загружен. Revit " +
                                     application.ControlledApplication.VersionNumber + ".");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Script Runner", "Не удалось загрузить надстройку:\n\n" + ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            UI.RunnerWindow.CloseIfOpen();
            UI.AssemblyRunnerWindow.CloseIfOpen();
            ConsoleOutput.Uninstall();
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromPluginFolder;
            return Result.Succeeded;
        }

        private static void BuildRibbon(UIControlledApplication application)
        {
            try { application.CreateRibbonTab(TabName); }
            catch (Exception) { /* вкладка уже создана другой надстройкой */ }

            var panel = application.CreateRibbonPanel(TabName, PanelName);
            var assemblyPath = typeof(App).Assembly.Location;

            var openButton = new PushButtonData(
                "RsrOpen",
                "Script\nRunner",
                assemblyPath,
                typeof(Commands.OpenRunnerCommand).FullName)
            {
                ToolTip = "Редактор C#-скриптов",
                LongDescription =
                    "Пишите код прямо в Revit и запускайте его без пересборки надстройки. " +
                    "Весь вывод — Console.WriteLine, Debug, исключения — попадает в консоль плагина."
            };

            SetIcon(panel.AddItem(openButton) as PushButton, "runner");

            var assembliesButton = new PushButtonData(
                "RsrAssemblies",
                "Запуск\nDLL",
                assemblyPath,
                typeof(Commands.OpenAssemblyRunnerCommand).FullName)
            {
                ToolTip = "Загрузка внешних DLL и запуск их команд",
                LongDescription =
                    "Подключите собранную DLL и запустите из неё IExternalCommand. " +
                    "Команда берётся из актуальной DLL на диске: после пересборки достаточно запустить её снова."
            };

            SetIcon(panel.AddItem(assembliesButton) as PushButton, "assemblies");

            var consoleButton = new PushButtonData(
                "RsrConsole",
                "Консоль",
                assemblyPath,
                typeof(Commands.ShowConsolePaneCommand).FullName)
            {
                ToolTip = "Показать панель с консолью плагина",
                LongDescription =
                    "Сюда попадает вывод запущенных команд: Console.WriteLine, Debug, исключения. " +
                    "Панель можно прикрепить к любому краю окна Revit."
            };

            SetIcon(panel.AddItem(consoleButton) as PushButton, "console");

            // Эта кнопка нужна не только пользователю: без места на ленте у команды
            // нет идентификатора, который принимает UIApplication.PostCommand, —
            // а именно так плагин получает живой ExternalCommandData.
            var runAgainButton = new PushButtonData(
                Core.RevitCommandBridge.RunButtonName,
                "Ещё\nраз",
                assemblyPath,
                typeof(Commands.ExecutePendingCommand).FullName)
            {
                ToolTip = "Повторить последний запуск в контексте настоящей команды Revit",
                LongDescription =
                    "Script Runner использует эту команду, чтобы запускать чужие IExternalCommand " +
                    "так же, как это делает сам Revit. Нажатая вручную, она повторяет последний запуск."
            };

            SetIcon(panel.AddItem(runAgainButton) as PushButton, "run");
        }

        private static void RegisterConsolePane(UIControlledApplication application)
        {
            try
            {
                UI.ConsolePane.Register(application);
            }
            catch (Exception ex)
            {
                // Без панели плагин работает: вывод виден в консоли окна скриптов.
                ConsoleOutput.Exception(ex, "Не удалось зарегистрировать панель консоли:");
            }
        }

        private static void SetIcon(PushButton button, string name)
        {
            if (button == null) return;

            button.LargeImage = LoadIcon(name + ".png");
            button.Image = LoadIcon(name + ".small.png");
        }

        private static BitmapImage LoadIcon(string fileName)
        {
            try
            {
                var path = Path.Combine(PluginDirectory ?? ".", "Icons", fileName);
                return File.Exists(path) ? new BitmapImage(new Uri(path, UriKind.Absolute)) : null;
            }
            catch
            {
                return null; // иконка — не повод падать при загрузке надстройки
            }
        }

        /// <summary>
        /// В процессе Revit крутятся свои версии Roslyn и прочих библиотек. Если
        /// запрошенную сборку загрузчик не нашёл — берём ту, что лежит рядом с плагином.
        ///
        /// Версию при этом проверяем. Подсунуть более старую сборку, чем запрошена, —
        /// худшее из решений: загрузка пройдёт, а потом вызов уйдёт в метод, которого
        /// там нет, и мы получим MissingMethodException вдалеке от причины. Лучше
        /// отказаться и сказать, что именно не сходится.
        /// </summary>
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
}
