using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Temporary diagnostic writer; bounded queue and background I/O keep it off the game callback.</summary>
internal sealed class ScrollProfileLog : IDisposable
{
    private readonly BlockingCollection<string> _pending = new(16);
    private readonly Thread _writer;
    private int _dropped;
    private bool _disposed;

    public ScrollProfileLog(string path, Action<Exception>? onError = null)
    {
        _writer = new Thread(() => Write(path, onError)) { IsBackground = true, Name = "LegACEy scroll profile log" };
        _writer.Start();
    }

    public void Enqueue(string text)
    {
        if (_disposed) return;
        var lost = Interlocked.Exchange(ref _dropped, 0);
        if (!_pending.TryAdd((lost == 0 ? string.Empty : ScrollProfile.Prefix + " droppedSnapshots=" + lost + Environment.NewLine) + text))
            Interlocked.Add(ref _dropped, lost + 1);
    }

    private void Write(string path, Action<Exception>? onError)
    {
        try
        {
            using var file = new StreamWriter(path, false);
            foreach (var text in _pending.GetConsumingEnumerable())
            {
                file.Write(text);
                file.Flush();
            }
        }
        catch (Exception error)
        {
            try { onError?.Invoke(error); }
            catch { /* Failure reporting also stays off the game callback. */ }
            // Diagnostic I/O failure must not disable the UI or block its callbacks.
            // Continue draining the bounded queue until shutdown.
            foreach (var _ in _pending.GetConsumingEnumerable()) { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending.CompleteAdding();
        if (_writer.Join(1000)) _pending.Dispose();
    }
}
