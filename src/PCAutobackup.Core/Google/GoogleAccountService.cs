using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCAutobackup.Core.Google;

public sealed record GoogleRepository(string Id, string DeviceId, string DeviceName);

/// <summary>Desktop OAuth: browser + loopback + PKCE. No user password enters this application.</summary>
public sealed class GoogleAccountService
{
    internal const string DriveScope = "https://www.googleapis.com/auth/drive.file";
    // Redirects are handled as failures: a provider response must never redirect bearer tokens or token POST bodies.
    internal static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };

    public async Task<AppConfiguration> SignInAsync(AppConfiguration config, string? emailHint = null, CancellationToken ct = default)
    {
        ValidateClient(config);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = $"http://127.0.0.1:{port}/oauth2callback";
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = config.GoogleClientId, ["redirect_uri"] = redirect,
                ["response_type"] = "code", ["scope"] = $"openid email {DriveScope}",
                ["access_type"] = "offline", ["prompt"] = "consent select_account",
                ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
            };
            if (!string.IsNullOrWhiteSpace(emailHint)) parameters["login_hint"] = emailHint.Trim();
            var query = await new FormUrlEncodedContent(parameters).ReadAsStringAsync(timeout.Token);
            Process.Start(new ProcessStartInfo("https://accounts.google.com/o/oauth2/v2/auth?" + query) { UseShellExecute = true });
            var code = await ReadCallbackAsync(listener, port, state, timeout.Token);
            using var response = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = config.GoogleClientId, ["client_secret"] = config.GoogleClientSecret,
                ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code"
            }), timeout.Token);
            await GoogleSession.EnsureSuccessAsync(response, timeout.Token);
            var token = await GoogleSession.ReadTokenAsync(response, null, timeout.Token);
            ValidateGrantedScopes(token.Scope);
            using var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
            userRequest.Headers.Authorization = new("Bearer", token.AccessToken);
            using var userResponse = await Http.SendAsync(userRequest, timeout.Token);
            await GoogleSession.EnsureSuccessAsync(userResponse, timeout.Token);
            using var identity = await JsonDocument.ParseAsync(await userResponse.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var subject = identity.RootElement.GetProperty("sub").GetString();
            var email = identity.RootElement.GetProperty("email").GetString();
            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email) ||
                !identity.RootElement.TryGetProperty("email_verified", out var verified) || !verified.GetBoolean())
                throw new InvalidOperationException("Google ไม่ได้คืนบัญชีอีเมลที่ยืนยันแล้ว กรุณาลองเชื่อมต่อใหม่");
            // A real Drive request verifies that authorization is usable before displaying a connected account.
            using var driveRequest = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files?pageSize=1&fields=files(id)");
            driveRequest.Headers.Authorization = new("Bearer", token.AccessToken);
            using var driveResponse = await Http.SendAsync(driveRequest, timeout.Token);
            await GoogleSession.EnsureSuccessAsync(driveResponse, timeout.Token);
            var protectedTokens = GoogleSession.Protect(token);
            if (!string.Equals(config.GoogleSubject, subject, StringComparison.Ordinal)) config.GoogleRootFolderId = null;
            config.GoogleSubject = subject;
            config.GoogleEmail = email;
            config.ProtectedGoogleTokens = protectedTokens;
            return config;
        }
        finally { listener.Stop(); }
    }

    public Task DisconnectAsync(AppConfiguration config, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        config.GoogleEmail = null;
        config.GoogleSubject = null;
        config.ProtectedGoogleTokens = null;
        config.GoogleRootFolderId = null;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<GoogleRepository>> ListRepositoriesAsync(AppConfiguration config, CancellationToken ct = default)
    {
        var session = new GoogleSession(config, () => Task.CompletedTask);
        var roots = await DriveApi.ListFilesAsync(session, "trashed=false and mimeType='application/vnd.google-apps.folder' and appProperties has { key='pcabRole' and value='root-v1' }", ct);
        var result = new List<GoogleRepository>();
        foreach (var root in roots)
        {
            var children = await DriveApi.ListFilesAsync(session, $"'{DriveApi.Escape(root.Id)}' in parents and trashed=false and mimeType='application/vnd.google-apps.folder' and appProperties has {{ key='pcabRole' and value='repository-v1' }}", ct);
            result.AddRange(children.Select(x => new GoogleRepository(x.Id, x.Properties.GetValueOrDefault("deviceId", ""), x.Properties.GetValueOrDefault("deviceName", x.Name))));
        }
        return result;
    }

    public static void ValidateGrantedScopes(string scopes)
    {
        var granted = scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (!granted.Contains(DriveScope) || !granted.Contains("openid") ||
            !(granted.Contains("email") || granted.Contains("https://www.googleapis.com/auth/userinfo.email")))
            throw new InvalidOperationException("ยังไม่ได้อนุญาตสิทธิ์ Google Drive และข้อมูลบัญชีที่จำเป็น กรุณาเชื่อมต่อใหม่และอนุญาตสิทธิ์");
    }

    public static bool IsValidState(string expected, string? actual) => actual is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    internal static void ValidateClient(AppConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.GoogleClientId) || string.IsNullOrWhiteSpace(config.GoogleClientSecret))
            throw new InvalidOperationException("ยังไม่ได้ตั้งค่า Google OAuth Desktop client กรุณานำเข้าไฟล์ credentials ของ Desktop app ก่อน");
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string> ReadCallbackAsync(TcpListener listener, int port, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var header = new List<byte>();
            var one = new byte[1];
            while (header.Count < 16384 && await stream.ReadAsync(one, requestTimeout.Token) != 0)
            {
                header.Add(one[0]);
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
            }
            var line = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", 2)[0].Split(' ');
            if (line.Length != 3 || line[0] != "GET" || !line[1].StartsWith("/oauth2callback?", StringComparison.Ordinal))
            {
                await ReplyAsync(stream, false, ct);
                continue;
            }
            var uri = new Uri($"http://127.0.0.1:{port}" + line[1]);
            var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=', 2)).ToArray();
            if (pairs.GroupBy(x => x[0], StringComparer.Ordinal).Any(x => x.Count() > 1))
                throw new InvalidOperationException("คำตอบ OAuth ไม่ถูกต้อง");
            var query = pairs.ToDictionary(x => Uri.UnescapeDataString(x[0]), x => x.Length == 2 ? Uri.UnescapeDataString(x[1].Replace('+', ' ')) : "");
            var valid = IsValidState(state, query.GetValueOrDefault("state"));
            var ok = valid && !query.ContainsKey("error") && !string.IsNullOrWhiteSpace(query.GetValueOrDefault("code"));
            await ReplyAsync(stream, ok, ct);
            if (!valid) throw new InvalidOperationException("OAuth state ไม่ตรงกัน การเชื่อมต่อถูกยกเลิก");
            if (!ok) throw new InvalidOperationException("การอนุญาต Google ถูกยกเลิกหรือไม่ได้รับรหัสยืนยัน");
            return query["code"];
        }
    }

    private static async Task ReplyAsync(Stream stream, bool ok, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(ok ? "เชื่อมต่อ Google สำเร็จ กลับไปที่ PC AutoBackup ได้แล้ว" : "เชื่อมต่อไม่สำเร็จ กลับไปที่ PC AutoBackup เพื่อลองอีกครั้ง");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(ok ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
    }
}
