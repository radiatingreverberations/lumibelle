using lumibelle.Models;

namespace lumibelle.Services.AI;

public static class ComfyQueuePolicy
{
    public static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);
    // How long ComfyUI must keep answering without any record of a request before it
    // counts as lost. One empty reading can be a job moving from queue to history.
    public static readonly TimeSpan LostRemoteGrace = TimeSpan.FromMinutes(2);
    public const string LostRemoteMessage = "ComfyUI has no record of this request in its queue or history, so it cannot be recovered. " +
        "Nothing was rendered again and the queue has moved on. Use Retry when connected to run the captured workflow again.";

    // A dropped connection says nothing about the remote work, and every later ComfyUI
    // job waits on the answer. Armed retries and cancellations have their own schedules.
    public static bool ShouldRecheck(AiJobHeader job, int attempts, DateTimeOffset now) =>
        job.Backend == AiBackend.ComfyUI && job.ComfyControl is null && !job.CancelRequested &&
        job.State == AiJobState.NeedsAttention && job.RemoteUnconfirmed &&
        (job.FinishedUtc is null || now - job.FinishedUtc >= RecheckDelay(attempts));

    public static TimeSpan RecheckDelay(int attempts) =>
        TimeSpan.FromSeconds(attempts >= 4 ? 60 : 5 << attempts);

    public static bool CanRetry(AiJobHeader job) => job.Backend == AiBackend.ComfyUI && !job.CancelRequested &&
        job.State == AiJobState.NeedsAttention && (job.RemoteUnconfirmed || job.Recovery == AiJobRecovery.CheckStatus);

    public static bool NeedsPause(AiJobHeader job) => job.Backend == AiBackend.ComfyUI && !job.CancelRequested &&
        (job.State == AiJobState.Running || job.RemoteUnconfirmed);

    public static bool ShouldReconcile(AiJobHeader job, IReadOnlyList<AiBackend> paused, DateTimeOffset now) =>
        job.Backend == AiBackend.ComfyUI && job.ComfyControl is { } control && !job.CancelRequested &&
        job.State == AiJobState.NeedsAttention && job.Recovery == AiJobRecovery.CheckStatus &&
        (control.PauseRequested || !paused.Contains(AiBackend.ComfyUI)) &&
        (job.FinishedUtc is null || now - job.FinishedUtc >= ReconnectInterval);

    public static AiJobHeader Suspended(AiJobHeader job, bool remoteUnknown, bool hasSubmissions, DateTimeOffset now) => job with
    {
        State = remoteUnknown ? AiJobState.NeedsAttention : AiJobState.Waiting,
        Recovery = remoteUnknown || hasSubmissions ? AiJobRecovery.CheckStatus : AiJobRecovery.None,
        RemoteUnconfirmed = remoteUnknown, FinishedUtc = now,
        Unread = job.Unread || remoteUnknown, ActivityClearedUtc = null,
        Error = remoteUnknown
            ? "Pausing: waiting for ComfyUI to confirm the owned work has stopped. Reconnection is checked automatically; the request is retained."
            : null
    };

    internal static string AttemptOperation(string operation, Guid retryId) =>
        "comfy-retry/" + retryId.ToString("N") + "/" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(operation)));
}
