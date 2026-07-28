using VDict.Core.Policies;

namespace VDict.Core.Tests.Policies;

public sealed class LatestWinsWorkQueueTests
{
    [Fact]
    public async Task Queue_RunsOneAndKeepsOnlyLatestPendingItem()
    {
        TaskCompletionSource releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> started = [];
        await using LatestWinsWorkQueue<int> queue = new(async (value, _) =>
        {
            lock (started)
            {
                started.Add(value);
            }

            if (value == 1)
            {
                await releaseFirst.Task;
            }
        });

        Task first = queue.EnqueueAsync(1);
        await WaitUntilAsync(() => queue.ActiveCount == 1);
        Task replaced = queue.EnqueueAsync(2);
        Task latest = queue.EnqueueAsync(3);
        releaseFirst.TrySetResult();

        await first;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replaced);
        await latest;

        Assert.Equal([1, 3], started);
        Assert.Equal(1, queue.PeakActiveCount);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Queue_CancelsActiveItemWhenNewerWorkArrives()
    {
        TaskCompletionSource firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using LatestWinsWorkQueue<int> queue = new(async (value, token) =>
        {
            if (value == 1)
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        });

        Task first = queue.EnqueueAsync(1);
        await firstStarted.Task;
        Task second = queue.EnqueueAsync(2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await second;
        Assert.Equal(1, queue.PeakActiveCount);
        double observation = Assert.Single(
            queue.CancellationObservationMilliseconds);
        Assert.InRange(observation, 0, 1000);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
