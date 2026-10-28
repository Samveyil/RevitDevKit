using System.Windows;

namespace RevitDevKit.UI
{
    public partial class ScriptEditorWindow : Window
    {
        private static readonly SingleWindow<ScriptEditorWindow> Single = new SingleWindow<ScriptEditorWindow>();

        private readonly ScriptEditorVM _vm;

        public ScriptEditorWindow(ScriptEditorVM vm)
        {
            InitializeComponent();

            _vm = vm;
            DataContext = vm;
        }

        public static void ShowSingleton() =>
            Single.Show(() => new ScriptEditorWindow(new ScriptEditorVM()), w => w._vm.RefreshContext());

        public static void CloseIfOpen() => Single.Close();
    }
}
