using ChameLab.Protocol;
using Xunit;

namespace ChameLab.Cli.Tests;

public class ExclusiveConsoleTests
{
    [Theory]
    [InlineData("hw version", true)]
    [InlineData("hf mf rdbl --help", true)]
    [InlineData("hf mf rdbl --blk 1 -k 001122334455", false)]
    [InlineData("hf mf eload -f 'private-file'", false)]
    [InlineData("rem synthetic-private-text", false)]
    [InlineData("hw version --unexpected", false)]
    [InlineData("rem 'unterminated", false)]
    public void HistoryNeverRetainsArguments(string command, bool safe) => Assert.Equal(safe, CommandHistory.IsSafe(command));

    [Fact]
    public void DiskHistoryFiltersOldSensitiveEntriesAndDeduplicates()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, ["hw version", "rem synthetic-private-text"]);
            var history = new CommandHistory(file);
            history.Add("hw version");
            history.Add("hf mf eload -f synthetic-private-file");
            history.Add("hw chipid");
            Assert.Equal(new[] { "hw version", "hw chipid" }, history.Lines);
            Assert.Equal(history.Lines, File.ReadAllLines(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task BorrowedConsoleWaitsForOwnerAndCancellationLeavesConnectionUsable()
    {
        var transport = new ScriptedTransport(r => ScriptedTransport.Reply(r));
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await using var console = new CliSession(TextWriter.Null, TextWriter.Null);
        console.BorrowConnection(connection);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = connection.RunExclusiveAsync(async () =>
        {
            await connection.RunExclusiveAsync(() => connection.SendAsync(Command.GetDeviceChipId));
            entered.SetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        var blocked = console.ExecuteAsync("hw chipid", stop.Token);
        Assert.False(blocked.IsCompleted);
        stop.Cancel();
        Assert.Equal(130, await blocked.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(transport.Requests);
        release.SetResult();
        await owner.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await console.ExecuteAsync("hw chipid").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, transport.Requests.Count);
        Assert.False(connection.IsClosed);
    }
}
