using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Wisper.Core;

namespace Wisper.App;

/// <summary>The floating status pill shown while dictating.</summary>
/// <remarks>
/// <para>
/// <b>This window must never take focus.</b> It is the load-bearing detail of the whole app:
/// the text is injected into whatever had keyboard focus, so if the HUD ever became the
/// active window the user's text field would lose focus and there would be nothing left to
/// type into.
/// </para>
/// <para>
/// Three independent mechanisms enforce that, because any one of them alone has a gap:
/// <c>ShowActivated="False"</c> covers the initial show but not later ones;
/// <c>WS_EX_NOACTIVATE</c> tells Windows never to activate on click; and
/// <c>IsHitTestVisible="False"</c> means clicks pass through to the app underneath.
/// <c>WS_EX_TOOLWINDOW</c> is what keeps it out of Alt-Tab.
/// </para>
/// </remarks>
public partial class HudWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const int BarCount = 13;
    private const double BarMinHeight = 3;
    private const double BarMaxHeight = 24;

    private readonly Rectangle[] _bars = new Rectangle[BarCount];

    /// <summary>Per-bar weights, so the meter reads as a waveform rather than a block.
    /// Fixed rather than random: a shape that jitters frame to frame reads as noise.</summary>
    private static readonly double[] BarWeights =
        [0.35, 0.5, 0.68, 0.82, 0.93, 1.0, 1.0, 1.0, 0.93, 0.82, 0.68, 0.5, 0.35];

    private double _smoothedLevel;

    public HudWindow()
    {
        InitializeComponent();
        BuildMeter();
        PositionAtBottomCenter();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var style = Native.GetWindowLongPtrW(handle, GWL_EXSTYLE);
        Native.SetWindowLongPtrW(handle, GWL_EXSTYLE,
            style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void BuildMeter()
    {
        for (var i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = BarMinHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xED)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1.5, 0, 1.5, 0),
            };
            _bars[i] = bar;
            Meter.Children.Add(bar);
        }
    }

    private void PositionAtBottomCenter()
    {
        var area = SystemParameters.WorkArea;
        // Measure first: SizeToContent means ActualWidth is still 0 before layout runs.
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Left = area.Left + (area.Width - DesiredSize.Width) / 2;
        Top = area.Bottom - DesiredSize.Height - 72;
    }

    public void ShowState(DictationState state, string? message = null)
    {
        switch (state)
        {
            case DictationState.Starting:
            case DictationState.Listening:
                StatusText.Text = "Listening";
                RecordDot.Visibility = Visibility.Visible;
                StartPulse();
                ShowPill();
                break;

            case DictationState.Finishing:
                StatusText.Text = "Transcribing";
                RecordDot.Visibility = Visibility.Collapsed;
                StopPulse();
                SetLevel(0);
                break;

            case DictationState.Error:
                StatusText.Text = message ?? "Something went wrong";
                RecordDot.Visibility = Visibility.Collapsed;
                StopPulse();
                ShowPill();
                break;

            case DictationState.Idle:
            default:
                StopPulse();
                Hide();
                break;
        }
    }

    private void ShowPill()
    {
        if (!IsVisible)
        {
            PositionAtBottomCenter();
            Show();
        }
    }

    public void SetLevel(float level)
    {
        // Light smoothing so the meter glides instead of strobing at buffer rate.
        _smoothedLevel += (level - _smoothedLevel) * 0.4;

        for (var i = 0; i < BarCount; i++)
        {
            var target = BarMinHeight + (BarMaxHeight - BarMinHeight) * _smoothedLevel * BarWeights[i];
            _bars[i].Height = Math.Max(BarMinHeight, target);
        }
    }

    private void StartPulse()
    {
        var pulse = new DoubleAnimation(1.0, 0.35, TimeSpan.FromSeconds(0.7))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        RecordDot.BeginAnimation(OpacityProperty, pulse);
    }

    private void StopPulse() => RecordDot.BeginAnimation(OpacityProperty, null);
}
