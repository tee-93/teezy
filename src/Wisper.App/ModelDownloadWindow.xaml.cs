using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Wisper.Speech;

namespace Wisper.App;

/// <summary>First-run download of the speech model.</summary>
/// <remarks>
/// Shown only when no usable model is found. The app cannot transcribe a word without it, so
/// this is a blocking setup step rather than a background task — but it is cancellable, and
/// cancelling leaves the app running so the download can be retried from the tray menu.
/// </remarks>
public partial class ModelDownloadWindow : Window
{
    private readonly string _directory;
    private CancellationTokenSource? _cts;

    /// <summary>True once every file is present and the right size.</summary>
    public bool Succeeded { get; private set; }

    public ModelDownloadWindow(string directory)
    {
        InitializeComponent();
        _directory = directory;
        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        ErrorBox.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        CancelButton.Content = "Cancel";

        // Progress arrives from a background thread; Progress<T> captures this window's
        // synchronization context at construction, so the callback lands back on the UI thread.
        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            Bar.Value = p.OverallFraction;
            PercentText.Text = $"{p.OverallFraction * 100:F0}%";
            StatusText.Text = $"{p.Describe()}   ({p.FileIndex} of {p.FileCount})";
        });

        try
        {
            await new ModelDownloader().DownloadAsync(_directory, progress, _cts.Token);
            Succeeded = true;
            StatusText.Text = "Done.";
            Bar.Value = 1;
            PercentText.Text = "100%";
            await Task.Delay(400);
            Close();
        }
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or Exception)
        {
            Fail(e is HttpRequestException
                ? $"Could not reach the download server.\n\n{e.Message}"
                : e.Message);
        }
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorBox.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Visible;
        CancelButton.Content = "Close";
        StatusText.Text = "Stopped.";

        // Files that already arrived intact are kept, so a retry resumes at file granularity
        // rather than re-fetching 650 MB that already landed.
    }

    private async void OnRetry(object sender, RoutedEventArgs e) => await RunAsync();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnClosed(e);
    }
}
