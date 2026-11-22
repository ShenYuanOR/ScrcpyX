using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace ScrcpyX;

public enum UpdateKind
{
    Newer,
    Current,
    Unavailable
}

public sealed record UpdateResult(UpdateKind Kind, string? Version, string? PageUrl);

public static class ReleaseCheck
{
    public const string AuthorUrl = "https://github.com/ShenYuanOL";
    public const string RepoUrl = "https://github.com/ShenYuanOR/ScrcpyX";
    public const string LicenseUrl = "https://github.com/ShenYuanOR/ScrcpyX/blob/main/LICENSE";
    public const string IconCreditUrl = "https://icon-icons.com/zh/authors/1235-css-gg";
    public const string LatestApi = "https://api.github.com/repos/ShenYuanOR/ScrcpyX/releases/latest";

    public static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version is null)
                return "0.0.0";
            return version.Revision > 0
                ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
                : version.Build >= 0
                    ? $"{version.Major}.{version.Minor}.{version.Build}"
                    : $"{version.Major}.{version.Minor}";
        }
    }

    public static bool IsNewer(string? tag, string? current)
    {
        if (!TryParts(tag, out var latest) || !TryParts(current, out var ours))
            return false;
        var count = Math.Max(latest.Length, ours.Length);
        for (var i = 0; i < count; i++)
        {
            var left = i < latest.Length ? latest[i] : 0;
            var right = i < ours.Length ? ours[i] : 0;
            if (left != right)
                return left > right;
        }
        return false;
    }

    public static async Task<UpdateResult> QueryAsync(string current, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            request.Headers.TryAddWithoutValidation("User-Agent", "ScrcpyX/" + current);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateResult(UpdateKind.Unavailable, null, null);
            if (!response.IsSuccessStatusCode)
                return new UpdateResult(UpdateKind.Unavailable, null, null);
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
            var page = doc.RootElement.TryGetProperty("html_url", out var pageValue) ? pageValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag))
                return new UpdateResult(UpdateKind.Unavailable, null, null);
            var kind = IsNewer(tag, current) ? UpdateKind.Newer : UpdateKind.Current;
            return new UpdateResult(kind, DisplayVersion(tag), string.IsNullOrWhiteSpace(page) ? null : page);
        }
        catch (Exception)
        {
            return new UpdateResult(UpdateKind.Unavailable, null, null);
        }
    }

    public static string DisplayVersion(string tag)
    {
        var text = tag.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            text = text[1..];
        return text;
    }

    private static bool TryParts(string? text, out int[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            text = text[1..];
        var bits = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (bits.Length == 0)
            return false;
        var numbers = new int[bits.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            if (!int.TryParse(bits[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] < 0)
                return false;
        }
        parts = numbers;
        return true;
    }
}
