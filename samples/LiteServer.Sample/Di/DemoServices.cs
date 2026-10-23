namespace LiteServer.Sample.Di;

/// <summary>单例：全进程只有一个实例，用于验证 Singleton 生命周期。</summary>
public sealed class SingletonCounter
{
    public string InstanceId { get; } = Guid.NewGuid().ToString("N")[..8];

    private int _hits;

    public int Increment() => Interlocked.Increment(ref _hits);

    public int Hits => Volatile.Read(ref _hits);
}

/// <summary>作用域：每个请求一个实例，用于验证 Scoped 生命周期。</summary>
public sealed class ScopedStamp
{
    public string InstanceId { get; } = Guid.NewGuid().ToString("N")[..8];

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>瞬时：每次解析都是新实例，用于验证 Transient 生命周期。</summary>
public sealed class TransientStamp
{
    public string InstanceId { get; } = Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// 依赖注入的消费者：验证「构造函数注入」在 AOT 下可用。
/// 它同时依赖三种生命周期，用来证明容器能把它们正确组合起来。
/// </summary>
public sealed class DiShowcase(SingletonCounter singleton, ScopedStamp scoped, TransientStamp transient)
{
    public SingletonCounter Singleton { get; } = singleton;

    public ScopedStamp Scoped { get; } = scoped;

    public TransientStamp Transient { get; } = transient;

    /// <summary>本请求内再次解析 Transient，应得到与构造时不同的实例。</summary>
    public string Describe(TransientStamp another)
        => $"singleton={Singleton.InstanceId};scoped={Scoped.InstanceId};transientA={Transient.InstanceId};transientB={another.InstanceId}";
}
