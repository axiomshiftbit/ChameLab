using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Transport;
using ChameLab.Localization;

namespace ChameLab.Services;

/// <summary>
/// The newest firmware published on GitHub for one device model. <see cref="Tag"/> is the
/// release's (ChameLab's) version, <see cref="Version"/> the firmware's own.
/// </summary>
public sealed record FirmwareRelease(
    FirmwareVersion Version, string Tag, string Name, string Notes, string PageUrl,
    string? PackageUrl, long PackageSize, string? Sha256 = null);

public sealed record FirmwareUpdateTarget(TransportCandidate Candidate, DeviceInfo Device, IDfuTarget Bootloader);

public enum FirmwareUpdateStep { Downloading, Checking, Restarting, WaitingForBootloader, Flashing, Reconnecting }

public readonly record struct FirmwareUpdateProgress(FirmwareUpdateStep Step, double Fraction = 0);

/// <summary>
/// Checks GitHub releases for newer firmware and installs a DFU package: restarts
/// the device into its bootloader, flashes it over USB serial or BLE and reconnects.
/// </summary>
public sealed partial class FirmwareUpdateService(DeviceService device, IReadOnlyList<IDfuLocator> locators, HttpClient? http = null,
    FirmwareRecoveryStore? recovery = null)
{
    /// <summary>Where releases (with <c>ultra-dfu-app.zip</c> / <c>lite-dfu-app.zip</c>) are published.</summary>
    public const string Repository = "axiomshiftbit/ChameLab";

    private static readonly TimeSpan BootloaderTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(40);

    private static readonly HttpClient SharedHttp = CreateHttp();
    private HttpClient Http => http ?? SharedHttp;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    /// <summary>The line releases start with: <c>ChameLab v1.0.7 with firmware v2.3.2-cl</c>.</summary>
    [GeneratedRegex(@"\bfirmware (v\d+\.\d+\S*)", RegexOptions.IgnoreCase)]
    private static partial Regex FirmwareLine();

    /// <summary>Whether firmware can be installed to a device connected over <paramref name="kind"/>.</summary>
    public bool CanInstall(TransportKind? kind) => locators.Any(l => l.Kind == kind);
    public bool CanRecover => recovery is not null && locators.Count > 0;

    public Task<FirmwareRecovery?> LoadRecoveryAsync() => Task.Run(() => recovery?.Load());
    public Task DiscardRecoveryAsync() => Task.Run(() => recovery?.Clear());
    public Task CheckRecoveryTargetAsync(DeviceInfo info) => Task.Run(() => recovery?.RequireCompatible(info));

    public FirmwareUpdateTarget PrepareRecovery(FirmwareRecovery saved)
    {
        var locator = locators.FirstOrDefault(l => l.Kind == saved.Candidate.Kind)
            ?? throw new NotSupportedException(Strings.Firmware_NotSupportedHere);
        return new(saved.Candidate, saved.Device, locator.Prepare(saved.Candidate, saved.Device));
    }

    /// <summary>Whether a release file is the application package for <paramref name="model"/>: <c>ultra-dfu-app-2.3.2-cl.zip</c>.</summary>
    public static bool IsPackage(string? name, DeviceModel model) =>
        name is not null
        && name.StartsWith(model == DeviceModel.Lite ? "lite-dfu-app" : "ultra-dfu-app", StringComparison.Ordinal)
        && name.EndsWith(".zip", StringComparison.Ordinal);

    /// <summary>The latest (non pre-release) release, with the package for <paramref name="model"/>.</summary>
    public async Task<FirmwareRelease> GetLatestAsync(DeviceModel model, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException(Strings.Firmware_NoRelease);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var line = FirmwareLine().Match(notes);
        if (!line.Success || !FirmwareVersion.TryParse(line.Groups[1].Value, out var version))
            throw new InvalidOperationException(string.Format(Strings.Firmware_ReleaseNoVersion, tag));

        string? url = null;
        long size = 0;
        string? checksum = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!IsPackage(asset.GetProperty("name").GetString(), model)) continue;
            url = asset.GetProperty("browser_download_url").GetString();
            size = asset.GetProperty("size").GetInt64();
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } value &&
                value.StartsWith("sha256:", StringComparison.Ordinal)) checksum = value[7..];
        }

        return new FirmwareRelease(
            version, tag,
            root.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name ? name : tag,
            notes,
            root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repository}/releases",
            url, size, checksum);
    }

    public async Task<byte[]> DownloadAsync(FirmwareRelease release, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (release.PackageUrl is null)
            throw new InvalidOperationException(string.Format(Strings.Firmware_NoPackageForDevice, release.Tag));
        if (release.PackageSize is <= 0 or > DfuPackage.MaxZipSize || release.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException(Strings.Firmware_DownloadCorrupted);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2)); // includes the response stream, not just HTTP headers
        using var response = await Http.GetAsync(release.PackageUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } size && size != release.PackageSize)
            throw new InvalidDataException(Strings.Firmware_DownloadCorrupted);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream((int)release.PackageSize);
        var chunk = new byte[16384];
        int n;
        while ((n = await stream.ReadAsync(chunk, timeout.Token).AsTask().WaitAsync(timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > release.PackageSize) throw new InvalidDataException(Strings.Firmware_DownloadCorrupted);
            buffer.Write(chunk, 0, n);
            progress?.Report((double)buffer.Length / release.PackageSize);
        }
        var bytes = buffer.ToArray();
        if (bytes.Length != release.PackageSize || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(hash)))
            throw new InvalidDataException(Strings.Firmware_DownloadCorrupted);
        return bytes;
    }

    /// <summary>
    /// The bootloader of the device connected now, to pass to <see cref="InstallAsync"/>
    /// (and again for a retry, when the device already waits in its bootloader).
    /// </summary>
    public FirmwareUpdateTarget PrepareTarget()
    {
        var candidate = device.Candidate;
        var info = device.DeviceInfo;
        if (candidate is null || info is null) throw new InvalidOperationException(Strings.Firmware_WrongDevice);
        var locator = locators.FirstOrDefault(l => l.Kind == candidate.Kind)
            ?? throw new NotSupportedException(Strings.Firmware_NotSupportedHere);
        return new FirmwareUpdateTarget(candidate, info, locator.Prepare(candidate, info));
    }

    /// <summary>Checks compatibility before any restart and confirms the original chip after DFU.</summary>
    public async Task InstallAsync(
        DfuPackage package, FirmwareUpdateTarget target,
        IProgress<FirmwareUpdateProgress> progress, CancellationToken ct = default,
        FirmwareVersion? expectedVersion = null)
    {
        if (!await _installGate.WaitAsync(0, ct)) throw new InvalidOperationException(Strings.Firmware_Installing);
        try
        {
            progress.Report(new(FirmwareUpdateStep.Checking));
            package.RequireModel(target.Device.Model);
            if (device.Client is { } client)
            {
                device.DeviceInfo?.Capabilities.Require(FirmwareFeature.FirmwareUpdate);
                if (device.DeviceInfo?.ChipId != target.Device.ChipId || device.DeviceInfo?.Model != target.Device.Model)
                    throw new InvalidOperationException(Strings.Firmware_WrongDevice);
                if (target.Candidate.Kind == TransportKind.Ble)
                {
                    var battery = await client.GetBatteryInfoAsync(ct);
                    for (var attempt = 0; battery.Millivolts == 0 && attempt < 6; attempt++)
                    {
                        await Task.Delay(1000, ct);
                        battery = await client.GetBatteryInfoAsync(ct);
                    }
                    if (battery.Millivolts == 0 || battery.Percent < 20)
                        throw new InvalidOperationException(Strings.Firmware_ChargeBeforeUpdate);
                }
                await Task.Run(() => recovery?.Save(target, package, expectedVersion), ct);
                progress.Report(new(FirmwareUpdateStep.Restarting));
                await client.EnterBootloaderAsync(ct);
                await device.DisconnectAsync();
            }
            else
                await Task.Run(() => recovery?.Save(target, package, expectedVersion), ct);

            progress.Report(new(FirmwareUpdateStep.WaitingForBootloader));
            var channel = await PollAsync(token => target.Bootloader.TryOpenAsync(token), BootloaderTimeout, ct,
                Strings.Firmware_NoBootloader);
            progress.Report(new(FirmwareUpdateStep.Flashing));
            await using (channel)
            {
                await new SecureDfu(channel).FlashAsync(package,
                    new Progress<double>(f => progress.Report(new(FirmwareUpdateStep.Flashing, f))), ct);
            }

            progress.Report(new(FirmwareUpdateStep.Reconnecting));
            // Candidate and identity survive retries. Never declare success merely because the
            // transfer reached 100%, or connect to the first unrelated device in the scan result.
            await PollAsync(async token =>
            {
                var found = await device.ScanAsync(target.Candidate.Kind, token);
                var candidate = found.FirstOrDefault(c => c.Id == target.Candidate.Id);
                if (candidate is null && locators.FirstOrDefault(l => l.Kind == target.Candidate.Kind) is { } locator)
                {
                    var matching = await Task.Run(() => found.Where(c => locator.MatchesApplication(c, target.Device)).ToArray(), token);
                    if (matching.Length > 1) throw new InvalidOperationException(Strings.Firmware_WrongDevice);
                    candidate = matching.SingleOrDefault();
                }
                if (candidate is null) return null;
                await device.ConnectAsync(candidate, token);
                if (device.DeviceInfo is not { } info || info.ChipId != target.Device.ChipId || info.Model != target.Device.Model)
                {
                    await device.DisconnectAsync();
                    throw new InvalidOperationException(Strings.Firmware_WrongDevice);
                }
                return info;
            }, ReconnectTimeout, ct, Strings.Firmware_DidNotReturn);
            if (expectedVersion is { } expected &&
                (FirmwareVersion.OfDevice(device.DeviceInfo!.GitVersion, device.DeviceInfo.FirmwareVersion) is not { } actual || actual.CompareTo(expected) != 0))
                throw new InvalidOperationException(Strings.Firmware_VersionNotConfirmed);
            await Task.Run(() => recovery?.Clear(), ct);
        }
        finally { _installGate.Release(); }
    }

    private static async Task<T> PollAsync<T>(Func<CancellationToken, Task<T?>> probe, TimeSpan timeout,
        CancellationToken ct, string timeoutMessage) where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        Exception? last = null;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    if (await probe(deadline.Token) is { } found) return found;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; }
                await Task.Delay(300, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(last is null ? timeoutMessage : string.Format(Strings.Firmware_TimeoutLastError, timeoutMessage, last.Message), last);
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChameLab");
        return http;
    }
}
