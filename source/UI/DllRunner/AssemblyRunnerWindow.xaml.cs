using RevitDevKit.UI.Shared;
using System;
using System.Windows;

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

        public static void ShowSingleton()
        {
            _vm ??= new AssemblyRunnerVM();
            Single.Show(() => new AssemblyRunnerWindow(_vm));
        }

        public static void CloseIfOpen() => Single.Close();
    }
}
