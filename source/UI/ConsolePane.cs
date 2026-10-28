using System;
using Autodesk.Revit.UI;
using RevitDevKit.Core;

namespace RevitDevKit.UI
{
    // Dockable panes can be registered only in OnStartup and shown only from an API context.
    public sealed class ConsolePane : IDockablePaneProvider
    {
        public static readonly DockablePaneId Id = new DockablePaneId(new Guid("6b0f6c2e-5a2d-4c8e-9a43-2f1d8e7b5c91"));

        public const string Title = "DevKit Console";

        public static void Register(UIControlledApplication application)
        {
            application.RegisterDockablePane(Id, Title, new ConsolePane());
        }

        public static void Show(UIApplication application)
        {
            application.GetDockablePane(Id).Show();
        }

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
