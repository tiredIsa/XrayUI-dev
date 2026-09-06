using Microsoft.UI.Xaml;

namespace XrayUI.Helpers;

public static partial class LocalizationBindings
{
    // A native XAML tree can outlive its managed wrappers. A ConditionalWeakTable
    // alone therefore loses callbacks for unnamed elements after a collection.
    // Store the scope on the native element as well, so WinUI's reference tracker
    // manages its lifetime together with the element (without a global strong root).
    private static readonly DependencyProperty NativeScopeProperty = DependencyProperty.RegisterAttached(
        "NativeScope", typeof(object), typeof(Localize), new PropertyMetadata(null));

    static partial void RetainNativeScope(object owner, object scope)
    {
        if (owner is DependencyObject element) element.SetValue(NativeScopeProperty, scope);
    }

    static partial void ReleaseNativeScope(object owner)
    {
        if (owner is DependencyObject element) element.ClearValue(NativeScopeProperty);
    }
}
