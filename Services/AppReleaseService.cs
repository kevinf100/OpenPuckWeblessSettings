using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace OpenPuckWeblessSettings.Services;

public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private readonly string[] _prerelease;

    public SemanticVersion(int major, int minor, int patch, IEnumerable<string>? prerelease = null)
    {
        if (major < 0 || minor < 0 || patch < 0) throw new ArgumentOutOfRangeException(nameof(major));
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease?.ToArray() ?? [];
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease => _prerelease;
    public bool IsPrerelease => _prerelease.Length > 0;

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = null!;
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var withoutBuild = text.Split('+', 2)[0];
        var parts = withoutBuild.Split('-', 2);
        var core = parts[0].Split('.');
        if (core.Length != 3 || !ParseNumber(core[0], out var major) || !ParseNumber(core[1], out var minor) || !ParseNumber(core[2], out var patch)) return false;
        var prerelease = parts.Length == 1 ? [] : parts[1].Split('.');
        if (prerelease.Any(identifier => string.IsNullOrWhiteSpace(identifier) || identifier.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))) return false;
        if (prerelease.Any(identifier => identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit))) return false;
        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var version) ? version : throw new FormatException($"'{value}' is not a valid semantic version.");

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var core = Major.CompareTo(other.Major);
        if (core == 0) core = Minor.CompareTo(other.Minor);
        if (core == 0) core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;
        if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        for (var index = 0; index < Math.Max(_prerelease.Length, other._prerelease.Length); index++)
        {
            if (index == _prerelease.Length) return -1;
            if (index == other._prerelease.Length) return 1;
            var leftNumeric = int.TryParse(_prerelease[index], out var left);
            var rightNumeric = int.TryParse(other._prerelease[index], out var right);
            var compared = leftNumeric && rightNumeric
                ? left.CompareTo(right)
                : leftNumeric != rightNumeric
                    ? leftNumeric ? -1 : 1
                    : StringComparer.Ordinal.Compare(_prerelease[index], other._prerelease[index]);
            if (compared != 0) return compared;
        }
        return 0;
    }

    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major); hash.Add(Minor); hash.Add(Patch);
        foreach (var identifier in _prerelease) hash.Add(identifier, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (IsPrerelease ? "-" + string.Join('.', _prerelease) : "");
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    private static bool ParseNumber(string text, out int value) =>
        int.TryParse(text, out value) && value >= 0 && (text == "0" || !text.StartsWith('0'));
}

public sealed record AppRelease(
    SemanticVersion Version,
    bool IsPrerelease,
    string Name,
    string Notes,
    DateTimeOffset PublishedAt,
    Uri ReleaseUri);

public sealed record AppUpdateCheckResult(SemanticVersion CurrentVersion, AppRelease? Latest)
{
    public bool IsUpdateAvailable => Latest is not null && Latest.Version > CurrentVersion;
}

public interface IAppReleaseService
{
    Task<IReadOnlyList<AppRelease>> GetReleasesAsync(CancellationToken cancellationToken);
    Task<AppUpdateCheckResult> CheckAsync(SemanticVersion currentVersion, bool includePrereleases, CancellationToken cancellationToken);
}

public sealed class GitHubAppReleaseService(HttpClient? httpClient = null) : IAppReleaseService, IDisposable
{
    public static readonly Uri RepositoryUri = new("https://github.com/kevinf100/OpenPuckWeblessSettings");
    public static readonly Uri ReleasesUri = new("https://github.com/kevinf100/OpenPuckWeblessSettings/releases");
    public static readonly Uri UpstreamRepositoryUri = new("https://github.com/safijari/openpuck");
    private static readonly Uri ApiUri = new("https://api.github.com/repos/kevinf100/OpenPuckWeblessSettings/releases");
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    private readonly bool _ownsClient = httpClient is null;

    public async Task<IReadOnlyList<AppRelease>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("OpenPuckNative", "0.1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is 403 or 429) throw new HttpRequestException("GitHub update checks are currently rate-limited.", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var releases = new List<AppRelease>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            if (!item.TryGetProperty("tag_name", out var tag) || !SemanticVersion.TryParse(tag.GetString(), out var version)) continue;
            if (!item.TryGetProperty("html_url", out var url) || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var releaseUri) || !IsTrustedReleaseUri(releaseUri)) continue;
            if (!item.TryGetProperty("published_at", out var published) || !published.TryGetDateTimeOffset(out var publishedAt)) continue;
            var isPrerelease = version.IsPrerelease || item.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean();
            releases.Add(new AppRelease(
                version,
                isPrerelease,
                item.TryGetProperty("name", out var name) ? name.GetString() ?? version.ToString() : version.ToString(),
                item.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                publishedAt,
                releaseUri));
        }
        return releases;
    }

    public async Task<AppUpdateCheckResult> CheckAsync(SemanticVersion currentVersion, bool includePrereleases, CancellationToken cancellationToken)
    {
        var releases = await GetReleasesAsync(cancellationToken);
        var latest = releases
            .Where(release => includePrereleases || !release.IsPrerelease)
            .OrderByDescending(release => release.Version)
            .FirstOrDefault();
        return new AppUpdateCheckResult(currentVersion, latest);
    }

    public static bool IsTrustedReleaseUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/kevinf100/OpenPuckWeblessSettings/", StringComparison.OrdinalIgnoreCase);

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}

public interface IApplicationVersionProvider
{
    SemanticVersion Version { get; }
    string DisplayVersion { get; }
    bool IsPrerelease { get; }
}

public sealed class AssemblyApplicationVersionProvider : IApplicationVersionProvider
{
    public AssemblyApplicationVersionProvider(Assembly? assembly = null)
    {
        assembly ??= typeof(AssemblyApplicationVersionProvider).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var normalized = Normalize(informational ?? assembly.GetName().Version?.ToString(3) ?? "0.1.0");
        Version = SemanticVersion.TryParse(normalized, out var parsed) ? parsed : SemanticVersion.Parse("0.1.0");
        DisplayVersion = Version.ToString();
    }

    public SemanticVersion Version { get; }
    public string DisplayVersion { get; }
    public bool IsPrerelease => Version.IsPrerelease;
    public static string Normalize(string value) => value.Trim().TrimStart('v', 'V').Split('+', 2)[0];
}

public interface IExternalUriLauncher
{
    void Open(Uri uri);
}

public sealed class SystemExternalUriLauncher : IExternalUriLauncher
{
    public void Open(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
}
