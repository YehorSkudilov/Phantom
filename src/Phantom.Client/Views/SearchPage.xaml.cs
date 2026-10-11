using Phantom.ViewModels;
using Phantom.Shared;

namespace Phantom.Views;

public partial class SearchPage : ContentPage
{
    private readonly SearchViewModel _vm;

    public SearchPage(SearchViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        // BindableLayout rows have no selection; open a movie on tap instead.
        MovieList.ChildAdded += (_, e) =>
        {
            if (e.Element is View view)
                view.GestureRecognizers.Add(new TapGestureRecognizer
                {
                    Command = new Command(() => _vm.OpenMovieCommand.Execute(view.BindingContext as MovieSummaryDto)),
                });
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Box.Focus();
    }
}
