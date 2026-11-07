using System;
using System.Collections.Generic;
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
public class WriteInspectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnauthenticatedBlockZeroIsOnlyAHintAndRequiresAValidCrc(bool validCrc)
    {
        var block = new byte[18]; new byte[] { 1, 2, 3, 4 }.CopyTo(block, 0);
        var crc = HfSniff.CrcA(block.AsSpan(0, 16)); block[16] = (byte)crc; block[17] = (byte)(crc >> 8);
        if (!validCrc) block[17] ^= 1;
        var transport = new CardTransport { Response = req => req.Command == Command.Hf14aRaw
            ? (Status.HfTagOk, req.Data[5] == 0x30 ? block : Array.Empty<byte>()) : null };
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var result = await new ChameleonClient(connection).InspectWriteTargetAsync(false);
        Assert.Equal(validCrc ? DetectionConfidence.Estimated : DetectionConfidence.Unknown, result!.GenerationConfidence);
        Assert.DoesNotContain(transport.Requests.Where(q => q.Command == Command.Hf14aRaw), q => q.Data[5] is 0xA0 or 0xA2 or 0x90);
    }

    [Theory]
    [InlineData(null, false, DetectionConfidence.Unknown)]
    [InlineData(MagicGeneration.Gen2, false, DetectionConfidence.Unknown)]
    [InlineData(MagicGeneration.Gen3, false, DetectionConfidence.Estimated)]
    [InlineData(MagicGeneration.Gen1a, false, DetectionConfidence.Confirmed)]
    [InlineData(null, true, DetectionConfidence.Confirmed)]
    public void GenerationConfidenceDoesNotPromoteGuessesToConfirmed(MagicGeneration? generation, bool gen4, DetectionConfidence expected)
    {
        var inspection = new WriteTargetInspection(new([1, 2, 3, 4], [4, 0], 8, []), TagSpecificType.Mifare1024, generation, gen4, null);
        Assert.Equal(expected, inspection.GenerationConfidence);
        Assert.False(string.IsNullOrWhiteSpace(inspection.GenerationExplanation));
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, null)]
    [InlineData(true, true, MagicGeneration.Gen1a)]
    public async Task DetectionRequiresBothMagicAcksAndDoesNotAssumeGen2(bool first, bool second, MagicGeneration? expected)
    {
        var transport = new CardTransport { Response = req => req.Command == Command.Hf14aRaw
            ? (Status.HfTagOk, req.Data[5] == 0x40 && first || req.Data[5] == 0x43 && second ? new byte[] {0x0a} : Array.Empty<byte>()) : null };
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var result = await new ChameleonClient(connection).InspectWriteTargetAsync(false);
        Assert.NotNull(result); Assert.Equal(expected, result.Generation);
        Assert.DoesNotContain(transport.Requests, q => q.Command == Command.Mf1WriteOneBlock);
        Assert.DoesNotContain(transport.Requests.Where(q => q.Command == Command.Hf14aRaw), q => q.Data[5] is 0xA0 or 0xA2 or 0x90);
    }
    [Fact]
    public async Task UltralightReadResponseCannotBeMisidentifiedAsGen3()
    {
        var transport = new CardTransport { Response = req => req.Command switch
        {
            Command.Mf1DetectSupport => (Status.HfErrStat, Array.Empty<byte>()),
            Command.Hf14aScan => (Status.HfTagOk, new byte[] {4,1,2,3,4,4,0,0x20,0}),
            Command.Hf14aRaw => (Status.HfTagOk, new byte[18]), _ => null,
        }};
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var result = await new ChameleonClient(connection).InspectWriteTargetAsync(false);
        Assert.NotNull(result); Assert.Null(result.Generation);
    }
    [Fact]
    public void BlanknessRequiresEveryUserBlockButIgnoresManufacturerAndTrailers()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1,2,3,4]);
        var inspected = new WriteTargetInspection(new([1,2,3,4],[4,0],9,[]),TagSpecificType.MifareMini,null,false,dump);
        Assert.Null(inspected.DataIsZeroed);
        for (int b=0;b<20;b++) dump.SetBlock(b, Enumerable.Repeat((byte)(b==0 || MifareClassic.IsTrailer(b) ? 0xFF : 0),16).ToArray());
        Assert.True(inspected.DataIsZeroed);
        dump.SetBlock(1, null); Assert.Null(inspected.DataIsZeroed);
        dump.SetBlock(2, Enumerable.Repeat((byte)1,16).ToArray()); Assert.False(inspected.DataIsZeroed);
    }
    [Fact]
    public async Task JournalNeverContainsPayloadKeysOrCardContents()
    {
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var key = Convert.FromHexString("A1B2C3D4E5F6");
        var data = Convert.FromHexString("112233445566778899AABBCCDDEEFF00");
        await new ChameleonClient(connection).Mf1WriteBlockAsync(2,MfKeyType.B,key,data);
        var log = connection.Journal.Export();
        Assert.Contains("block=2, key=B",log); Assert.Contains("RX",log);
        Assert.DoesNotContain(Convert.ToHexString(key),log); Assert.DoesNotContain(Convert.ToHexString(data),log);
    }
    [Fact]
    public void JournalIsBoundedAndBadObserversCannotBreakOperations()
    {
        var journal = new DeviceJournal(); journal.Changed += () => throw new Exception();
        for (int i=0;i<DeviceJournal.Capacity+10;i++) journal.Add("INFO",i.ToString());
        Assert.Equal(DeviceJournal.Capacity,journal.Snapshot().Length); Assert.Equal("10",journal.Snapshot()[0].Message);
    }
    [Fact]
    public async Task VerificationDetectsAcknowledgedButIncorrectWrite()
    {
        var source = new MifareDump(MifareClassicType.Mini,[1,2,3,4]); source.SetBlock(1,Enumerable.Repeat((byte)0x42,16).ToArray());
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var client = new ChameleonClient(connection); var session = new MifareWriteSession(source,MagicGeneration.Gen2);
        Assert.Empty(await client.WriteMifareDumpToCardAsync(source,[MifareClassic.DefaultKeys[0]],session:session));
        Assert.Equal(new[] {1},await client.VerifyMifareWriteAsync(session));
        Assert.Contains("NOT VERIFIED",connection.Journal.Export());
    }
    [Fact]
    public async Task EmptyWritePlanDoesNotClaimSuccessOrSendAnyCommand()
    {
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var source = new MifareDump(MifareClassicType.Mini, [1,2,3,4]);
        await Assert.ThrowsAsync<ArgumentException>(() => new ChameleonClient(connection).WriteMifareDumpToCardAsync(source, []));
        Assert.Empty(transport.Requests);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UltralightAcknowledgementRequiresMatchingReadback(bool ignoreWrite)
    {
        var data = Enumerable.Repeat((byte)0x5A, 4).ToArray();
        var memory = new byte[4];
        var transport = new CardTransport { Response = request => request.Command switch
        {
            Command.Hf14aScan => (Status.HfTagOk, new byte[] {4,1,2,3,4,4,0,0,0}),
            Command.Hf14aRaw => Reply(request.Data), _ => null,
        }};
        (Status, byte[]) Reply(byte[] request)
        {
            if (request.Length <= 5) return (Status.HfTagOk, []);
            if (request[5] == 0xA2)
            {
                if (!ignoreWrite) memory = request[7..11];
                return (Status.HfTagOk, [0x0A]);
            }
            if (request[5] == 0x30) return (Status.HfTagOk, [..memory, ..new byte[12]]);
            return (Status.HfTagOk, []);
        }
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var card = new CardSave { Tag = TagSpecificType.Ntag213, Uid = [1,2,3,4], Data = [[],[],[],[],data] };
        var failed = await new ChameleonClient(connection).WriteUltralightUserPagesAsync(card);
        Assert.Equal(ignoreWrite ? new[] {4} : Array.Empty<int>(), failed);
        Assert.Contains(ignoreWrite ? "NOT VERIFIED" : "VERIFIED", connection.Journal.Export());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UltralightProtectionRequiresConfirmedPasswordAndConfiguration(bool ignoreConfigWrite)
    {
        var memory = new byte[16];
        var protection = new UltralightProtection(4, true, 0, [1,2,3,4], [5,6]);
        var transport = new CardTransport { Response = r => r.Command switch
        {
            Command.Hf14aScan => (Status.HfTagOk, new byte[] {4,1,2,3,4,4,0,0,0}),
            Command.Hf14aRaw => Reply(r.Data), _ => null,
        }};
        (Status, byte[]) Reply(byte[] request)
        {
            if (request.Length <= 5) return (Status.HfTagOk, []);
            if (request[5] == 0x60) return (Status.HfTagOk, [0,4,4,2,1,0,15,3]);
            if (request[5] == 0x1B) return (Status.HfTagOk, request.AsSpan(6,4).SequenceEqual(memory.AsSpan(8,4)) ? memory[12..14] : []);
            if (request[5] == 0x30) return (Status.HfTagOk, memory.ToArray());
            if (request[5] == 0xA2)
            {
                var page = request[6];
                if (!ignoreConfigWrite || page != 0x29) request.AsSpan(7,4).CopyTo(memory.AsSpan((page - 0x29) * 4,4));
                return (Status.HfTagOk, [0x0A]);
            }
            return (Status.HfTagOk, []);
        }
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var failed = await new ChameleonClient(connection).WriteUltralightUserPagesAsync(new CardSave { Tag = TagSpecificType.Ntag213 }, protection: protection);
        Assert.Equal(ignoreConfigWrite ? 4 : 0, failed.Count);
        Assert.Contains("protection: " + (ignoreConfigWrite ? "NOT VERIFIED" : "VERIFIED"), connection.Journal.Export());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UltralightCWriterAuthenticatesVerifiesAndLeavesConfigurationUntouched(bool ignoreWrite, bool rejectKey)
    {
        var memory = new Dictionary<byte, byte[]>();
        var transport = new CardTransport { Response = request => request.Command switch
        {
            Command.Hf14aScan => (Status.HfTagOk, new byte[] {4,1,2,3,4,4,0,0,0}),
            Command.Mf0UlcAuth => (rejectKey ? Status.MfErrAuth : Status.HfTagOk, Array.Empty<byte>()),
            Command.Mf0UlcWrite => Write(request.Data),
            Command.Mf0UlcRead => (Status.HfTagOk, memory.GetValueOrDefault(request.Data[16], new byte[4])),
            _ => null,
        }};
        (Status, byte[]) Write(byte[] payload)
        {
            Assert.Equal(21, payload.Length);
            if (!ignoreWrite) memory[payload[16]] = payload[17..21];
            return (Status.HfTagOk, []);
        }
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var card = new CardSave { Tag = TagSpecificType.Mf0Icu2,
            Data = Enumerable.Range(0, 48).Select(page => Enumerable.Repeat((byte)(page + 1), 4).ToArray()).ToList() };
        var client = new ChameleonClient(connection);
        if (rejectKey)
        {
            await Assert.ThrowsAsync<ChameleonException>(() => client.WriteUltralightUserPagesAsync(card, new byte[16]));
            Assert.DoesNotContain(transport.Requests, request => request.Command == Command.Mf0UlcWrite);
        }
        else
        {
            var failed = await client.WriteUltralightUserPagesAsync(card, new byte[16]);
            Assert.Equal(ignoreWrite ? 36 : 0, failed.Count);
            var writes = transport.Requests.Where(request => request.Command == Command.Mf0UlcWrite).ToArray();
            Assert.Equal(36, writes.Length);
            Assert.All(writes, request => Assert.InRange(request.Data[16], 4, 39));
            Assert.Equal(36, transport.Requests.Count(request => request.Command == Command.Mf0UlcRead));
        }
        Assert.DoesNotContain(transport.Requests, request => request.Command == Command.Hf14aRaw
            && request.Data.Length > 5 && request.Data[5] == 0x1B);
    }

    [Fact]
    public async Task UltralightCRejectsNtagPasswordAndProtectionBeforeRfCommands()
    {
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var card = new CardSave { Tag = TagSpecificType.Mf0Icu2 };
        await Assert.ThrowsAsync<ArgumentException>(() => client.WriteUltralightUserPagesAsync(card, new byte[4]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.WriteUltralightUserPagesAsync(card,
            new byte[16], protection: new UltralightProtection(4, true, 0, new byte[4], new byte[2])));
        Assert.Empty(transport.Requests);
    }

    private sealed class CardTransport : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = new();
        public Action<ChameleonFrame>? OnRequest { get; set; }
        public Func<ChameleonFrame, (Status Status, byte[] Payload)?>? Response { get; set; }
        public MifareClassicPrngType Prng { get; init; } = MifareClassicPrngType.Weak;
        public TransportKind Kind => TransportKind.Tcp;
        public string DisplayName => "Test card";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(data))
            {
                Requests.Add(request);
                var (status, payload) = request.Command switch
                {
                    Command.ChangeDeviceMode => (Status.Success, Array.Empty<byte>()),
                    Command.Hf14aScan => (Status.HfTagOk, new byte[] { 4, 1, 2, 3, 4, 4, 0, 9, 0 }),
                    Command.Mf1DetectPrng => (Status.HfTagOk, new byte[] { (byte)Prng }),
                    Command.Mf1DetectNtDist => (Status.HfTagOk, new byte[8]),
                    Command.Mf1StaticNestedAcquire => (Status.HfTagOk, new byte[20]),
                    Command.Mf1DarksideAcquire => (Status.HfTagOk, new byte[] { (byte)DarksideResult.Fixed }),
                    Command.Mf1ReadOneBlock => (Status.HfTagOk, new byte[16]),
                    _ => (Status.HfTagOk, Array.Empty<byte>()),
                };
                (status, payload) = Response?.Invoke(request) ?? (status, payload);
                OnRequest?.Invoke(request);
                _replies.Writer.TryWrite(FrameCodec.Encode(request.Command, payload, (ushort)status));
            }
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var reply = await _replies.Reader.ReadAsync(ct);
            reply.CopyTo(buffer);
            return reply.Length;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
