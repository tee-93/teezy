using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Teezy.App;

/// <summary>A dashed box that says "drop an email here", always in view.</summary>
/// <remarks>
/// A drop that works anywhere on a page is invisible until tried, so the page shows where it
/// goes before any drag starts. While an email is over the page the box lights up in the accent
/// and says what the drop will do; while a dropped email is being read, it says so.
/// </remarks>
public sealed class DropZone : Grid
{
    private readonly Rectangle _edge;
    private readonly TextBlock _text;
    private readonly System.Windows.Threading.DispatcherTimer _settle = new() { Interval = System.TimeSpan.FromMilliseconds(400) };
    private System.Action? _afterSettle;

    /// <summary>What it says with no drag under way.</summary>
    public string Resting { get; set; } = "Drag an email from Outlook here to make a task";

    public DropZone()
    {
        MinHeight = 40;
        SnapsToDevicePixels = true;

        _edge = new Rectangle
        {
            RadiusX = 6,
            RadiusY = 6,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
        };
        _text = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(12, 8, 12, 8),
        };

        Children.Add(_edge);
        Children.Add(_text);
        Loaded += (_, _) => Rest();
        // Drag-over only arrives when the pointer moves, so a pause is not an ending; the
        // button coming up without a drop is.
        _settle.Tick += (_, _) =>
        {
            if ((GetAsyncKeyState(LeftButton) & 0x8000) != 0) return;
            _settle.Stop();
            Rest();
            _afterSettle?.Invoke();
        };
    }

    /// <summary>An email is over the page: lit up, saying what the drop will do.</summary>
    public void Ready(string what)
    {
        _settle.Stop();
       
        _edge.Stroke = Brand.Accent;
        _edge.Fill = Brand.Brush("AccentSoft");
        _text.Foreground = Brand.Brush("AccentInk");
        _text.Text = $"✉  {what}";
    }

    /// <summary>A dropped email is being read — New Outlook takes a moment to hand it over.</summary>
    public void Reading()
    {
        Ready("Reading the email…");
    }

    /// <summary>
    /// A leave inside the target: usually only a move between two of its parts, but also what a
    /// drag cancelled with Esc looks like. Stays lit while the mouse button is held, and rests
    /// once it is let go (a drop rests it anyway).
    /// </summary>
    public void MaybeLeft(System.Action? then = null)
    {
        _afterSettle = then;
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>Whether a drag-leave is only a move between two parts of the same target.</summary>
    public static bool StillOver(FrameworkElement target, DragEventArgs e)
    {
        var at = e.GetPosition(target);
        return at.X > 0 && at.Y > 0 && at.X < target.ActualWidth && at.Y < target.ActualHeight;
    }

    private const int LeftButton = 0x01;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    /// <summary>No drag: the quiet dashed outline.</summary>
    public void Rest()
    {
       
        _settle.Stop();
        _edge.Stroke = Brand.Brush("Faint");
        _edge.Fill = Brushes.Transparent;
        _text.Foreground = Brand.Muted;
        _text.Text = $"✉  {Resting}";
    }
}
