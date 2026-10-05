using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed class PrototypeButton : Button
{
    public PrototypeButton()
    {
        IsEnabledChanged += (_, _) => UpdateCursor();
        PointerEntered += (_, _) => UpdateCursor();
        PointerExited += (_, _) => ProtectedCursor = null;
    }

    private void UpdateCursor() => ProtectedCursor = IsEnabled
        ? InputSystemCursor.Create(InputSystemCursorShape.Hand)
        : null;
}

public sealed class PrototypeToggleButton : ToggleButton
{
    public PrototypeToggleButton()
    {
        IsEnabledChanged += (_, _) => UpdateCursor();
        PointerEntered += (_, _) => UpdateCursor();
        PointerExited += (_, _) => ProtectedCursor = null;
    }

    private void UpdateCursor() => ProtectedCursor = IsEnabled
        ? InputSystemCursor.Create(InputSystemCursorShape.Hand)
        : null;
}

public sealed class PrototypeCheckBox : CheckBox
{
    public PrototypeCheckBox()
    {
        IsEnabledChanged += (_, _) => UpdateCursor();
        PointerEntered += (_, _) => UpdateCursor();
        PointerExited += (_, _) => ProtectedCursor = null;
    }

    private void UpdateCursor() => ProtectedCursor = IsEnabled
        ? InputSystemCursor.Create(InputSystemCursorShape.Hand)
        : null;
}
