using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DreamLauncher.Core.Downloads;
using DreamLauncher.Models.Config;
using DreamLauncher.Models.Modpack;

namespace DreamLauncher.Core.Modpack;

public sealed class CurseForgeApiClient
{
    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly DownloadSource _source;

    public CurseForgeApiClient(
        HttpClient? httpClient = null,
        string? apiKey = null,
        DownloadSource source = DownloadSource.Bmclapi)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DreamLauncher/0.1");
        _apiKey = apiKey;
        _source = source;
    }

    public async Task<IReadOnlyList<ModpackFileEntry>> ResolveFilesAsync(
        IReadOnlyList<(int ProjectId, int FileId)> curseForgeFiles,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ModpackFileEntry>();
        var batches = curseForgeFiles
            .Select((f, i) => (f, i))
            .GroupBy(x => x.i / 50)
            .Select(g => g.Select(x => x.f).ToList());

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = await ResolveBatchAsync(batch, cancellationToken);
            results.AddRange(resolved);
        }

        return results;
    }

    private async Task<IReadOnlyList<ModpackFileEntry>> ResolveBatchAsync(
        List<(int ProjectId, int FileId)> batch,
        CancellationToken cancellationToken)
    {
        var fileIds = batch.Select(f => f.FileId).ToArray();
        var requestBody = new StringContent(
            $"{{\"fileIds\": [{string.Join(",", fileIds)}]}}",
            Encoding.UTF8,
            "application/json");

        JsonObject? response = null;

        var primaryBase = DownloadSourceUrls.GetCurseForgeApiBase(_source);
        var primaryUseApiKey = _source == DownloadSource.Official;

        // Try selected source first
        try
        {
            response = await PostCurseForgeAsync(
                $"{primaryBase}/v1/mods/files",
                requestBody,
                useApiKey: primaryUseApiKey,
                cancellationToken);
        }
        catch
        {
            // Primary failed, try fallback
        }

        // Fallback to the other source
        if (response is null)
        {
            var fallbackBase = _source == DownloadSource.Official
                ? DownloadSourceUrls.GetCurseForgeApiBase(DownloadSource.Bmclapi)
                : DownloadSourceUrls.GetCurseForgeApiBase(DownloadSource.Official);
            var fallbackUseApiKey = _source != DownloadSource.Official;

            try
            {
                response = await PostCurseForgeAsync(
                    $"{fallbackBase}/v1/mods/files",
                    requestBody,
                    useApiKey: fallbackUseApiKey,
                    cancellationToken);
            }
            catch
            {
                // Both failed
            }
        }

        if (response is null)
        {
            throw new IOException("无法连接到 CurseForge API，请检查网络连接。");
        }

        var results = new List<ModpackFileEntry>();
        var dataArray = response["data"]?.AsArray() ?? [];

        foreach (var fileJson in dataArray)
        {
            if (fileJson is not JsonObject fileObj)
            {
                continue;
            }

            var fileName = fileObj["fileName"]?.ToString() ?? "";
            var downloadUrl = fileObj["downloadUrl"]?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            var targetFolder = DetectTargetFolder(fileObj);
            var relativePath = $"{targetFolder}/{fileName}";

            var urls = BuildDownloadUrls(downloadUrl);

            results.Add(new ModpackFileEntry
            {
                RelativePath = relativePath.Replace('\\', '/'),
                DownloadUrls = urls,
                FileSize = fileObj["fileLength"]?.GetValue<long>() ?? 0,
                Sha1 = ""
            });
        }

        return results;
    }

    private async Task<JsonObject> PostCurseForgeAsync(
        string url,
        HttpContent content,
        bool useApiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = content;

        if (useApiKey && !string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Add("x-api-key", _apiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonNode.Parse(text)?.AsObject()
            ?? throw new InvalidDataException("CurseForge API 返回无效 JSON。");
    }

    private List<string> BuildDownloadUrls(string originalUrl)
    {
        var urls = new List<string>();

        // Fix overwolf URLs first
        var corrected = originalUrl
            .Replace("-service.overwolf.wtf", ".forgecdn.net")
            .Replace("://media.", "://edge.");

        // Primary: convert to selected source
        var primary = DownloadSourceUrls.ConvertCurseForgeUrl(_source, corrected);
        urls.Add(primary);

        // Fallback: the other source
        var fallbackSource = _source == DownloadSource.Official
            ? DownloadSource.Bmclapi
            : DownloadSource.Official;
        var fallback = DownloadSourceUrls.ConvertCurseForgeUrl(fallbackSource, corrected);
        if (!string.Equals(fallback, primary, StringComparison.OrdinalIgnoreCase))
        {
            urls.Add(fallback);
        }

        // Add original if different from both
        if (!string.Equals(originalUrl, primary, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(originalUrl, fallback, StringComparison.OrdinalIgnoreCase))
        {
            urls.Add(originalUrl);
        }

        return urls;
    }

    private static string DetectTargetFolder(JsonObject fileObj)
    {
        var modules = fileObj["modules"]?.AsArray();
        if (modules is null || modules.Count == 0)
        {
            return "mods";
        }

        var moduleNames = modules
            .Select(m => m?["name"]?.ToString() ?? "")
            .ToList();

        if (moduleNames.Any(n =>
            string.Equals(n, "META-INF", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "mcmod.info", StringComparison.OrdinalIgnoreCase)))
        {
            return "mods";
        }

        var fileName = fileObj["fileName"]?.ToString() ?? "";
        if (fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            return "mods";
        }

        if (moduleNames.Any(n =>
            string.Equals(n, "pack.mcmeta", StringComparison.OrdinalIgnoreCase)))
        {
            return "resourcepacks";
        }

        if (moduleNames.Any(n =>
            string.Equals(n, "level.dat", StringComparison.OrdinalIgnoreCase)))
        {
            return "saves";
        }

        return "shaderpacks";
    }
}
