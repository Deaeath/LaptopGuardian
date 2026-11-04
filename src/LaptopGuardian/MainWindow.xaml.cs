using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace LaptopGuardian;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        // A page switch starts at the top. Scroll on the next frame, never inside the click.
        vm.PageChanged += () => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Scroller.ScrollToTop());
    }

    /// <summary>Closing the window keeps LaptopGuardian watching from the notification area.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
