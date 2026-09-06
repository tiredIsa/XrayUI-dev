using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace XrayUI.Helpers;

/// <summary>Typed, AOT-safe localization for static XAML captions. Never binds user input.</summary>
public sealed partial class Localize : DependencyObject
{
    public static readonly DependencyProperty ContentProperty = DependencyProperty.RegisterAttached(
        "Content", typeof(string), typeof(Localize), new PropertyMetadata(null, OnContentChanged));
    public static string? GetContent(DependencyObject target) => (string?)target.GetValue(ContentProperty);
    public static void SetContent(DependencyObject target, string? value) => target.SetValue(ContentProperty, value);
    private static void OnContentChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        LocalizationBindings.Bind(target, "Content", () =>
        {
            var key = GetContent(target);
            if (string.IsNullOrEmpty(key)) return;
            var value = Loc.GetString(key);
            if (target is ContentControl content) content.Content = value; else throw new InvalidOperationException("Unsupported localized Content target.");
        });
    }
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.RegisterAttached(
        "Header", typeof(string), typeof(Localize), new PropertyMetadata(null, OnHeaderChanged));
    public static string? GetHeader(DependencyObject target) => (string?)target.GetValue(HeaderProperty);
    public static void SetHeader(DependencyObject target, string? value) => target.SetValue(HeaderProperty, value);
    private static void OnHeaderChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        LocalizationBindings.Bind(target, "Header", () =>
        {
            var key = GetHeader(target);
            if (string.IsNullOrEmpty(key)) return;
            var value = Loc.GetString(key);
            if (target is TextBox text) text.Header = value; else if (target is ComboBox combo) combo.Header = value; else throw new InvalidOperationException("Unsupported localized Header target.");
        });
    }
    public static readonly DependencyProperty MessageProperty = DependencyProperty.RegisterAttached(
        "Message", typeof(string), typeof(Localize), new PropertyMetadata(null, OnMessageChanged));
    public static string? GetMessage(DependencyObject target) => (string?)target.GetValue(MessageProperty);
    public static void SetMessage(DependencyObject target, string? value) => target.SetValue(MessageProperty, value);
    private static void OnMessageChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        LocalizationBindings.Bind(target, "Message", () =>
        {
            var key = GetMessage(target);
            if (string.IsNullOrEmpty(key)) return;
            var value = Loc.GetString(key);
            if (target is InfoBar info) info.Message = value; else throw new InvalidOperationException("Unsupported localized Message target.");
        });
    }
    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.RegisterAttached(
        "PlaceholderText", typeof(string), typeof(Localize), new PropertyMetadata(null, OnPlaceholderTextChanged));
    public static string? GetPlaceholderText(DependencyObject target) => (string?)target.GetValue(PlaceholderTextProperty);
    public static void SetPlaceholderText(DependencyObject target, string? value) => target.SetValue(PlaceholderTextProperty, value);
    private static void OnPlaceholderTextChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        LocalizationBindings.Bind(target, "PlaceholderText", () =>
        {
            var key = GetPlaceholderText(target);
            if (string.IsNullOrEmpty(key)) return;
            var value = Loc.GetString(key);
            if (target is TextBox text) text.PlaceholderText = value; else if (target is ComboBox combo) combo.PlaceholderText = value; else if (target is AutoSuggestBox search) search.PlaceholderText = value; else throw new InvalidOperationException("Unsupported localized placeholder target.");
        });
    }
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Localize), new PropertyMetadata(null, OnTextChanged));
    public static string? GetText(DependencyObject target) => (string?)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string? value) => target.SetValue(TextProperty, value);
    private static void OnTextChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        LocalizationBindings.Bind(target, "Text", () =>
        {
            var key = GetText(target);
            if (string.IsNullOrEmpty(key)) return;
            var value = Loc.GetString(key);
            if (target is TextBlock text) text.Text = value; else if (target is MenuFlyoutItem item) item.Text = value; else if (target is MenuFlyoutSubItem menu) menu.Text = value; else throw new InvalidOperationException("Unsupported localized Text target.");
        });
    }
    public static readonly DependencyProperty TitleProperty = DependencyProperty.RegisterAttached(
        "Title", typeof(string), typeof(Localize), new PropertyMetadata(null, OnTitleChanged));
    public static string? GetTitle(DependencyObject target) => (string?)target.GetValue(TitleProperty);
    public static void SetTitle(DependencyObject target, string? value) => target.SetValue(TitleProperty, value);
    private static void OnTitleChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        LocalizationBindings.Bind(target, "Title", () =>
        {
            var key = GetTitle(target);
            if (string.IsNullOrEmpty(key)) return;
            var value = Loc.GetString(key);
            if (target is InfoBar info) info.Title = value; else if (target is ContentDialog dialog) dialog.Title = value; else throw new InvalidOperationException("Unsupported localized Title target.");
        });
    }
}
