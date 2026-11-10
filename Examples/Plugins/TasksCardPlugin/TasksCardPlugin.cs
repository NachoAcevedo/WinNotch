using System.Windows;
using WinNotch.Plugins;
using WinNotch.Views;
namespace PersonalCards;
[WinNotchPlugin("personal.card.tasks", "Tareas", "1.0.0", "Nacho_114")]
public sealed class TasksCardPlugin : CardPluginBase
{
 public override string Id => "personal.card.tasks";
 public override string Name => "Tareas";
 public override string Description => "Tarjeta independiente: Tareas";
 public override string CardKey => "tasks";
 public override string CardTitle => "Tareas";
 public override int CardOrder => 40;
 public override double CardHeight => 270;
 protected override UIElement CreateCard() => new TasksCardView();
 
}