using System.Windows.Controls;

namespace RevitDevKit.UI
{
    public partial class ConsolePage : Page
    {
        public ConsolePage()
        {
            InitializeComponent();
            DataContext = new ConsoleVM();
        }
    }
}
