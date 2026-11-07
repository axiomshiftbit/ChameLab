using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Protocol;

namespace ChameLab.Services;

public sealed record JournalEntry(long Sequence, DateTimeOffset Time, string Level, string Message);

/// <summary>Bounded session log. Only command metadata and explicit operation summaries; never wire payloads.</summary>
public sealed class DeviceJournal
{
    private readonly object _gate = new();
    private readonly Queue<JournalEntry> _entries = new();
    private long _sequence;
    public const int Capacity = 10000;
    public event Action? Changed;
    public JournalEntry[] Snapshot() { lock (_gate) return _entries.ToArray(); }
    public void Clear() { lock (_gate) _entries.Clear(); Changed?.Invoke(); }
    public void Add(string level, string message)
    {
        lock (_gate)
        {
            _entries.Enqueue(new(++_sequence, DateTimeOffset.Now, level, message));
            while (_entries.Count > Capacity) _entries.Dequeue();
        }
        // A log subscriber must never break an in-flight write.
        if (Changed is { } handlers)
            foreach (Action handler in handlers.GetInvocationList()) try { handler(); } catch { }
    }
    public string Export() => string.Join(Environment.NewLine,
        Snapshot().Select(e => $"{e.Time:yyyy-MM-dd HH:mm:ss.fff zzz} #{e.Sequence} [{e.Level}] {e.Message}"));
    public static string Address(Command command, byte[]? payload) => command switch
    {
        Command.Mf1AuthOneKeyBlock or Command.Mf1ReadOneBlock or Command.Mf1WriteOneBlock
            when payload is { Length: >= 2 } => $" block={payload[1]}, key={(payload[0] == 0x60 ? 'A' : 'B')}",
        Command.Mf1ReadEmuBlockData or Command.Mf1WriteEmuBlockData
            when payload is { Length: >= 1 } => $" block={payload[0]}",
        _ => string.Empty,
    };
}
