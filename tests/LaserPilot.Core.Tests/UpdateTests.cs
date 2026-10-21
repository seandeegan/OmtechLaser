using System.Net;
using System.Text;
using System.Text.Json;
using LaserPilot.Core.Updates;

namespace LaserPilot.Core.Tests;

[TestClass]
public sealed class UpdateTests
{
    private const string Releases = """
    [
      { "tag_name": "v0.2.7", "name": "Build 7", "draft": false, "prerelease": false, "body": "older", "html_url": "https://x/7",
        "assets": [ { "id": 7, "name": "LaserPilot-win-x64.zip", "size": 10, "url": "https://api.github.com/repos/o/r/releases/assets/7" } ] },
      { "tag_name": "v0.2.12", "name": "", "draft": false, "prerelease": false, "body": "newest stable", "html_url": "https://x/12",
        "assets": [ { "id": 12, "name": "LaserPilot-win-x64.zip", "size": 10, "url": "https://api.github.com/repos/o/r/releases/assets/12" } ] },
      { "tag_name": "v0.2.13", "draft": true, "prerelease": false, "assets": [ { "id": 13, "name": "LaserPilot-win-x64.zip", "url": "u" } ] },
      { "tag_name": "v0.2.14", "draft": false, "prerelease": true, "assets": [ { "id": 14, "name": "LaserPilot-win-x64.zip", "url": "https://api.github.com/a/14" } ] },
      { "tag_name": "v0.3.0", "draft": false, "prerelease": false, "assets": [ { "id": 30, "name": "source.zip", "url": "u" } ] },
      { "tag_name": "nightly", "draft": false, "prerelease": false, "assets": [] }
    ]
    """;

    [TestMethod]
    public void PicksHighestStableReleaseWithAWindowsBuild()
    {
        using var doc = JsonDocument.Parse(Releases);
        var latest = GitHubUpdateClient.PickLatest(doc.RootElement, includePrereleases: false);
        Assert.IsNotNull(latest);
        Assert.AreEqual(new Version(0, 2, 12), latest.Version);
        Assert.AreEqual("v0.2.12", latest.Title, "an empty release name falls back to the tag");
        Assert.AreEqual(12, latest.Asset.Id);

        var withPre = GitHubUpdateClient.PickLatest(doc.RootElement, includePrereleases: true);
        Assert.AreEqual(new Version(0, 2, 14), withPre!.Version);
    }

    [TestMethod]
    public void VersionParsingAndComparison()
    {
        Assert.IsTrue(GitHubUpdateClient.TryParseVersion("v0.2.15", out var a));
        Assert.AreEqual(new Version(0, 2, 15), a);
        Assert.IsTrue(GitHubUpdateClient.TryParseVersion("release-1.4", out var b));
        Assert.AreEqual(new Version(1, 4), b);
        Assert.IsTrue(GitHubUpdateClient.TryParseVersion("0.2.15-beta", out var c));
        Assert.AreEqual(new Version(0, 2, 15), c);
        Assert.IsFalse(GitHubUpdateClient.TryParseVersion("nightly", out _));

        Assert.IsTrue(GitHubUpdateClient.IsNewer(new Version(0, 2, 16), new Version(0, 2, 15, 0)));
        Assert.IsFalse(GitHubUpdateClient.IsNewer(new Version(0, 2, 15), new Version(0, 2, 15, 0)));
        Assert.IsTrue(GitHubUpdateClient.IsNewer(new Version(0, 2, 1), new Version(0, 1, 2)));
        Assert.IsTrue(GitHubUpdateClient.IsNewer(new Version(0, 2, 1), new Version(0, 1)));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    [TestMethod]
    public async Task Download_FollowsRedirectWithoutTheToken()
    {
        var payload = Encoding.ASCII.GetBytes("zip-bytes");
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.Host == "api.github.com")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://objects.githubusercontent.com/signed?sig=abc");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });
        using var client = new GitHubUpdateClient("o/r", () => "secret-token", handler);
        var path = Path.Combine(Path.GetTempPath(), $"lp-update-test-{Guid.NewGuid():N}.zip");
        try
        {
            await client.DownloadAsync(new ReleaseAsset(1, "LaserPilot-win-x64.zip", payload.Length, "https://api.github.com/repos/o/r/releases/assets/1"), path);
            CollectionAssert.AreEqual(payload, File.ReadAllBytes(path));
            Assert.AreEqual(2, handler.Requests.Count);
            Assert.AreEqual("secret-token", handler.Requests[0].Headers.Authorization?.Parameter);
            Assert.IsNull(handler.Requests[1].Headers.Authorization, "the token must not be sent to the storage host");
            Assert.AreEqual("application/octet-stream", handler.Requests[0].Headers.Accept.Single().MediaType);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task TokenIsNeverSentOutsideGitHubsApi()
    {
        // A reply that points the download somewhere else must not receive the token.
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) });
        using var client = new GitHubUpdateClient("o/r", () => "secret-token", handler);
        var path = Path.Combine(Path.GetTempPath(), $"lp-update-test-{Guid.NewGuid():N}.zip");
        try
        {
            await client.DownloadAsync(new ReleaseAsset(1, "LaserPilot-win-x64.zip", 1, "https://example.com/assets/1"), path);
            Assert.IsNull(handler.Requests.Single().Headers.Authorization);
        }
        finally { File.Delete(path); }

