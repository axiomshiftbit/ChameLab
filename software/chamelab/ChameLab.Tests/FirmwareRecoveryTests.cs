using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public class FirmwareRecoveryTests
{
    private static readonly DeviceInfo Info = new()
    {
        Model = DeviceModel.Ultra, ChipId = "0102030405060708", Address = "C00102030405",
        FirmwareVersion = "2.3", GitVersion = "v2.3.14-cl",
    };

    [Fact]
    public void NewStoreInstanceRecoversIdentityPackageAndExpectedVersion()
    {
        var storage = new MemoryKeyValueStore();
        var package = DfuPackage.Parse(DfuPackageTests.Zip());
        new FirmwareRecoveryStore(storage).Save(Target(), package, new(2, 3, 15, 0));
        var loaded = new FirmwareRecoveryStore(storage).Load()!;
        Assert.Equal(Info.ChipId, loaded.Device.ChipId);
        Assert.Equal(Info.Address, loaded.Device.Address);
        Assert.Equal("original", loaded.Candidate.Id);
        Assert.Equal(package.Firmware.ToArray(), loaded.Package.Firmware.ToArray());
        Assert.Equal(new FirmwareVersion(2, 3, 15, 0), loaded.ExpectedVersion);
    }

    [Fact]
    public void PackageIsOwnedAndCannotBeChangedThroughTheOriginalInput()
    {
        var zip = DfuPackageTests.Zip();
        var package = DfuPackage.Parse(zip);
        Array.Clear(zip);
        Assert.Equal(package.Firmware.ToArray(), DfuPackage.Parse(package.Zip.ToArray()).Firmware.ToArray());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"Version\":99}")]
    public void DamagedRecordIsNotOverwritten(string value)
    {
        var storage = new MemoryKeyValueStore();
        storage.Write(FirmwareRecoveryStore.Key, value);
        Assert.Throws<InvalidDataException>(() => new FirmwareRecoveryStore(storage).Load());
        Assert.Equal(value, storage.Read(FirmwareRecoveryStore.Key));
    }

    [Fact]
    public async Task FailedPersistenceNeverOpensBootloader()
    {
        var target = Target();
        var store = new FirmwareRecoveryStore(new FailingStore());
        await Assert.ThrowsAsync<IOException>(() => new FirmwareUpdateService(new DeviceService([]), [], recovery: store)
            .InstallAsync(DfuPackage.Parse(DfuPackageTests.Zip()), target, new Progress<FirmwareUpdateProgress>()));
        Assert.Equal(0, ((Bootloader)target.Bootloader).Opens);
    }

    [Fact]
    public async Task InterruptedTransferLeavesTaskForANewApplicationSession()
    {
        var storage = new MemoryKeyValueStore();
        var target = Target();
        await Assert.ThrowsAsync<IOException>(() => new FirmwareUpdateService(new DeviceService([]), [], recovery: new(storage))
            .InstallAsync(DfuPackage.Parse(DfuPackageTests.Zip()), target, new Progress<FirmwareUpdateProgress>()));
        Assert.NotNull(new FirmwareRecoveryStore(storage).Load());
    }

    [Fact]
    public void ExplicitDiscardClearsTheTask()
    {
        var store = new FirmwareRecoveryStore(new MemoryKeyValueStore());
        store.Save(Target(), DfuPackage.Parse(DfuPackageTests.Zip()), null);
        store.Clear();
        Assert.Null(store.Load());
    }

    [Fact]
    public void AnotherDeviceCannotOverwriteAnUnfinishedRecoveryTask()
    {
        var store = new FirmwareRecoveryStore(new MemoryKeyValueStore());
        var package = DfuPackage.Parse(DfuPackageTests.Zip());
        store.Save(Target(), package, null);
        var other = Target() with { Device = Info with { ChipId = "0807060504030201" } };
        Assert.Throws<InvalidOperationException>(() => store.RequireCompatible(other.Device));
        Assert.Throws<InvalidOperationException>(() => store.Save(other, package, null));
        Assert.Equal(Info.ChipId, store.Load()!.Device.ChipId);
    }

    private static FirmwareUpdateTarget Target() => new(new(TransportKind.Serial, "original", "Synthetic"), Info, new Bootloader());
    private sealed class FailingStore : IKeyValueStore
    {
        public string? Read(string key) => null;
        public void Write(string key, string value) => throw new IOException("Synthetic full disk");
    }
    private sealed class Bootloader : IDfuTarget
    {
        public int Opens;
        public Task<IDfuChannel?> TryOpenAsync(CancellationToken ct)
        {
            Opens++;
            return Task.FromResult<IDfuChannel?>(new SecureDfuTests.Bootloader { Fault = "crc" });
        }
    }
}
