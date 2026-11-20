using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ScrcpyX;

public sealed class FileCommandWrap : Panel
{
    private const double Gap = 6;

    protected override Size MeasureOverride(Size available)
    {
        var limit = double.IsInfinity(available.Width) ? double.PositiveInfinity : Math.Max(0, available.Width);
        double x = 0, y = 0, rowHeight = 0, usedWidth = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(limit, double.PositiveInfinity));
            var width = child.DesiredSize.Width;
            var height = child.DesiredSize.Height;
            if (x > 0 && x + width > limit)
            {
                y += rowHeight + Gap;
                x = 0;
                rowHeight = 0;
            }
            x += width + Gap;
            rowHeight = Math.Max(rowHeight, height);
            usedWidth = Math.Max(usedWidth, x - Gap);
        }
        var totalWidth = double.IsInfinity(available.Width) ? usedWidth : available.Width;
        return new Size(totalWidth, y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            var width = child.DesiredSize.Width;
            var height = child.DesiredSize.Height;
            if (x > 0 && x + width > finalSize.Width)
            {
                y += rowHeight + Gap;
                x = 0;
                rowHeight = 0;
            }
            child.Arrange(new Rect(x, y, width, height));
            x += width + Gap;
            rowHeight = Math.Max(rowHeight, height);
        }
        return finalSize;
    }
}
