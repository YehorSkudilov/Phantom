using AppSkeleton;

namespace Phantom.Views;

/// <summary>
/// A tab hosted in <see cref="MainPage"/>'s CView. Tabs lay themselves out edge to edge (PageBase's side padding is
/// dropped) and end their scrolling content with <see cref="NavBarInset"/> of space (a CollectionView footer, or
/// bottom padding) so it clears the nav bar.
/// </summary>
public class TabView : PageBase
{
    /// <summary>The floating nav bar's height plus its bottom margin (see MainPage.xaml).</summary>
    public const double NavBarInset = 84;

    public TabView()
    {
        Padding = 0;
    }
}
