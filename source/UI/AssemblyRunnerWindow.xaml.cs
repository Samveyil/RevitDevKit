using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace RevitScriptRunner.UI
{
    /// <summary>
    /// Окно со списком DLL и их команд. Логика — в AssemblyRunnerVM.
    ///
    /// VM живёт весь сеанс Revit: окно закрывается при запуске команды и при
    /// повторном открытии показывает тот же список.
    /// </summary>
    public partial class AssemblyRunnerWindow : Window
    {
        private static AssemblyRunnerWindow _instance;
        private static AssemblyRunnerVM _vm;

        private readonly AssemblyRunnerVM _model;

        public AssemblyRunnerWindow(AssemblyRunnerVM vm)
        {
            InitializeComponent();

            _model = vm;
            DataContext = vm;

            vm.CloseRequested += OnCloseRequested;
            Closed += (s, e) =>
            {
                _model.CloseRequested -= OnCloseRequested;
                _instance = null;
            };
        }

        private void OnCloseRequested(object sender, EventArgs e) => Close();

        /// <summary>Окно одно на сеанс Revit: повторное нажатие кнопки просто поднимает его.</summary>
        public static void ShowSingleton()
        {
            _vm ??= new AssemblyRunnerVM();

            if (_instance != null)
            {
                if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
                _instance.Activate();
                return;
            }

            _instance = new AssemblyRunnerWindow(_vm);

            // Привязываем к главному окну Revit, чтобы не терялось за ним.
            try
            {
                var helper = new WindowInteropHelper(_instance);
                helper.Owner = Process.GetCurrentProcess().MainWindowHandle;
            }
            catch
            {
                // не критично
            }

            _instance.Show();
        }

        public static void CloseIfOpen()
        {
            if (_instance == null) return;
            try { _instance.Close(); } catch { /* Revit уже закрывается */ }
            _instance = null;
        }
    }
}
