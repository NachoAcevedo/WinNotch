using System.Windows;
using System.Windows.Controls;

namespace PersonalCards.BatteryManager;

internal sealed class CenteredDevicePanel : WrapPanel
{
    protected override Size ArrangeOverride(Size size)
    {
        double top = 0;
        int first = 0;

        while (first < InternalChildren.Count)
        {
            int end = first;
            double width = 0;
            double height = 0;
            while (end < InternalChildren.Count)
            {
                Size tile = InternalChildren[end].DesiredSize;
                if (end > first && width + tile.Width > size.Width) break;
                width += tile.Width;
                height = Math.Max(height, tile.Height);
                end++;
            }

            double left = Math.Max(0, (size.Width - width) / 2);
            for (int i = first; i < end; i++)
            {
                var tile = InternalChildren[i];
                tile.Arrange(new Rect(left, top, tile.DesiredSize.Width, height));
                left += tile.DesiredSize.Width;
            }

            top += height;
            first = end;
        }

        return size;
    }
}
