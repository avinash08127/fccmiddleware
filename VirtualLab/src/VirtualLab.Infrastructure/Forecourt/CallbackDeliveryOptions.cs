namespace VirtualLab.Infrastructure.Forecourt;

public sealed class CallbackDeliveryOptions
{
    public const string SectionName = "VirtualLab:Callbacks";

    public int DispatchBatchSize { get; set; } = 25;
    public int WorkerPollIntervalMs { get; set; } = 1000;
    public int RequestTimeoutSeconds { get; set; } = 5;
    public int MaxRetryCount { get; set; } = 3;
    public int[] RetryDelaysSeconds { get; set; } = [2, 10, 30];
}
