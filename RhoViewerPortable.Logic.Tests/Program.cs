using RhoViewerPortable.Tests;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

SynchronizationContextRegressionGuard.Run(UiPerformanceHelperTests.Run, TimeSpan.FromSeconds(5));
Console.WriteLine("PASS M5 platform-independent UI performance helper tests under a nonpumping synchronization context");
LowFixPortableTests.Run();
Console.WriteLine("PASS L1-L3 platform-independent low-fix tests");

internal static class SynchronizationContextRegressionGuard
{
    public static void Run(Action test, TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SingleThreadNonPumpingSynchronizationContext());
            try
            {
                test();
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetResult(ex);
            }
        })
        {
            IsBackground = true,
            Name = "nonpumping synchronization context regression guard"
        };

        worker.Start();
        if (!completion.Task.Wait(timeout))
            throw new TimeoutException($"The async UI helper suite did not finish within {timeout} under a nonpumping synchronization context.");

        var failure = completion.Task.GetAwaiter().GetResult();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class SingleThreadNonPumpingSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();

        // Post enqueues synchronously, but this single-thread context has no pump to run callbacks.
        public override void Post(SendOrPostCallback d, object? state) => _pending.Enqueue((d, state));
    }
}
