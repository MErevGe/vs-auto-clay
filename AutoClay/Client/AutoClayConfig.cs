namespace AutoClay.Client;

public sealed class AutoClayConfig
{
    public int ActionsPerBatch { get; set; } = 32;
    public int ServerTimeoutMilliseconds { get; set; } = 5000;

    public void Validate()
    {
        ActionsPerBatch = Math.Clamp(ActionsPerBatch, 1, 64);
        ServerTimeoutMilliseconds = Math.Clamp(ServerTimeoutMilliseconds, 1000, 30000);
    }
}
