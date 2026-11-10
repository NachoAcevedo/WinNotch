using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using WinNotch.Views;
using Xunit;

namespace WinNotch.Tests;

public class CompactHudTests
{
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Volume(HudOverlay hud) => typeof(HudOverlay)
        .GetMethod("ShowVolume", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(hud, new object[] { 0.65f, false });

    [Fact]
    public void VolumeHasReadableLayoutAndDismissesWhenDisabled() => OnSta(() =>
    {
        var hud = new HudOverlay();
        Volume(hud);
        hud.Measure(new Size(280, 32));
        hud.Arrange(new Rect(0, 0, 280, 32));
        hud.UpdateLayout();
        Assert.True(hud.IsShowing);
        Assert.Equal("65%", ((TextBlock)hud.FindName("HudPercent")).Text);
        var fill = (Border)hud.FindName("SliderFill");
        Assert.True(fill.Width > 0, $"Fill={fill.Width}; track={((Border)fill.Parent).ActualWidth}; HUD={hud.ActualWidth}");
        hud.VolumeHudEnabled = false;
        Assert.False(hud.IsShowing);
        Volume(hud);
        Assert.False(hud.IsShowing);
    });

    [Fact]
    public void InactiveDockCannotDisplayHud() => OnSta(() =>
    {
        var hud = new HudOverlay { IsActive = false };
        Volume(hud);
        Assert.False(hud.IsShowing);
        hud.IsActive = true;
        Volume(hud);
        Assert.True(hud.IsShowing);
        hud.IsActive = false;
        Assert.False(hud.IsShowing);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwelvePixelMinimumGeometryDoesNotOverflow(bool side) => OnSta(() =>
    {
        var rect = side ? new Rect(0, 0, 12, 96) : new Rect(0, 0, 96, 12);
        var shape = side ? NotchShape.CreateSideNotchGeometry(rect, 6, 14, true)
                         : NotchShape.CreateNotchGeometry(rect, 6, 14);
        Assert.True(shape.Bounds.Top >= -0.001);
        Assert.True(shape.Bounds.Left >= -0.001);
        Assert.True(shape.Bounds.Bottom <= rect.Bottom + 0.001);
        Assert.True(shape.Bounds.Right <= rect.Right + 0.001);
    });
}
