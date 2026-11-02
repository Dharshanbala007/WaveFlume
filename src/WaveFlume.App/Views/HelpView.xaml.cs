using System.Windows.Controls;

namespace WaveFlume.App.Views;

public partial class HelpView : UserControl
{
    public HelpView()
    {
        InitializeComponent();
        VersionText.Text = $"Version {typeof(HelpView).Assembly.GetName().Version} - .NET {Environment.Version}";
    }
}
