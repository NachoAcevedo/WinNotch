using System.Windows;
using WinNotch.Models;

namespace WinNotch.Plugins;

public interface ICardPlugin : IUIPlugin
{
    string CardKey { get; }
    string CardTitle { get; }
    int CardOrder { get; }
    double CardHeight { get; }
}

public abstract class CardPluginBase : PluginBase, ICardPlugin
{
    private UIElement? _view;
    public abstract string CardKey { get; }
    public abstract string CardTitle { get; }
    public abstract int CardOrder { get; }
    public virtual double CardHeight => 160;
    public override string Version => "1.0.0";
    public override string Author => "Personal";
    protected abstract UIElement CreateCard();
    public UIElement? GetUIElement(UIPluginLocation location) => location == UIPluginLocation.CustomTab
        ? _view ??= CreateCard() : null;
    public virtual void OnNotchStateChanged(NotchState state) { }
}
