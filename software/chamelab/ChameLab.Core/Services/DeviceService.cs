using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Transport;

namespace ChameLab.Services;

public enum ConnectionState { Disconnected, Connecting, Connected }

/// <summary>
/// Central connection state holder shared by all view models. Owns the active
/// <see cref="ChameleonConnection"/>/<see cref="ChameleonClient"/> and the set of
/// platform-provided transport scanners.
/// </summary>
public sealed class DeviceService(IEnumerable<ITransportScanner> scanners)
{
    private readonly IReadOnlyList<ITransportScanner> _scanners = scanners.ToList();
    public DeviceJournal Journal { get; } = new();
    private ChameleonConnection? _conn;
    private Task _disconnecting = Task.CompletedTask;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _resumeGate = new(1, 1);
    private CancellationTokenSource? _connecting;
    private int _connectionIntent;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public ChameleonClient? Client { get; private set; }
    public DeviceInfo? DeviceInfo { get; private set; }
    /// <summary>What the current (or last) connection was made to.</summary>
    public TransportCandidate? Candidate { get; private set; }

    public IReadOnlyList<ITransportScanner> Scanners => _scanners;

    public event Action<ConnectionState>? StateChanged;

    private void SetState(ConnectionState s)
    {
        if (State == s) return;
        Journal.Add("INFO", $"{nameof(ConnectionState)}: {s}");
        State = s;
        StateChanged?.Invoke(s);
    }

    /// <summary>
    /// Scans the registered transports and returns a flat candidate list. <paramref name="trigger"/>
    /// says why the scan runs: <see cref="AutoScan.Never"/> is an explicit Rescan and scans
    /// everything; automatic scans skip scanners that don't allow running for that reason.
    /// </summary>
    public async Task<IReadOnlyList<TransportCandidate>> ScanAsync(
        AutoScan trigger = AutoScan.Never, CancellationToken ct = default)
    {
        var all = new List<TransportCandidate>();
        foreach (var scanner in _scanners)
        {
            if (!RunsOn(scanner, trigger)) continue;
            try { all.AddRange(await scanner.ScanAsync(trigger, ct)); }
            catch (Exception ex) { Console.WriteLine($"[Scan] {scanner.DisplayName} failed: {ex.GetType().Name}"); }
        }
        return all;
    }

    /// <summary>Whether a scan for <paramref name="trigger"/> runs <paramref name="scanner"/>; watchers report on their own.</summary>
    public static bool RunsOn(ITransportScanner scanner, AutoScan trigger) =>
        trigger == AutoScan.Never || (scanner.AutoScan >= trigger && scanner is not ITransportWatcher);

    /// <summary>Scans only the transport of <paramref name="kind"/>, whatever its auto-scan policy.</summary>
    public async Task<IReadOnlyList<TransportCandidate>> ScanAsync(TransportKind kind, CancellationToken ct = default)
    {
        var scanner = _scanners.FirstOrDefault(s => s.Kind == kind);
        return scanner is null ? Array.Empty<TransportCandidate>() : await scanner.ScanAsync(ct);
    }

    public async Task ConnectAsync(TransportCandidate candidate, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _connectionIntent);
        CancelConnect();
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _connecting = opening;
        var entered = false;
        try
        {
            await _connectionGate.WaitAsync(opening.Token);
            entered = true;
            await DisconnectCoreAsync();
            opening.Token.ThrowIfCancellationRequested();
            var scanner = _scanners.First(s => s.Kind == candidate.Kind);
            var conn = new ChameleonConnection(scanner.Create(candidate)) { Journal = Journal };
            SetState(ConnectionState.Connecting);
            conn.Disconnected += ex => { _ = HandleDropAsync(conn); };
            try
            {
                await conn.OpenAsync(opening.Token);
                var client = new ChameleonClient(conn);
                var info = await client.GetDeviceInfoAsync(opening.Token);
                opening.Token.ThrowIfCancellationRequested();
                if (conn.IsClosed) throw new ChameleonDisconnectedException();
                _conn = conn;
                Client = client;
                DeviceInfo = info;
                Candidate = candidate;
                SetState(ConnectionState.Connected);
            }
            catch
            {
                await conn.DisposeAsync();
                SetState(ConnectionState.Disconnected);
                throw;
            }
        }
        finally
        {
            if (ReferenceEquals(_connecting, opening)) _connecting = null;
            if (entered) _connectionGate.Release();
        }
    }

    private void CancelConnect()
    {
        try { _connecting?.Cancel(); }
        catch (ObjectDisposedException) { /* the completing attempt already released its token */ }
    }

    /// <summary>
    /// On return from the background, keep a responsive BLE session or reopen the same
    /// device directly. Never replay the card operation that happened before suspension.
    /// </summary>
    public async Task ResumeBleAsync(TransportCandidate expected, CancellationToken ct = default)
    {
        if (expected.Kind != TransportKind.Ble) return;
        await _resumeGate.WaitAsync(ct);
        var intent = Volatile.Read(ref _connectionIntent);
        try
        {
            if (State == ConnectionState.Connecting || Candidate?.Kind != expected.Kind || Candidate.Id != expected.Id) return;
            var conn = _conn;
            if (conn is not null && Client is { } client)
            {
                // Do not probe through an active card command or a long-running attack.
                if (conn.HasPendingRequests) return;
                try
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probe.CancelAfter(TimeSpan.FromSeconds(4));
                    await client.GetAppVersionAsync(probe.Token).WaitAsync(probe.Token);
                    return;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // A user-selected connection or explicit disconnect wins over recovery.
                    if (Volatile.Read(ref _connectionIntent) != intent ||
                        _conn is not null && !ReferenceEquals(_conn, conn)) return;
                    if (conn.HasPendingRequests) return; // another operation started during the probe
                }
            }
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _connectionIntent) != intent) return;
            Journal.Add("INFO", "Restoring Bluetooth connection after resume");
            await ConnectAsync(expected, ct);
        }
        finally { _resumeGate.Release(); }
    }

    private async Task HandleDropAsync(ChameleonConnection conn)
    {
        // A drop during the handshake fails ConnectAsync itself; a late drop from an older
        // connection must not tear down the current one.
        await _connectionGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_conn, conn)) return;
            await DisconnectCoreAsync();
        }
        finally { _connectionGate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        Interlocked.Increment(ref _connectionIntent);
        CancelConnect();
        await _connectionGate.WaitAsync();
        try { await DisconnectCoreAsync(); }
        finally { _connectionGate.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        var conn = _conn;
        _conn = null;
        Client = null;
        DeviceInfo = null;
        if (conn is not null)
            _disconnecting = conn.DisposeAsync().AsTask();
        // Reflect the logical disconnect immediately; reconnect still waits for the old
        // native handle below, so a slow USB/BLE close cannot make the header look stuck.
        SetState(ConnectionState.Disconnected);
        await _disconnecting;
    }
}
