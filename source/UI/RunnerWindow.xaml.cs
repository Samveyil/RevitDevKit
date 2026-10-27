using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace RevitScriptRunner.UI
{
    /// <summary>
    /// Логика окна — в RunnerVM, консоль — в ConsoleView. Здесь остался только
    /// жизненный цикл окна.
    /// </summary>
    public partial class RunnerWindow : Window
    {
        private static RunnerWindow _instance;

        private readonly RunnerVM _vm;

        public RunnerWindow(RunnerVM vm)
        {
            InitializeComponent();

            _vm = vm;
            DataContext = vm;

            Closed += OnWindowClosed;
        }

        /// <summary>Окно одно на сеанс Revit: повторное нажатие кнопки просто поднимает его.</summary>
        public static void ShowSingleton()
        {
            if (_instance != null)
            {
                _instance._vm.RefreshContext();
                if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
                _instance.Activate();
                return;
            }

            _instance = new RunnerWindow(new RunnerVM());

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

            _instance._vm.RefreshContext();
            _instance.Show();
        }

        public static void CloseIfOpen()
        {
            if (_instance == null) return;
            try { _instance.Close(); } catch { /* Revit уже закрывается */ }
            _instance = null;
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            _instance = null;
        }
    }
}
