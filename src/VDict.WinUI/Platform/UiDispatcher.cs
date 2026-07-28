using Microsoft.UI.Dispatching;
using VDict.Presentation.Services;

namespace VDict.WinUI.Platform;

public sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;

    public Task EnqueueAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (queue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("UI 调度队列已关闭。"));
        }

        return completion.Task;
    }
}
