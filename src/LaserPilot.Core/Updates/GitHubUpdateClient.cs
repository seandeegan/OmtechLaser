using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LaserPilot.Core.Updates;

public sealed record ReleaseAsset(long Id, string Name, long Size, string ApiUrl);

public sealed record ReleaseInfo(
    Version Version, string Tag, string Title, string Notes, DateTimeOffset? Published,
    string PageUrl, ReleaseAsset Asset, bool Prerelease);

public enum UpdateFailure { Network, Unauthorized, NotFound, RateLimited, BadResponse }

public sealed class UpdateCheckException(UpdateFailure kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public UpdateFailure Kind { get; } = kind;
}

/// <summary>
/// Finds and downloads LaserPilot releases from a (possibly private) GitHub
/// repository. Private repositories need a token with read access to the
/// repository's contents; it is sent only to api.github.com.
/// </summary>
public sealed class GitHubUpdateClient : IDisposable
{
    public const string AssetName = "LaserPilot-win-x64.zip";

    private readonly string _owner;
    private readonly string _repo;
    private readonly Func<string?> _token;
    private readonly HttpClient _http;

    public GitHubUpdateClient(string ownerSlashRepo, Func<string?> tokenProvider, HttpMessageHandler? handler = null)
    {
        var parts = ownerSlashRepo.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            throw new ArgumentException("Repository must look like 'owner/name'.", nameof(ownerSlashRepo));
        (_owner, _repo) = (parts[0], parts[1]);
        _token = tokenProvider;
        // Redirects are followed by hand so the token never leaves api.github.com.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public string RepositoryUrl => $"https://github.com/{_owner}/{_repo}";

    /// <summary>The newest release that carries a Windows build, or null if there is none.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(bool includePrereleases, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage response;
        try
        {
            // A renamed or transferred repository answers with a redirect to its new API address.
            response = await GetAsync(new Uri($"https://api.github.com/repos/{_owner}/{_repo}/releases?per_page=30"),
                "application/vnd.github+json", HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new UpdateCheckException(UpdateFailure.Network, "Couldn't reach GitHub — check the internet connection.", ex);
        }

        using (response)
        {
            ThrowForStatus(response);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(json);
                return PickLatest(doc.RootElement, includePrereleases);
            }
            catch (JsonException ex)
            {
                throw new UpdateCheckException(UpdateFailure.BadResponse, "GitHub sent an unexpected reply.", ex);
            }
        }
    }

    private const int MaxRedirects = 5;

    private static bool IsGitHubApi(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort &&
        url.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);

