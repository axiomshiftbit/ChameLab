using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public class FirmwareCapabilitiesTests
{
    public static IEnumerable<object[]> Features() => Enum.GetValues<FirmwareFeature>().Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(Features))]
    public void UnknownIsDistinctFromMissingAndPartialCommandSets(FirmwareFeature feature)
    {
        var unknown = new FirmwareCapabilities(null);
        Assert.Equal(FeatureSupport.Unknown, unknown.Check(feature));
        Assert.True(unknown.Allows(feature));
        var commands = FirmwareCapabilities.Required(feature).Select(c => (ushort)c).ToArray();
        Assert.True(new FirmwareCapabilities(commands).Supports(feature));
        var partial = new FirmwareCapabilities(commands[..^1]);
        Assert.Equal(FeatureSupport.Unsupported, partial.Check(feature));
        Assert.Throws<NotSupportedException>(() => partial.Require(feature));
    }

    [Theory]
    [InlineData(Status.InvalidCmd)]
    [InlineData(Status.NotImplemented)]
    [InlineData(Status.Success)]
    public async Task MissingOrEmptyReportRemainsUnknown(Status status)
    {
        var transport = new Link(status, []);
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        Assert.Empty(await client.GetCapabilitiesAsync());
        Assert.Null(client.Capabilities);
        Assert.Empty(await client.GetCapabilitiesAsync());
        Assert.Equal(1, transport.Requests);
    }

    [Fact]
    public async Task CorruptReportDoesNotBecomeACachedAbsence()
    {
        await using var connection = new ChameleonConnection(new Link(Status.Success, [1])); await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => client.GetCapabilitiesAsync());
        Assert.Null(client.Capabilities);
    }

    [Fact]
    public async Task ValidReportIsReadOnceAndCannotBeChangedByCallers()
    {
        var link = new Link(Status.Success, [0x07, 0xD0, 0x27, 0x10]); // known and future command IDs
        await using var connection = new ChameleonConnection(link); await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var first = await client.GetCapabilitiesAsync();
        Assert.Same(first, await client.GetCapabilitiesAsync());
        Assert.Equal(1, link.Requests);
        Assert.Equal(new ushort[] { 2000, 10000 }, first);
        Assert.Throws<NotSupportedException>(() => ((IList<ushort>)first)[0] = 1);
    }

    private sealed class Link(Status status, byte[] reply) : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public int Requests;
        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "Synthetic";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var frame in new FrameParser().Push(data))
            { Requests++; _replies.Writer.TryWrite(FrameCodec.Encode(frame.Command, reply, (ushort)status)); }
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var data = await _replies.Reader.ReadAsync(ct); data.CopyTo(buffer); return data.Length; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
