using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Review;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed partial class AiJobCoordinatorTests
{
    // After a restart the review worker can look at a plan's render before the queue has been read. An empty view then
    // must not read as a lost request, or the step fails although the queue still holds it.
    [Fact]
    public async Task APlansRequestReadsAsWaitingUntilTheQueueHasBeenRead()
    {
        var saved = await Store.EnqueueAsync(Request(AiBackend.Codex), _ct);
        var coordinator = new AiJobCoordinator(Store, _settings, [new FakeHandler()], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        var queue = new ReviewRenderQueue(null!, null!, coordinator);

        Assert.False(coordinator.Loaded);
        Assert.Equal(AiJobState.Waiting, queue.State(saved.Id));

        await coordinator.RefreshAsync(_ct);

        Assert.True(coordinator.Loaded);
        Assert.Equal(AiJobState.Waiting, queue.State(saved.Id));
        // Once read, a request the queue does not list is gone.
        Assert.Null(queue.State(Guid.NewGuid()));
    }
}
