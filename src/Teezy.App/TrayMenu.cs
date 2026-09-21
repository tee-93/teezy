using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Teezy.App;

/// <summary>Paints the tray's WinForms menu in the theme, so it matches the window.</summary>
/// <remarks>
/// The tray menu is the one piece of chrome WPF cannot draw — NotifyIcon is WinForms — and a
/// stock white menu under a dark app is the first thing that makes it look assembled rather
/// than designed. The colours are read from <c>Theme.xaml</c>, not restated: the same rule as
/// the tray icon, for the same reason.
/// </remarks>
internal static class TrayMenu
{
    public static void Apply(Forms.ContextMenuStrip menu)
    {
        var colours = new Palette();
        menu.Renderer = new Renderer(colours);
        menu.BackColor = colours.Menu;
        menu.ForeColor = colours.Text;
        menu.ShowImageMargin = false;
        menu.Padding = new Forms.Padding(4);
        menu.Font = new Drawing.Font("Segoe UI", 9.75f);
    }

    private sealed class Palette : Forms.ProfessionalColorTable
    {
        public Drawing.Color Menu { get; } = Read("Raised");
        public Drawing.Color Hover { get; } = Read("Selected");
        public Drawing.Color Line { get; } = Read("Hairline");
        public Drawing.Color Text { get; } = Read("Ink");

        public override Drawing.Color ToolStripDropDownBackground => Menu;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Menu;
        public override Drawing.Color ImageMarginGradientBegin => Menu;
        public override Drawing.Color ImageMarginGradientMiddle => Menu;
        public override Drawing.Color ImageMarginGradientEnd => Menu;

        private static Drawing.Color Read(string key)
        {
            var c = ((SolidColorBrush)Application.Current.FindResource(key)).Color;
            return Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }
    }

    private sealed class Renderer(Palette colours) : Forms.ToolStripProfessionalRenderer(colours)
    {
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = colours.Text;
            base.OnRenderItemText(e);
        }
    }
}
