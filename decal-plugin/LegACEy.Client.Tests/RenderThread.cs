using System.Collections.Concurrent;

namespace LegACEy.Client.Tests;

/// <summary>One long-lived thread that owns Avalonia, as the game's render thread does in the plugin.</summary>
internal static class RenderThread
{
    private static readonly BlockingCollection<Action> Work = new();

    static RenderThread()
    {
        new Thread(() =>
        {
            foreach (var action in Work.GetConsumingEnumerable())
                action();
        })
        { IsBackground = true, Name = "Panel render thread" }.Start();
    }

    public static void Run(Action action) => Run<object?>(() =>
    {
        action();
        return null;
    });

    public static T Run<T>(Func<T> func)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Work.Add(() =>
        {
            try
            {
                completion.SetResult(func());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task.GetAwaiter().GetResult();
    }
}
