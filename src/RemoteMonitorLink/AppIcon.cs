using System;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteMonitorLink
{
    // Shows the executable's embedded application icon (app.ico, ApplicationIcon in each csproj) on a form's title
    // bar and taskbar button. Icon extraction can fail on an unusual launch path; the form then keeps the default
    // WinForms icon, which is never an operational error.
    internal static class AppIcon
    {
        internal static void Apply(Form form)
        {
            if (form == null) return;
            try
            {
                var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null) form.Icon = icon;
            }
            catch (Exception)
            {
                // Keep the default icon.
            }
        }
    }
}
