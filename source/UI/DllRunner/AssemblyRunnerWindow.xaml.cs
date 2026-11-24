using RevitDevKit.UI.Shared;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RevitDevKit.UI.DllRunner
{
    public partial class AssemblyRunnerWindow : Window
    {
        private static readonly SingleWindow<AssemblyRunnerWindow> Single = new SingleWindow<AssemblyRunnerWindow>();

        // The VM outlives the window: it closes on every run, and on .NET Framework the loaded
        // assemblies stay in memory anyway, so the list should stay too.
        private static AssemblyRunnerVM _vm;

        private readonly AssemblyRunnerVM _model;

        public AssemblyRunnerWindow(AssemblyRunnerVM vm)
        {
            InitializeComponent();

            _model = vm;
            DataContext = vm;

            vm.CloseRequested += OnCloseRequested;
            Closed += (s, e) => _model.CloseRequested -= OnCloseRequested;
            vm.OnWindowOpened();
        }

        private void OnCloseRequested(object sender, EventArgs e) => Close();

        // A right click selects the node under it first, as in Visual Studio, and the context menu acts on that node.
        // Used by the Commands and Dependencies trees. Without it the menu of a DLL stays grey: WPF checks its items before they get the node and does not check
        // them again, while a new selection makes the commands check again. Nodes that cannot be selected are skipped.
        private void SelectOnRightClick(object sender, MouseButtonEventArgs e)
        {
            var current = e.OriginalSource as DependencyObject;
            while (current != null && !(current is TreeViewItem))
                current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);

            if (current is TreeViewItem item && item.Focusable)
            {
                item.IsSelected = true;
                item.Focus();
            }
        }

        public static void ShowSingleton()
        {
            _vm ??= new AssemblyRunnerVM();
            Single.Show(() => new AssemblyRunnerWindow(_vm));
        }

        public static void CloseIfOpen() => Single.Close();
    }
}
