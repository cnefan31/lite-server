using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiteServer.Sample.Jobs;

/// <summary>
/// 后台任务调度器。
/// <para>
/// 这是「依赖注入 + 后台服务 + NativeAOT」的综合验证点：
/// ① 由 DI 以 Singleton 注入；
/// ② 继承 <see cref="BackgroundService"/>，随通用主机一同启动与停止；
/// ③ 不用任何反射，AOT 下可正常工作。
/// </para>
/// </summary>
public sealed class JobService : BackgroundService
{
    private const int MaxJobs = 100;
    private const int MaxSteps = 50;
    private const int MaxStepDelayMs = 1000;

    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ILogger<JobService> _logger;
    private int _seq;

    public JobService(ILogger<JobService> logger) => _logger = logger;

    /// <summary>创建一个后台任务并立即开始执行。返回任务记录。</summary>
    public JobRecord Create(string? name, int? steps, int? stepDelayMs, string? scopeId)
    {
        int stepCount = Math.Clamp(steps ?? 10, 1, MaxSteps);
        int delay = Math.Clamp(stepDelayMs ?? 100, 0, MaxStepDelayMs);

        // 容量保护：超过上限时先淘汰最早已结束的任务。
        if (_jobs.Count >= MaxJobs) EvictOldestFinished();

        string id = $"job-{Interlocked.Increment(ref _seq)}";
        var record = new JobRecord
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? $"任务 {id}" : name.Trim(),
            State = JobState.Queued,
            Progress = 0,
            Message = "已入队",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedInScope = scopeId,
        };

        _jobs[id] = record;
        _order.Enqueue(id);

        var cts = new CancellationTokenSource();
        _running[id] = cts;

        // 后台执行，不阻塞当前请求。
        _ = Task.Run(() => RunJobAsync(record, stepCount, delay, cts.Token), CancellationToken.None);
        return record;
    }

    private async Task RunJobAsync(JobRecord record, int steps, int delayMs, CancellationToken ct)
    {
        record.State = JobState.Running;
        record.StartedAt = DateTimeOffset.UtcNow;

        try
        {
            for (int i = 1; i <= steps; i++)
            {
                ct.ThrowIfCancellationRequested();

                if (delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);

                record.Progress = (int)Math.Round(i * 100.0 / steps);
                record.Message = $"完成第 {i}/{steps} 步";
            }

            record.State = JobState.Completed;
            record.Message = "已完成";
        }
        catch (OperationCanceledException)
        {
            record.State = JobState.Canceled;
            record.Message = "已取消";
            _logger.LogInformation("任务 {Id} 被取消", record.Id);
        }
        catch (Exception ex)
        {
            record.State = JobState.Failed;
            record.Error = ex.Message;
            record.Message = "执行失败";
            _logger.LogError(ex, "任务 {Id} 失败", record.Id);
        }
        finally
        {
            record.FinishedAt = DateTimeOffset.UtcNow;
            if (record.StartedAt is { } started)
            {
                record.DurationMs = (long)(record.FinishedAt.Value - started).TotalMilliseconds;
            }
            _running.TryRemove(record.Id, out CancellationTokenSource? cts2);
            cts2?.Dispose();
        }
    }

    /// <summary>取单个任务。不存在返回 null。</summary>
    public JobRecord? Get(string id) => _jobs.TryGetValue(id, out JobRecord? r) ? r : null;

    /// <summary>列出全部任务，最新在前。</summary>
    public JobRecord[] List(int limit = 20)
    {
        if (limit <= 0) limit = 20;
        return _order.Reverse().Take(limit).Select(id => _jobs.TryGetValue(id, out JobRecord? r) ? r : null)
            .Where(r => r is not null).Select(r => r!).ToArray();
    }

    /// <summary>请求取消。</summary>
    public bool Cancel(string id)
    {
        if (!_running.TryGetValue(id, out CancellationTokenSource? cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary>统计各状态数量，供健康检查用。</summary>
    public (int total, int running, int completed, int failed) Stats()
    {
        int running = 0, completed = 0, failed = 0;
        foreach (JobRecord r in _jobs.Values)
        {
            switch (r.State)
            {
                case JobState.Running or JobState.Queued: running++; break;
                case JobState.Completed: completed++; break;
                case JobState.Failed or JobState.Canceled: failed++; break;
            }
        }
        return (_jobs.Count, running, completed, failed);
    }

    private void EvictOldestFinished()
    {
        foreach (string id in _order)
        {
            if (!_jobs.TryGetValue(id, out JobRecord? r)) continue;
            if (r.State is JobState.Running or JobState.Queued) continue;

            _jobs.TryRemove(id, out _);
            return;
        }
    }

    /// <summary>主机停止时取消所有在跑任务。</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (CancellationTokenSource cts in _running.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
