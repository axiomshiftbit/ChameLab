using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;

namespace ChameLab.Services;

public readonly record struct KeySearchProgress(ulong Completed, ulong Total, TimeSpan Elapsed, double AttemptsPerSecond);

/// <summary>Only completed authentication attempts advance the cursor; cancellation/transport errors can be retried.</summary>
public sealed class KeySearch(KeySearchCandidates candidates)
{
    public KeySearchCandidates Candidates { get; } = candidates;
    public ulong Completed { get; private set; }
    public byte[]? Found { get; private set; }
    public bool Finished => Found is not null || Completed == Candidates.Count;
    private TimeSpan _elapsed;
    private int _running;

    public async Task RunAsync(Func<byte[], CancellationToken, Task<bool>> authenticate, int attemptBudget,
        IProgress<KeySearchProgress>? progress = null, CancellationToken ct = default)
    {
        if (attemptBudget is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(attemptBudget));
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("Search is already running.");
        var watch = Stopwatch.StartNew();
        try
        {
            for (var i = 0; i < attemptBudget && !Finished; i++)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = Candidates.At(Completed);
                var matched = await authenticate(candidate, ct);
                Completed++;
                if (matched) Found = candidate;
                var time = _elapsed + watch.Elapsed;
                progress?.Report(new(Completed, Candidates.Count, time, Completed / Math.Max(0.001, time.TotalSeconds)));
            }
        }
        finally { _elapsed += watch.Elapsed; Interlocked.Exchange(ref _running, 0); }
    }
}
