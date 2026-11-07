using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Protocol;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public class FirmwareDownloadTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("missing-hash")]
    public async Task DownloadMustMatchPublishedSizeAndSha256(string scenario)
    {
        byte[] original = [1, 2, 3, 4];
        var body = scenario switch { "corrupt" => new byte[] { 1, 2, 3, 5 }, "truncated" => [1, 2], "oversized" => [1, 2, 3, 4, 5], _ => original };
        using var http = new HttpClient(new Handler(body));
        var service = new FirmwareUpdateService(new DeviceService([]), [], http);
        var release = new FirmwareRelease(default, "v1.5.1", "", "", "", "https://example.invalid/package.zip", 4,
            scenario == "missing-hash" ? null : Convert.ToHexString(SHA256.HashData(original)));
        if (scenario == "valid") Assert.Equal(original, await service.DownloadAsync(release, null));
        else await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, null));
    }

    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
