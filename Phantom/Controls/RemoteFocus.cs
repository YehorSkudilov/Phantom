namespace Phantom.Controls;

/// <summary>
/// TV remotes and keyboards. On Android anything with a tap can be reached with the arrows and pressed with OK (see
/// Platforms/Android/DpadFocus.cs); <see cref="SkipProperty"/> leaves a view out: a backdrop whose tap only closes or
/// toggles something, which the remote would otherwise stop on.
/// </summary>
public static class RemoteFocus
{
    public static readonly BindableProperty SkipProperty =
        BindableProperty.CreateAttached("Skip", typeof(bool), typeof(RemoteFocus), false);

    public static bool GetSkip(BindableObject view) => (bool)view.GetValue(SkipProperty);

    public static void SetSkip(BindableObject view, bool value) => view.SetValue(SkipProperty, value);
}
