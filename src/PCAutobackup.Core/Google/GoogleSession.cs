using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace PCAutobackup.Core.Google;

internal sealed record GoogleTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresUtc, string Scope);

internal sealed class GoogleSession(AppConfiguration config, Func<Task> saveConfiguration, HttpClient? httpClient = null)
{
    private readonly HttpClient http = httpClient ?? GoogleAccountService.Http;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private GoogleTokens? tokens;

    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct, bool retrySafe = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            var accessToken = await GetAccessTokenAsync(false, ct);
            using var request = requestFactory();
            request.Headers.Authorization = new("Bearer", accessToken);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (HttpRequestException) when (retrySafe && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt < 4)
            {
                response.Dispose();
                await GetAccessTokenAsync(true, ct);
                continue;
            }
            if (retrySafe && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) && attempt < 4)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                response.Dispose();
                await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, ct);
                continue;
            }
            return response;
        }
    }

    private async Task<string> GetAccessTokenAsync(bool force, CancellationToken ct)
    {
        await refreshLock.WaitAsync(ct);
        try
        {
            GoogleAccountService.ValidateClient(config);
            if (tokens is null)
            {
                if (string.IsNullOrWhiteSpace(config.ProtectedGoogleTokens))
                    throw new InvalidOperationException("ยังไม่ได้เชื่อมต่อ Google กรุณาเข้าสู่ระบบก่อน");
                try
                {
                    var plain = ProtectedData.Unprotect(Convert.FromBase64String(config.ProtectedGoogleTokens), null, DataProtectionScope.CurrentUser);
                    try { tokens = JsonSerializer.Deserialize<GoogleTokens>(plain) ?? throw new CryptographicException(); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
                { throw new InvalidOperationException("อ่านข้อมูลเข้าสู่ระบบ Google บน Windows บัญชีนี้ไม่ได้ กรุณาเชื่อมต่อใหม่", ex); }
                GoogleAccountService.ValidateGrantedScopes(tokens.Scope);
            }
            if (!force && tokens.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(2)) return tokens.AccessToken;
            using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = config.GoogleClientId, ["client_secret"] = config.GoogleClientSecret,
                ["refresh_token"] = tokens.RefreshToken, ["grant_type"] = "refresh_token"
            }), ct);
            await EnsureSuccessAsync(response, ct);
            tokens = await ReadTokenAsync(response, tokens, ct);
            GoogleAccountService.ValidateGrantedScopes(tokens.Scope);
            config.ProtectedGoogleTokens = Protect(tokens);
            await saveConfiguration();
            return tokens.AccessToken;
        }
        finally { refreshLock.Release(); }
    }

    internal static string Protect(GoogleTokens tokens)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(tokens);
        try { return Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal static async Task<GoogleTokens> ReadTokenAsync(HttpResponseMessage response, GoogleTokens? old, CancellationToken ct)
    {
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = json.RootElement;
        var access = root.GetProperty("access_token").GetString();
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : old?.RefreshToken;
        var scope = root.TryGetProperty("scope", out var s) ? s.GetString() : old?.Scope;
        var type = root.GetProperty("token_type").GetString();
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh) || string.IsNullOrWhiteSpace(scope) || !string.Equals(type, "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Google ไม่ได้คืน offline token ที่ใช้งานได้ กรุณาเชื่อมต่อใหม่และอนุญาตสิทธิ์");
        return new(access, refresh, DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()), scope);
    }

    internal static Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "สิทธิ์ Google หมดอายุ กรุณาเชื่อมต่อบัญชีใหม่",
            HttpStatusCode.Forbidden => "Google ปฏิเสธสิทธิ์ กรุณาตรวจสอบ Drive API สิทธิ์บัญชี และพื้นที่ว่าง",
            HttpStatusCode.NotFound => "ไม่พบข้อมูล Google Drive ที่เลือก กรุณาตรวจสอบบัญชีและคลังสำรอง",
            _ => $"Google API ทำรายการไม่สำเร็จ (HTTP {(int)response.StatusCode})"
        };
        // Never include response bodies, tokens, authorization codes or credential values in errors/logs.
        throw new HttpRequestException(message, null, response.StatusCode);
    }
}

internal sealed record DriveFile(string Id, string Name, long Size, DateTimeOffset ModifiedUtc, Dictionary<string, string> Properties, string[] Parents, string MimeType);

internal static class DriveApi
{
    internal const string Fields = "id,name,size,modifiedTime,appProperties,parents,mimeType";
    internal static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
    internal static async Task<List<DriveFile>> ListFilesAsync(GoogleSession session, string query, CancellationToken ct)
    {
        var result = new List<DriveFile>();
        await foreach (var file in EnumerateFilesAsync(session, query, ct)) result.Add(file);
        return result;
    }

    internal static async IAsyncEnumerable<DriveFile> EnumerateFilesAsync(GoogleSession session, string query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? page = null;
        do
        {
            var url = "https://www.googleapis.com/drive/v3/files?pageSize=1000&fields=" + Uri.EscapeDataString($"nextPageToken,files({Fields})") + "&q=" + Uri.EscapeDataString(query);
            if (page is not null) url += "&pageToken=" + Uri.EscapeDataString(page);
            using var response = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
            await GoogleSession.EnsureSuccessAsync(response, ct);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            foreach (var item in json.RootElement.GetProperty("files").EnumerateArray()) yield return Parse(item);
            page = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (page is not null);
    }

    internal static DriveFile Parse(JsonElement file) => new(file.GetProperty("id").GetString()!, file.GetProperty("name").GetString()!,
        file.TryGetProperty("size", out var size) ? long.Parse(size.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : 0,
        file.TryGetProperty("modifiedTime", out var modified) ? modified.GetDateTimeOffset() : DateTimeOffset.MinValue,
        file.TryGetProperty("appProperties", out var props) ? props.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!) : [],
        file.TryGetProperty("parents", out var parents) ? parents.EnumerateArray().Select(x => x.GetString()!).ToArray() : [],
        file.GetProperty("mimeType").GetString()!);

    internal static async Task<DriveFile> GetFileAsync(GoogleSession session, string id, CancellationToken ct)
    {
        using var response = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(id) + "?fields=" + Uri.EscapeDataString(Fields)), ct);
        await GoogleSession.EnsureSuccessAsync(response, ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return Parse(json.RootElement);
    }

    internal static async Task<DriveFile> CreateFolderAsync(GoogleSession session, string name, string? parent, Dictionary<string, string> properties, CancellationToken ct)
    {
        var metadata = new Dictionary<string, object> { ["name"] = name, ["mimeType"] = "application/vnd.google-apps.folder", ["appProperties"] = properties };
        if (parent is not null) metadata["parents"] = new[] { parent };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
        // Folder creation is not retried automatically: an uncertain response must not create duplicates.
        using var response = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=" + Uri.EscapeDataString(Fields)) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/json") } } }, ct, retrySafe: false);
        await GoogleSession.EnsureSuccessAsync(response, ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return Parse(json.RootElement);
    }
}
