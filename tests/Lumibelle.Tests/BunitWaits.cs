using Bunit;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

// bUnit's WaitFor* re-run their check only after the component renders; their timer only enforces the timeout. A wait on state the page
// does not render, such as a fake's call count, the AI queue or a store, can therefore time out when no render follows the change.
// Await these instead: they re-check whenever that state itself changes.

/// <summary>Counts a fake's calls so a test can await them.</summary>
internal sealed class CallCount
{
    private readonly Lock _gate = new();
    private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];
    private int _value;
    public int Value { get { lock (_gate) return _value; } }
    public void Increment()
    {
        TaskCompletionSource[] reached;
        lock (_gate)
        {
            _value++;
            reached = [.. _waiters.Where(w => w.Count <= _value).Select(w => w.Reached)];
            _waiters.RemoveAll(w => w.Count <= _value);
        }
        foreach (var waiter in reached) waiter.TrySetResult();
    }
    /// <summary>Completes once at least <paramref name="count"/> calls were made.</summary>
    public async Task Reached(int count)
    {
        TaskCompletionSource waiter;
        lock (_gate)
        {
            if (_value >= count) return;
            waiter = new(TaskCreationOptions.RunContinuationsAsynchronously); _waiters.Add((count, waiter));
        }
        try { await waiter.Task.WaitAsync(BunitContext.DefaultWaitTimeout, Xunit.TestContext.Current.CancellationToken); }
        catch (TimeoutException) { throw new TimeoutException($"Expected {count} call(s) but saw {Value}."); }
    }
}

internal static class StateWaits
{
    /// <summary>Completes once <paramref name="assertion"/> passes, checking it now and whenever the state raises <paramref name="subscribe"/>'s event.</summary>
    public static async Task Until(Action<Action> subscribe, Action<Action> unsubscribe, Action assertion)
    {
        var passed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        void Check() { try { assertion(); passed.TrySetResult(); } catch (Exception e) { failure = e; } }
        subscribe(Check);
        try
        {
            Check();
            await passed.Task.WaitAsync(BunitContext.DefaultWaitTimeout, Xunit.TestContext.Current.CancellationToken);
        }
        catch (TimeoutException) { throw new TimeoutException("The expected state was never reached.", failure); }
        finally { unsubscribe(Check); }
    }

    /// <summary>Completes once <paramref name="assertion"/> about the queue passes.</summary>
    public static Task Until(this AiJobCoordinator queue, Action assertion) => Until(h => queue.Changed += h, h => queue.Changed -= h, assertion);
}
