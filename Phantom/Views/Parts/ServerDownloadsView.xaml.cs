namespace Phantom.Views.Parts;

public partial class ServerDownloadsView : ContentView
{
    public ServerDownloadsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => PosterGrid.Span = Math.Max(3, (int)(Width / 140));
    }
}
