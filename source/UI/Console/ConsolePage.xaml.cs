using Autodesk.Revit.UI;
using RevitDevKit.Core.Output;
using System;
using System.Windows.Controls;

namespace RevitDevKit.UI.Console
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

        // The Console button: shows the pane, or hides it when it is already open.
        public static void Toggle(UIApplication application)
        {
            var pane = application.GetDockablePane(Id);
            if (pane.IsShown())
                pane.Hide();
            else
                pane.Show();
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
            // The size the pane docks with, more than Revit's default of 200, so its first opening shows some output.
            // Revit uses it when docking only: the user can still drag the pane smaller, and the API has no way to stop it.
            // Like the position, it counts for a Revit that has not saved a layout of this pane yet.
            var state = new DockablePaneState { DockPosition = DockPosition.Bottom };
            state.SetFloatingRectangle(new Autodesk.Revit.DB.Rectangle(200, 200, 1100, 500));
#if R2020_OR_GREATER
            state.MinimumHeight = 250;
            state.MinimumWidth = 400;
#endif
            data.InitialState = state;
            data.VisibleByDefault = false;
        }
    }

    public partial class ConsolePage : Page
    {
        public ConsolePage()
        {
            InitializeComponent();
            DataContext = new ConsoleVM();
        }
    }
}
