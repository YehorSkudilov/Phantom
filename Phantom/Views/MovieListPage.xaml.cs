using Phantom.ViewModels;

namespace Phantom.Views;

/// <summary>A collection's movies (and the watchlist when opened from Home's "See all").</summary>
public partial class MovieListPage : ContentPage
{
    public MovieListPage(MovieListViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
