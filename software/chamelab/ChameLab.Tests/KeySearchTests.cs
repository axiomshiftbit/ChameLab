using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public class KeySearchTests
{
    [Fact]
    public void RangeIsInclusiveBigEndianAndDoesNotAllocateAllCandidates()
    {
        var range = KeySearchCandidates.Range(Convert.FromHexString("00000000FFFE"), Convert.FromHexString("000000010001"));
        Assert.Equal(4UL, range.Count);
        Assert.Equal("000000010000", Convert.ToHexString(range.At(2)));
        var full = KeySearchCandidates.Range(new byte[6], Enumerable.Repeat((byte)255, 6).ToArray());
        Assert.Equal(1UL << 48, full.Count);
        Assert.Equal("FFFFFFFFFFFF", Convert.ToHexString(full.At(full.Count - 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => full.At(full.Count));
        Assert.Throws<ArgumentException>(() => KeySearchCandidates.Range(new byte[16], Enumerable.Repeat((byte)255, 16).ToArray()));
    }
    [Fact]
    public async Task BudgetAndCancellationRetainNextUnfinishedCandidate()
    {
        var search = new KeySearch(KeySearchCandidates.Range(new byte[4], [0,0,0,3]));
        await search.RunAsync((_, _) => Task.FromResult(false), 2);
        Assert.Equal(2UL, search.Completed); Assert.False(search.Finished);
        await Assert.ThrowsAsync<IOException>(() => search.RunAsync((_, _) => throw new IOException("Link lost"), 2));
        Assert.Equal(2UL, search.Completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.RunAsync((_, _) => throw new OperationCanceledException(), 2));
        Assert.Equal(2UL, search.Completed);
        await search.RunAsync((candidate, _) => Task.FromResult(candidate[3] == 2), 2);
        Assert.Equal(3UL, search.Completed); Assert.Equal(new byte[] {0,0,0,2}, search.Found);
        Assert.True(search.Finished);
    }
    [Fact]
    public void DictionarySnapshotCannotChangeDuringSearchAndRemovesDuplicates()
    {
        byte[] key = [0,0,0,1];
        var dictionary = KeySearchCandidates.Dictionary([key,key], 4);
        key[3] = 2;
        Assert.Equal(1UL, dictionary.Count); Assert.Equal(1, dictionary.At(0)[3]);
        Assert.Throws<ArgumentException>(() => KeySearchCandidates.Dictionary([new byte[6]], 4));
        Assert.Throws<ArgumentException>(() => KeySearchCandidates.Dictionary([], 4));
    }
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(7, false)]
    [InlineData(-1, false)]
    public async Task NtagPreparationNeverAttemptsPasswordAndRequiresReadableUnlimitedAuth(int limit, bool allowed)
    {
        var transport = new SearchTransport { Limit = limit };
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        if (allowed) Assert.Equal(TagSpecificType.Ntag213, (await client.PrepareKeySearchAsync(KeySearchKind.NtagPassword)).Type);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => client.PrepareKeySearchAsync(KeySearchKind.NtagPassword));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Hf14aRaw && r.Data.Length > 5 && r.Data[5] == 0x1B);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1WriteOneBlock or Command.Mf0UlcWrite);
    }
    [Fact]
    public async Task ClassicSearchDistinguishesWrongKeyFromLostTagAndDoesNotWrite()
    {
        var transport = new SearchTransport { Classic = true };
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var target = await client.PrepareKeySearchAsync(KeySearchKind.MifareClassic, 1, MfKeyType.B);
        Assert.False(await client.CheckSearchCandidateAsync(target, new byte[6]));
        transport.AuthStatus = Status.HfTagNo;
        await Assert.ThrowsAsync<ChameleonException>(() => client.CheckSearchCandidateAsync(target, new byte[6]));
        transport.AuthStatus = Status.HfTagOk;
        Assert.True(await client.CheckSearchCandidateAsync(target, new byte[6]));
        transport.Changed = true;
        var before = transport.Requests.Count(r => r.Command == Command.Mf1AuthOneKeyBlock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CheckSearchCandidateAsync(target, new byte[6]));
        Assert.Equal(before, transport.Requests.Count(r => r.Command == Command.Mf1AuthOneKeyBlock));
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1WriteOneBlock or Command.Mf0UlcWrite);
    }
    [Fact]
    public async Task NtagSearchChecksCrcAndDoesNotCountRfFailureAsWrongPassword()
    {
        var transport = new SearchTransport();
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var target = await client.PrepareKeySearchAsync(KeySearchKind.NtagPassword);
        transport.Pack = [4];
        Assert.False(await client.CheckSearchCandidateAsync(target, new byte[4]));
        var crc = HfSniff.CrcA(new byte[] {1,2});
        transport.Pack = [1,2,(byte)crc,(byte)(crc >> 8)];
        Assert.True(await client.CheckSearchCandidateAsync(target, new byte[4]));
        transport.Pack[2] ^= 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CheckSearchCandidateAsync(target, new byte[4]));
        transport.Pack = [];
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CheckSearchCandidateAsync(target, new byte[4]));
        transport.RawAuthStatus = Status.HfTagNo;
        await Assert.ThrowsAsync<ChameleonException>(() => client.CheckSearchCandidateAsync(target, new byte[4]));
    }
    private sealed class SearchTransport : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = [];
        public int Limit { get; init; }
        public bool Classic { get; init; }
        public byte[] Pack { get; set; } = [];
        public Status RawAuthStatus { get; set; } = Status.HfTagOk;
        public bool Changed { get; set; }
        public Status AuthStatus { get; set; } = Status.MfErrAuth;
        public TransportKind Kind => TransportKind.Tcp;
        public string DisplayName => "Fixture";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var r in _parser.Push(data))
            {
                Requests.Add(r);
                var status = Status.HfTagOk; byte[] payload = [];
                switch (r.Command)
                {
                    case Command.ChangeDeviceMode: status = Status.Success; break;
                    case Command.Hf14aScan: payload = [4, (byte)(Changed ? 9 : 1),2,3,4,4,0,(byte)(Classic ? 8 : 0),0]; break;
                    case Command.Mf1AuthOneKeyBlock: status = AuthStatus; break;
                    case Command.Hf14aRaw when r.Data.Length > 5 && r.Data[5] == 0x1B: payload = Pack; status = RawAuthStatus; break;
                    case Command.Hf14aRaw when r.Data.Length > 5 && r.Data[5] == 0x60: payload = [0,4,4,2,1,0,15,3]; break;
                    case Command.Hf14aRaw when r.Data.Length > 5 && r.Data[5] == 0x30:
                        payload = Limit < 0 ? [] : new byte[16]; if (payload.Length > 0) payload[4] = (byte)Limit; break;
                }
                _replies.Writer.TryWrite(FrameCodec.Encode(r.Command, payload, (ushort)status));
            }
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var reply = await _replies.Reader.ReadAsync(ct); reply.CopyTo(buffer); return reply.Length; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
