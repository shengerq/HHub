using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private Action? _cancelPageFirstFrame;
    private long _pageRenderStarted;

    private void AfterPageFirstFrame(FrameworkElement shell, Action continuePage)
    {
        long started = _pageRenderStarted;
        string route = _openPlugin?.Manifest.Id ?? _page;
        void Cancel()
        {
            CompositionTarget.Rendered -= OnRendered;
            _cancelPageFirstFrame = null;
        }

        void OnRendered(object? sender, object args)
        {
            if (!shell.IsLoaded) return;
            Cancel();
            Trace.WriteLine($"Hhub first frame [{route}]: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (CanCompletePage(shell)) continuePage();
            });
        }

        _cancelPageFirstFrame = Cancel;
        CompositionTarget.Rendered += OnRendered;
    }

    private bool CanCompletePage(FrameworkElement shell)
    {
        if (_closing || !_content.Children.Contains(shell)) return false;
        if (!_hiddenToTray) return true;
        _hiddenPageDirty = true;
        return false;
    }
}
