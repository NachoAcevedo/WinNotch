using System.Windows;
using WinNotch.Plugins;
using WinNotch.Views;
namespace PersonalCards;
[WinNotchPlugin("personal.card.files", "Archivos", "1.0.0", "Nacho_114")]
public sealed class FilesCardPlugin : CardPluginBase
{
 public override string Id => "personal.card.files";
 public override string Name => "Archivos";
 public override string Description => "Tarjeta independiente: Archivos";
 public override string CardKey => "files";
 public override string CardTitle => "Archivos";
 public override int CardOrder => 60;
 public override double CardHeight => 190;
 protected override UIElement CreateCard() => CreateShelf();
 private UIElement CreateShelf() { var shelf = new ShelfView { Height = 140 }; shelf.Bind(Context!.ShelfService); return shelf; }
}
