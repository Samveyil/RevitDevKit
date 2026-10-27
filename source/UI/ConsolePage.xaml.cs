using System.Windows.Controls;

namespace RevitScriptRunner.UI
{
    /// <summary>Содержимое прикрепляемой панели с консолью плагина (см. ConsolePane).</summary>
    public partial class ConsolePage : Page
    {
        public ConsolePage()
        {
            InitializeComponent();
            DataContext = new ConsoleVM();
        }
    }
}