        var insecure = new FakeHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("http://api.github.com/plain");
            return r;
        });
        using var client2 = new GitHubUpdateClient("o/r", () => "secret-token", insecure);
        var ex = await Assert.ThrowsExceptionAsync<UpdateCheckException>(() => client2.GetLatestAsync(false));
        Assert.AreEqual(UpdateFailure.BadResponse, ex.Kind);
        Assert.AreEqual(1, insecure.Requests.Count, "an http:// redirect must not be followed");
    }

    [TestMethod]
    public async Task RenamedRepository_RedirectIsFollowedWithTheToken()
    {
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.StartsWith("/repos/o/r/", StringComparison.Ordinal))
            {
                var r = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                r.Headers.Location = new Uri("https://api.github.com/repositories/42/releases?per_page=30");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Releases) };
        });
        using var client = new GitHubUpdateClient("o/r", () => "t", handler);
        var latest = await client.GetLatestAsync(false);
        Assert.AreEqual(new Version(0, 2, 12), latest!.Version);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("t", handler.Requests[1].Headers.Authorization?.Parameter);
    }

    [TestMethod]
    public async Task PrivateRepoWithoutToken_ExplainsWhatToDo()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new GitHubUpdateClient("o/r", () => null, handler);
        var ex = await Assert.ThrowsExceptionAsync<UpdateCheckException>(() => client.GetLatestAsync(false));
        Assert.AreEqual(UpdateFailure.NotFound, ex.Kind);
        StringAssert.Contains(ex.Message, "token");
        Assert.IsNull(handler.Requests[0].Headers.Authorization);
    }

    [TestMethod]
    public async Task ExpiredToken_IsReported()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new GitHubUpdateClient("o/r", () => "old", handler);
        var ex = await Assert.ThrowsExceptionAsync<UpdateCheckException>(() => client.GetLatestAsync(false));
        Assert.AreEqual(UpdateFailure.Unauthorized, ex.Kind);
    }

    [TestMethod]
    public async Task GetLatest_ParsesTheReleaseList()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Releases) });
        using var client = new GitHubUpdateClient("o/r", () => "t", handler);
        var latest = await client.GetLatestAsync(false);
        Assert.AreEqual(new Version(0, 2, 12), latest!.Version);
        Assert.AreEqual("https://api.github.com/repos/o/r/releases?per_page=30", handler.Requests[0].RequestUri!.ToString());
    }
}
