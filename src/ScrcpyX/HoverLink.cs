using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ScrcpyX;

public sealed class HoverLink : Button
{
    public HoverLink()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        PointerEntered += (_, _) => Apply(true);
        PointerExited += (_, _) => Apply(false);
        PointerCanceled += (_, _) => Apply(false);
        PointerCaptureLost += (_, _) => Apply(false);
    }

    private void Apply(bool hot)
    {
        var key = hot ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush";
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            Foreground = brush;
    }
}