    private HttpRequestMessage NewRequest(Uri url, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LaserPilot-Updater", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        // The token is for GitHub's API only — never for the storage server a download
        // redirects to, nor for any other address a reply might point at.
        if (IsGitHubApi(url))
        {
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var token = _token()?.Trim();
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }

    /// <summary>GET that follows redirects by hand, deciding afresh at every hop whether the token may go along.</summary>
    private async Task<HttpResponseMessage> GetAsync(Uri url, string accept, HttpCompletionOption completion, CancellationToken ct)
    {
        for (int hops = 0; ; hops++)
        {
            if (url.Scheme != Uri.UriSchemeHttps)
                throw new UpdateCheckException(UpdateFailure.BadResponse, "GitHub pointed to an insecure (non-HTTPS) address.");
            HttpResponseMessage response;
            using (var request = NewRequest(url, accept))
                response = await _http.SendAsync(request, completion, ct).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode)) return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new UpdateCheckException(UpdateFailure.BadResponse, "GitHub sent a redirect without a location.");
            if (hops >= MaxRedirects) throw new UpdateCheckException(UpdateFailure.BadResponse, "GitHub redirected too many times.");
            url = location.IsAbsoluteUri ? location : new Uri(url, location);
        }
    }

    private void ThrowForStatus(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        bool hasToken = !string.IsNullOrWhiteSpace(_token());
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new UpdateCheckException(UpdateFailure.Unauthorized,
                    "GitHub rejected the update token — it may have expired or been deleted. Enter a new one in Help → Update settings.");
            case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                when response.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0":
                throw new UpdateCheckException(UpdateFailure.RateLimited, "GitHub's hourly request limit was reached. LaserPilot will try again later.");
            case HttpStatusCode.Forbidden:
                throw new UpdateCheckException(UpdateFailure.Unauthorized,
                    "The update token doesn't have permission to read this repository. It needs read-only access to 'Contents'.");
            case HttpStatusCode.NotFound:
                throw new UpdateCheckException(UpdateFailure.NotFound, hasToken
                    ? "The repository wasn't found with this token. Check that the token can access " + _owner + "/" + _repo + "."
                    : "The repository is private, so LaserPilot needs an access token to check for updates. Add one in Help → Update settings.");
            default:
                throw new UpdateCheckException(UpdateFailure.BadResponse, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    /// <summary>Highest-versioned, non-draft release with the Windows zip attached.</summary>
    public static ReleaseInfo? PickLatest(JsonElement releases, bool includePrereleases)
    {
        if (releases.ValueKind != JsonValueKind.Array) return null;
        ReleaseInfo? best = null;
        foreach (var r in releases.EnumerateArray())
        {
            if (Bool(r, "draft")) continue;
            bool pre = Bool(r, "prerelease");
            if (pre && !includePrereleases) continue;
            var tag = Str(r, "tag_name");
            if (!TryParseVersion(tag, out var version)) continue;
            if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            ReleaseAsset? asset = null;
            foreach (var a in assets.EnumerateArray())
            {
                var name = Str(a, "name");
                if (!name.Equals(AssetName, StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith("win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
                asset = new ReleaseAsset(
                    a.TryGetProperty("id", out var id) && id.TryGetInt64(out var idv) ? idv : 0,
                    name,
                    a.TryGetProperty("size", out var size) && size.TryGetInt64(out var sz) ? sz : 0,
                    Str(a, "url"));
                if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) break;
            }
            if (asset is null || asset.ApiUrl.Length == 0) continue;
            if (best is not null && version <= best.Version) continue;
            DateTimeOffset? published = DateTimeOffset.TryParse(Str(r, "published_at"), out var p) ? p : null;
            var title = Str(r, "name");
            best = new ReleaseInfo(version, tag, title.Length > 0 ? title : tag, Str(r, "body"), published, Str(r, "html_url"), asset, pre);
        }
        return best;

        static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    /// <summary>Accepts "v0.2.15", "0.2.15", "release-0.2.15" or "0.2.15-beta".</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        int start = 0;
        while (start < tag.Length && !char.IsDigit(tag[start])) start++;
        int end = start;
        while (end < tag.Length && (char.IsDigit(tag[end]) || tag[end] == '.')) end++;
        var core = tag[start..end].TrimEnd('.');
        if (core.Length == 0) return false;
        if (!core.Contains('.')) core += ".0";
        if (!Version.TryParse(core, out var v)) return false;
        version = v;
        return true;
    }

    /// <summary>Normalises versions so 0.2.15 and 0.2.15.0 compare equal.</summary>
    public static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    /// <summary>Downloads a release asset to <paramref name="destinationPath"/>, reporting progress 0…1.</summary>
    public async Task DownloadAsync(ReleaseAsset asset, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(asset.ApiUrl, UriKind.Absolute, out var url))
            throw new UpdateCheckException(UpdateFailure.BadResponse, "The release's download address is invalid.");
        HttpResponseMessage response;
        try
        {
            // GitHub answers with a redirect to a short-lived signed storage URL,
            // which is fetched without the token.
            response = await GetAsync(url, "application/octet-stream", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException(UpdateFailure.Network, "The download failed — check the internet connection.", ex);
        }

        using (response)
        {
            ThrowForStatus(response);
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
            }
            if (total > 0 && done != total)
                throw new UpdateCheckException(UpdateFailure.Network, "The download was incomplete — please try again.");
        }
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public void Dispose() => _http.Dispose();
}
