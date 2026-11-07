using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Protocol;
using Xunit;

namespace ChameLab.Tests;

public class DeviceToolsTests
{
    [Fact]
    public void WirePacketsMatchFirmwareContracts()
    {
        Assert.Equal(Convert.FromHexString("D403E8000752"), DeviceTools.HfPayload(0xD4, [0x52], 7, 1000));
        Assert.Equal(Convert.FromHexString("030102030401AABBCCDD01"), DeviceTools.T55Payload(1, 3, [1,2,3,4], [0xAA,0xBB,0xCC,0xDD]));
        Assert.Equal(Convert.FromHexString("0001020304000000000000"), DeviceTools.T55Payload(0, 0, [1,2,3,4], null));
        Assert.Equal(Convert.FromHexString("60070102030405061388"), DeviceTools.AuthTracePayload(7, MfKeyType.A, [1,2,3,4,5,6], 5000));
        Assert.Equal(Convert.FromHexString("6001010203040506C1000001026102090807060504"),
            DeviceTools.ValuePayload(1, MfKeyType.A, [1,2,3,4,5,6], 0xC1, 258, 2, MfKeyType.B, [9,8,7,6,5,4]));
    }

    [Fact]
    public void InvalidWireInputsFailBeforeSending()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceTools.HfPayload(0, [1], 9, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceTools.HfPayload(0, [], 0, 0));
        Assert.Throws<ArgumentException>(() => DeviceTools.HfPayload(0x20, [0x52], 7, 1000));
        Assert.Throws<ArgumentException>(() => DeviceTools.HfPayload(0x20, [], 0, 1000));
        Assert.Throws<ArgumentException>(() => DeviceTools.HfPayload(0x20, new byte[63], 504, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceTools.HfPayload(0, new byte[65], 520, 1000));
        Assert.Equal(67, DeviceTools.HfPayload(0x20, new byte[62], 496, 1000).Length);
        Assert.Equal(69, DeviceTools.HfPayload(0, new byte[64], 512, 1000).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceTools.T55Payload(1, 4, new byte[4], null));
        Assert.Throws<ArgumentException>(() => DeviceTools.T55Payload(0, 1, new byte[4], new byte[6]));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceTools.AuthTracePayload(256, MfKeyType.A, new byte[6], 1000));
        Assert.Throws<ArgumentException>(() => DeviceTools.AuthTracePayload(0, (MfKeyType)0, new byte[6], 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceTools.ValuePayload(1, MfKeyType.A, new byte[6], 0xC1, -1, 2, MfKeyType.B, new byte[6]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ValueBlockRedundancyDetectsEverySingleByteCorruption(int value)
    {
        var encoded = DeviceTools.EncodeValue(value, 42);
        Assert.Equal(value, DeviceTools.DecodeValue(encoded));
        for (var i = 0; i < 16; i++)
        {
            var corrupted = encoded.ToArray();
            corrupted[i] ^= 1;
            Assert.Null(DeviceTools.DecodeValue(corrupted));
        }
    }

    [Fact]
    public void ValueLayoutUsesLittleEndianAndAddressRedundancy()
    {
        Assert.Equal(Convert.FromHexString("04030201FBFCFDFE0403020105FA05FA"), DeviceTools.EncodeValue(0x01020304, 5));
    }

    [Fact]
    public void AccessPermissionsUseLargeSectorGroupsAndRejectBrokenComplements()
    {
        byte[] trailer = new byte[16];
        // All data blocks C1/C2/C3 = 0, trailer = 001 (factory FF0780).
        trailer[6] = 0xFF; trailer[7] = 0x07; trailer[8] = 0x80;
        foreach (var block in new[] { 1, 2, 128, 132, 133, 137, 138, 142 })
            for (var permission = 0; permission < 4; permission++) Assert.Equal(3, DeviceTools.AccessMask(trailer, block, permission));
        Assert.Equal(0, DeviceTools.AccessMask(trailer, 143, 0));
        trailer[6] ^= 1;
        Assert.Equal(0, DeviceTools.AccessMask(trailer, 1, 0));
    }

    [Fact]
    public async Task Gen4PasswordChangesOnlyAfterAcknowledgement()
    {
        var requests = new List<byte[]>();
        var accepted = false;
        var config = new byte[30];
        config[2] = 0x42;
        var client = new Gen4Client((data, _) => { requests.Add(data); return Task.FromResult<byte[]>(accepted ? [0x90,0] : [0x63,0]); }, new byte[4]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.WriteConfigurationAsync(new(config), false));
        accepted = true;
        await client.WriteConfigurationAsync(new(config), false);
        await client.ReadVersionAsync();
        Assert.Equal(0, requests[1][1]);
        Assert.Equal(0x42, requests[2][1]);
        Assert.Equal(0xF0, requests[1][5]);
        await client.WriteConfigurationAsync(new(config), true);
        Assert.Equal(0xF1, requests[^1][5]);
    }

    [Fact]
    public async Task Gen4UltralightUidPreservesLockAndManufacturerBytes()
    {
        var memory = Enumerable.Range(0, 3).ToDictionary(i => i, _ => Enumerable.Repeat((byte)0x55, 16).ToArray());
        var client = MemoryClient(memory);
        var config = new byte[30]; config[0] = 1; config[1] = 1;
        await client.WriteUidAsync(new(config), [1,2,3,4,5,6,7]);
        Assert.Equal(new byte[] {1,2,3,0x88}, memory[0][..4]);
        Assert.Equal(new byte[] {4,5,6,7}, memory[1][..4]);
        Assert.Equal(0, memory[2][0]);
        Assert.All(memory[2][1..], b => Assert.Equal(0x55, b));
        Assert.All(memory[0][4..], b => Assert.Equal(0x55, b));
    }

    [Fact]
    public async Task Gen4PartialMemoryWritePreservesRemainingBytesAndCanReadBack()
    {
        var memory = new Dictionary<int, byte[]> { [0x13] = Enumerable.Repeat((byte)0x55, 16).ToArray() };
        var client = MemoryClient(memory);
        await client.WriteMemoryAsync(0x13, [1,2], 4);
        Assert.Equal(new byte[] {1,2}, await client.ReadMemoryAsync(0x13, 2, 4));
        Assert.All(memory[0x13][2..], b => Assert.Equal(0x55, b));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.WriteMemoryAsync(255, new byte[8], 4));
    }

    [Theory]
    [InlineData("00000006A0", 0x13)]
    [InlineData("0000006666ABCD", 0x13)]
    [InlineData("0000000001", 0xE6)]
    [InlineData("01000006A0", null)]
    [InlineData("000000", null)]
    public void Gen4PackAddressRequiresKnownVersionShape(string version, int? page) =>
        Assert.Equal(page, Gen4Configuration.PackPage(Convert.FromHexString(version)));

    private static Gen4Client MemoryClient(Dictionary<int, byte[]> memory) => new((command, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        var address = command[6];
        if (command[5] == 0xCE) return Task.FromResult(memory[address].ToArray());
        Assert.Equal(0xCD, command[5]);
        memory[address] = command[7..];
        return Task.FromResult<byte[]>([0x90,0]);
    }, new byte[4]);
}
