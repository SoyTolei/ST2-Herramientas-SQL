namespace SBBackup.Ui;



/// <summary>

/// No cambia la selección con la rueda salvo que el desplegable esté abierto.

/// </summary>

internal sealed class WheelSafeComboBox : ComboBox

{

    public Action<MouseEventArgs>? RedirectWheel;



    protected override void WndProc(ref Message m)

    {

        const int WM_MOUSEWHEEL = 0x020A;

        if (m.Msg == WM_MOUSEWHEEL && !DroppedDown)

        {

            var delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);

            RedirectWheel?.Invoke(new MouseEventArgs(MouseButtons.None, 0, 0, 0, delta));

            return;

        }



        base.WndProc(ref m);

    }

}


