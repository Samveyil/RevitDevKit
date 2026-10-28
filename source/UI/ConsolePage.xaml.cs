using System.Windows.Controls;

namespace RevitDevKit.UI
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
