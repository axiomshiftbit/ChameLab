using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Protocol;
using Xunit;

namespace ChameLab.Tests;

public class SecureDfuTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    public async Task EveryObjectAndReceiptIsChecked(int interval)
    {
        var channel = new Bootloader { PacketReceiptInterval = interval };
        var package = DfuPackage.Parse(DfuPackageTests.Zip());
        await new SecureDfu(channel).FlashAsync(package);
        Assert.Equal(package.Firmware.ToArray(), channel.Data.ToArray());
        Assert.Equal(4, channel.ExecutedData);
    }

    [Theory]
    [InlineData("crc")]
    [InlineData("offset")]
    [InlineData("receipt")]
    [InlineData("zero-object")]
    [InlineData("stale-offset")]
    [InlineData("short-select")]
    [InlineData("zero-mtu")]
    [InlineData("bad-prn")]
    [InlineData("signature")]
    public async Task InvalidTransferNeverExecutesAnImageObject(string fault)
    {
        var channel = new Bootloader { Fault = fault, PacketReceiptInterval = fault == "bad-prn" ? 65536 : 2 };
        await Assert.ThrowsAsync<IOException>(() => new SecureDfu(channel).FlashAsync(DfuPackage.Parse(DfuPackageTests.Zip())));
        Assert.Equal(0, channel.ExecutedData);
    }

    [Fact]
    public async Task CancellationBeforeTransferWritesNothing()
    {
        var channel = new Bootloader();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SecureDfu(channel).FlashAsync(
            DfuPackage.Parse(DfuPackageTests.Zip()), ct: new CancellationToken(true)));
        Assert.Equal(0, channel.Requests);
    }

    [Fact]
    public async Task RetryReplacesPartialImageFromTheBeginning()
    {
        var channel = new Bootloader { Fault = "second-object" };
        var package = DfuPackage.Parse(DfuPackageTests.Zip());
        await Assert.ThrowsAsync<IOException>(() => new SecureDfu(channel).FlashAsync(package));
        Assert.Equal(1, channel.ExecutedData);
        channel.Fault = "";
        await new SecureDfu(channel).FlashAsync(package);
        Assert.Equal(package.Firmware.ToArray(), channel.Data.ToArray());
        Assert.Equal(4, channel.ExecutedData);
    }

    [Fact]
    public void CrcIsStandardAndIncremental()
    {
        Assert.Equal(0xCBF43926u, SecureDfu.Crc32(Encoding.ASCII.GetBytes("123456789")));
        Assert.Equal(0xCBF43926u, SecureDfu.Crc32(Encoding.ASCII.GetBytes("56789"), SecureDfu.Crc32(Encoding.ASCII.GetBytes("1234"))));
    }

    internal sealed class Bootloader : IDfuChannel
    {
        public string Fault = "";
        public bool LoseLastAck;
        public int MaxDataSize => Fault == "zero-mtu" ? 0 : 7;
        public int PacketReceiptInterval { get; set; }
        public readonly List<byte> Data = [];
        private readonly List<byte> _init = [];
        private bool _command;
        public int ExecutedData, Requests;
        public Task<byte[]> RequestAsync(byte[] request, CancellationToken ct)
        {
            Requests++;
            var op = request[0];
            byte[] payload = [];
            if (op == SecureDfu.OpSelect)
            {
                _command = request[1] == 1;
                payload = [.. BitConverter.GetBytes(_command ? 512u : Fault == "zero-object" ? 0u : 32u),
                    .. BitConverter.GetBytes(!_command && Fault == "stale-offset" ? 32u : 0u), 0, 0, 0, 0];
                if (!_command && Fault == "short-select") payload = [1];
            }
            if (op == SecureDfu.OpCreate && request[1] == 1) { _init.Clear(); Data.Clear(); ExecutedData = 0; }
            if (op == SecureDfu.OpCrc) payload = Crc(Fault is "crc" or "offset" || Fault == "second-object" && ExecutedData == 1);
            if (op == SecureDfu.OpExecute)
            {
                if (_command && Fault == "signature") return Task.FromResult<byte[]>([0x60, op, 0x0B, 0x0B]);
                if (!_command) ExecutedData++;
                if (!_command && LoseLastAck && Data.Count == 117) throw new DfuLinkClosedException();
            }
            return Task.FromResult<byte[]>([0x60, op, 1, .. payload]);
        }
        private byte[] Crc(bool corrupt)
        {
            var bytes = (_command ? _init : Data).ToArray();
            return [.. BitConverter.GetBytes((uint)bytes.Length + (corrupt && Fault == "offset" ? 1u : 0u)),
                .. BitConverter.GetBytes(SecureDfu.Crc32(bytes) ^ (corrupt && Fault != "offset" ? 1u : 0u))];
        }
        public Task<byte[]> ReceiveAsync(CancellationToken ct) => Task.FromResult<byte[]>([0x60, 3, 1, .. Crc(Fault == "receipt")]);
        public Task WriteDataAsync(byte[] bytes, CancellationToken ct) { (_command ? _init : Data).AddRange(bytes); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
