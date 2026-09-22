namespace ZutWifi.Core;

/// 保守策略：一次连接事件最多提交 1 + maxRetries 次，间隔 2s/5s/15s，之后交给人工。
public sealed class RetryPolicy(int maxRetries)
{
    private static readonly TimeSpan[] Table =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120),
    ];

    public int MaxAttempts => maxRetries + 1;

    public TimeSpan? NextDelay(int attemptsSoFar)
        => attemptsSoFar >= maxRetries ? null : Table[Math.Min(attemptsSoFar, Table.Length - 1)];
}
