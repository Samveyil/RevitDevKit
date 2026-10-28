using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMPlugins.UI;
using RevitDevKit.Commands;
using RevitDevKit.Core;

namespace RevitDevKit
{
    /// <summary>
    /// Точка входа надстройки: лента, перехват консоли, мост в поток Revit.
    /// </summary>
    public class App : IExternalApplication
    {
        internal const string PanelName = "DevKit";

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
            UI.ScriptEditorWindow.CloseIfOpen();
            UI.AssemblyRunnerWindow.CloseIfOpen();
            ConsoleOutput.Uninstall();
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromPluginFolder;
            return Result.Succeeded;
        }

        private static void BuildRibbon(UIControlledApplication application)
        {
            var panel = application.CreateOrSelectPanel(PanelName);

            var pullBtn = panel.CreatePulldownButton(PanelName)
                .SetToolTip("Revit DevKit — инструменты разработчика: скрипты, запуск команд из DLL, консоль.");

            pullBtn.CreatePushButton<OpenScriptEditorCmd, AlwaysAvailable>("Script Editor")
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

            pullBtn.CreatePushButton<ShowConsolePaneCmd, AlwaysAvailable>("Console")
                .SetToolTip("Показать панель с консолью плагина.");

            SetPulldownIcons(application);
        }

        /// <summary>
        /// Ставит иконки выпадающей кнопке плагина. Кнопку ищем через Revit API, а картинки
        /// берём из встроенных ресурсов сборки — так не нужны ни файлы рядом с DLL, ни pack URI.
        /// </summary>
        private static void SetPulldownIcons(UIControlledApplication application)
        {
            try
            {
                var pulldown = application.GetRibbonPanels(Tab.AddIns)
                    .Where(p => p.Name == PanelName)
                    .SelectMany(p => p.GetItems())
                    .OfType<PulldownButton>()
                    .FirstOrDefault(b => b.Name == PanelName);

                if (pulldown == null) return;

                pulldown.LargeImage = LoadIcon("DevKit_32.png");
                pulldown.Image = LoadIcon("DevKit_16.png");
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Не удалось поставить иконки на ленту: " + ex.Message);
            }
        }

        /// <summary>Иконка из Resources\Icons, встроенная в сборку (см. EmbeddedResource в .csproj).</summary>
        internal static BitmapSource LoadIcon(string fileName)
        {
            using (var stream = typeof(App).Assembly.GetManifestResourceStream("Icons." + fileName))
            {
                if (stream == null) return null;

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        private static void RegisterConsolePane(UIControlledApplication application)
        {
            try
            {
                UI.ConsolePane.Register(application);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Exception(ex, "Не удалось зарегистрировать панель консоли:");
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

    public class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => applicationData != null;
    }
}