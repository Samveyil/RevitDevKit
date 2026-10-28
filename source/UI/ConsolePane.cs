using System;
using Autodesk.Revit.UI;
using RevitScriptRunner.Core;

namespace RevitScriptRunner.UI
{
    /// <summary>
    /// Консоль плагина как прикрепляемая панель Revit.
    ///
    /// Revit принимает регистрацию панели только в OnStartup, а показать её можно
    /// только в контексте API — из команды ленты или из выполняемого задания.
    /// Панель создаётся один раз и живёт весь сеанс, поэтому копит вывод и скрытой.
    /// </summary>
    public sealed class ConsolePane : IDockablePaneProvider
    {
        public static readonly DockablePaneId Id = new DockablePaneId(new Guid("6b0f6c2e-5a2d-4c8e-9a43-2f1d8e7b5c91"));

        public const string Title = "Script Runner — консоль";

        public static void Register(UIControlledApplication application)
        {
            application.RegisterDockablePane(Id, Title, new ConsolePane());
        }

        public static void Show(UIApplication application)
        {
            application.GetDockablePane(Id).Show();
        }

        /// <summary>Показывает панель, если это возможно; ошибка здесь не повод срывать запуск.</summary>
        public static void TryShow(UIApplication application)
        {
            if (application == null) return;

            try
            {
                Show(application);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Не удалось показать панель консоли: " + ex.Message);
            }
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = new ConsolePage();
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Bottom };
            data.VisibleByDefault = false;
        }
    }
}