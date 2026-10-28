using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace RevitDevKit.UI.Shared
{
    // One modeless window per Revit session: showing it again brings the open one to front.
    internal sealed class SingleWindow<TWindow> where TWindow : Window
    {
        private TWindow _window;

        // beforeShow runs for both an already open window and a newly created one.
        public void Show(Func<TWindow> create, Action<TWindow> beforeShow = null)
        {
            if (_window != null)
            {
                beforeShow?.Invoke(_window);

                if (_window.WindowState == WindowState.Minimized)
                    _window.WindowState = WindowState.Normal;

                _window.Activate();
                return;
            }

            var window = create();
            beforeShow?.Invoke(window);
            window.Closed += (s, e) =>
            {
                if (_window == window) _window = null;
            };

            // Owned by Revit's main window so it never hides behind it.
            try
            {
                new WindowInteropHelper(window).Owner = Process.GetCurrentProcess().MainWindowHandle;
            }
            catch { } // owning the window is cosmetic

            _window = window;
            window.Show();
        }

        public void Close()
        {
            var window = _window;
            _window = null;

            try { window?.Close(); } catch { } // Revit may already be shutting down
        }
    }
}
