namespace Phantom.Views.Parts;

public partial class CollectionsView : ContentView
{
    public CollectionsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => GridLayout.Span = Math.Max(2, (int)(Width / 200));
    }
}
