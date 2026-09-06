using System;
namespace XrayUI.Helpers;

/// <summary>A message retains its key/arguments until rendering; external text stays verbatim.</summary>
public sealed class LocalizedText
{
    private readonly Func<string> _render;
    private LocalizedText(Func<string> render) => _render = render;
    public string Value => _render();
    public static LocalizedText Key(string key) => new(() => Loc.GetString(key));
    public static LocalizedText Format(string key, params object?[] arguments)
    {
        var copy = (object?[])arguments.Clone();
        return new(() => Loc.Format(key, copy));
    }
    public static implicit operator LocalizedText(string value) => new(() => value);
    public override string ToString() => Value;
}
