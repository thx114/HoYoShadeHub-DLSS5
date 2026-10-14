using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class LatestRequestGateTests
{
    public static async Task<int> RunAsync()
    {
        var requests = new LatestRequestGate();
        var oldResult = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newResult = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int displayed = 0;
        async Task Submit(long ticket, Task<int> result)
        {
            int value = await result;
            if (requests.IsCurrent(ticket)) displayed = value;
        }

        Task oldRequest = Submit(requests.Begin(), oldResult.Task);
        Task newRequest = Submit(requests.Begin(), newResult.Task);
        newResult.SetResult(2);
        await newRequest;
        oldResult.SetResult(1);
        await oldRequest;
        if (displayed != 2) throw new Exception("A stale completion overwrote the latest result.");

        var unloadedResult = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task unloadedRequest = Submit(requests.Begin(), unloadedResult.Task);
        requests.Invalidate();
        unloadedResult.SetResult(3);
        await unloadedRequest;
        if (displayed != 2) throw new Exception("Invalidation allowed an unloaded owner to submit.");
        long reloaded = requests.Begin();
        if (!requests.IsCurrent(reloaded)) throw new Exception("A new owner could not submit.");
        return 3;
    }
}
