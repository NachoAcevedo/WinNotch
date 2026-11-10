using System.Windows;
using WinNotch.Plugins;
using WinNotch.Views;
namespace PersonalCards;
[WinNotchPlugin("personal.card.weather", "Clima", "1.0.0", "Personal")]
public sealed class WeatherCardPlugin : CardPluginBase
{
 public override string Id => "personal.card.weather";
 public override string Name => "Clima";
 public override string Description => "Tarjeta independiente: Clima";
 public override string CardKey => "weather";
 public override string CardTitle => "Clima";
 public override int CardOrder => 30;
 public override double CardHeight => 160;
 protected override UIElement CreateCard() => new WeatherCardView();
 
}