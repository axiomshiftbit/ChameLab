using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public class LfProtocolCompatibilityTests
{
    [Theory]
    [InlineData(Status.ParErr)]
    [InlineData(Status.InvalidCmd)]
    [InlineData(Status.NotImplemented)]
    public async Task OldFirmwareFallsBackWithoutSendingAnEmPassword(Status response)
    {
        var transport = new Fixture { AutoStatus = response };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new ChameleonClient(connection).LfAutoScanAsync();
        Assert.Equal(TagSpecificType.Em410x, result.Type);
        Assert.Equal(5, result.Id.Length);
        Assert.Contains(transport.Requests, r => r.Command == Command.Em410xScan);
        Assert.All(transport.Requests.Where(r => (ushort)r.Command == 3037), r => Assert.Empty(r.Data));
    }

    [Fact]
    public async Task NoTagIsNotAnUnsupportedProtocolAndDoesNotRepeatTheScan()
    {
        var transport = new Fixture { AutoStatus = Status.LfTagNoFound };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<ChameleonException>(() => new ChameleonClient(connection).LfAutoScanAsync());
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Em410xScan);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(8)]
    public async Task MalformedAutoReplyNeverBecomesASavedCard(int length)
    {
        var transport = new Fixture { AutoLength = length };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => new ChameleonClient(connection).LfAutoScanAsync());
    }

    [Fact]
    public async Task Ambiguous3037NeverProvesEmPasswordSupport()
    {
        var transport = new Fixture();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => new ChameleonClient(connection).PrepareKeySearchAsync(KeySearchKind.Em4x05));
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.LfAutoScan or Command.Em4x05CheckPassword or Command.Em4x05ReadWord);
    }

    [Fact]
    public async Task NewFirmwareUsesUnambiguous3038AndVerifiesTheSameCard()
    {
        var transport = new Fixture { SupportsEm = true };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var target = await client.PrepareKeySearchAsync(KeySearchKind.Em4x05);
        Assert.True(await client.CheckSearchCandidateAsync(target, new byte[4]));
        Assert.Single(transport.Requests, r => (ushort)r.Command == 3038);
        Assert.DoesNotContain(transport.Requests, r => (ushort)r.Command == 3037);
    }

    private sealed class Fixture : ITransport
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = [];
        public Status AutoStatus { get; init; } = Status.LfTagOk;
        public int AutoLength { get; init; } = 7;
        public bool SupportsEm { get; init; }
        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "Synthetic compatibility fixture";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(bytes))
            {
                Requests.Add(request);
                var status = Status.Success;
                byte[] data = [];
                switch (request.Command)
                {
                    case Command.GetDeviceCapabilities:
                        data = SupportsEm ? [11, 221, 11, 222] : [11, 221]; break;
                    case Command.LfAutoScan:
                        status = AutoStatus; data = new byte[AutoLength];
                        if (data.Length >= 2) { data[0] = (byte)((ushort)TagSpecificType.Em410x >> 8); data[1] = (byte)TagSpecificType.Em410x; }
                        break;
                    case Command.Em410xScan:
                        status = Status.LfTagOk; data = [(byte)((ushort)TagSpecificType.Em410x >> 8), (byte)TagSpecificType.Em410x, 1, 2, 3, 4, 5]; break;
                    case Command.Em4x05ReadWord:
                    case Command.Em4x05CheckPassword:
                        status = Status.LfTagOk; data = [1, 2, 3, 4]; break;
                }
                _responses.Writer.TryWrite(FrameCodec.Encode(request.Command, data, (ushort)status));
            }
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var data = await _responses.Reader.ReadAsync(ct); data.CopyTo(buffer); return data.Length;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
