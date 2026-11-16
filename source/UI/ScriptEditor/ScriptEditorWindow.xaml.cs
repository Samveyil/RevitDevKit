using RevitDevKit.UI.Shared;
using System.Windows;

namespace RevitDevKit.UI.ScriptEditor
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

        // The AI list stays open on a click inside it, so that its Подробная checkbox can be ticked; its items close it.
        private void AiMenuItem_Click(object sender, RoutedEventArgs e) => AiPopup.IsPopupOpen = false;
    }
}
