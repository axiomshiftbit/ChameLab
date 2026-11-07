using ChameLab.Protocol;
using Xunit;

namespace ChameLab.Cli.Tests;

public class EmbeddedCommandsTests
{
    [Fact]
    public async Task ClosingEmbeddedSessionLeavesAppConnectionUsable()
    {
        var transport = new ScriptedTransport(r => ScriptedTransport.Reply(r, [2, 3]));
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await using (var session = new CliSession(TextWriter.Null, TextWriter.Null))
        {
            session.BorrowConnection(connection);
            Assert.Equal(0, await session.ExecuteAsync("exit"));
        }
        Assert.True(transport.IsConnected);
        Assert.Equal(new byte[] { 2, 3 }, (await connection.SendAsync(Command.GetAppVersion)).Data);
    }

    [Fact]
    public async Task WorkspaceResolvesInputsWithoutChangingProcessDirectory()
    {
        var cwd = Environment.CurrentDirectory;
        var root = Path.Combine(Path.GetTempPath(), "chamelab-commands-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var files = new CommandFiles { Root = root };
            await files.WriteAllBytesAsync("card.bin", [1, 2, 3], CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3 }, files.ReadAllBytes("card.bin"));
            Assert.Equal(cwd, Environment.CurrentDirectory);
            Assert.Throws<ArgumentException>(() => files.ReadAllBytes("../outside.bin"));
            Assert.Throws<ArgumentException>(() => files.ReadAllBytes(Path.Combine(root + "-other", "card.bin")));
        }
        finally { Directory.Delete(root, true); }
    }
}
