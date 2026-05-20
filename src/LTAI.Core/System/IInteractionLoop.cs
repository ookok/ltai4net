namespace LTAI.Core.System;

public interface IInteractionLoop
{
    Task<InteractionTrajectory> RunAsync(
        string taskDescription,
        RolloutConfig? config = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<InteractionTrajectory> RunBatchAsync(
        IReadOnlyList<string> tasks,
        RolloutConfig? config = null,
        CancellationToken cancellationToken = default);

    InteractionTrajectory? RestoreFromCheckpoint(
        string trajectoryId);

    bool SaveCheckpoint(
        InteractionTrajectory trajectory);
}
