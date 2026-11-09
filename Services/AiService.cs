using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace ArtMockups;

/// <summary>Optional Claude API helpers. The key is read from the environment and never leaves the backend.</summary>
public sealed class AiService(ConfigStore config, HttpClient http)
{
    const string Endpoint = "https://api.anthropic.com/v1/messages";

    static string? Key => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } k ? k : null;
    public bool Enabled => Key != null;

    void RequireKey()
    {
        if (!Enabled) throw new ArgumentException("AI features are disabled: set ANTHROPIC_API_KEY in the .env file and restart.");
    }

    async Task<string> AskAsync(Mat image, string prompt, int maxTokens, CancellationToken ct)
    {
        RequireKey();
        using var small = ImageIO.ResizeToMaxSide(image, 1024);
        var b64 = Convert.ToBase64String(ImageIO.Encode(small, ".jpg", 85));
        var body = JsonSerializer.Serialize(new
        {
            model = config.Current.AiModel,
            max_tokens = maxTokens,
            messages = new object[]
            {
                new { role = "user", content = new object[]
                {
                    new { type = "image", source = new { type = "base64", media_type = "image/jpeg", data = b64 } },
                    new { type = "text", text = prompt },
                } },
            },
        });

        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", Key);
            req.Headers.Add("anthropic-version", "2023-06-01");

            HttpResponseMessage resp;
            try { resp = await http.SendAsync(req, ct); }
            catch (HttpRequestException ex) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                _ = ex;
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException($"Could not reach the Claude API: {ex.Message}");
            }

            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(text);
                    var sb = new StringBuilder();
                    foreach (var part in doc.RootElement.GetProperty("content").EnumerateArray())
                        if (part.TryGetProperty("text", out var t)) sb.Append(t.GetString());
                    return sb.ToString();
                }

                bool retryable = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
                if (retryable && attempt < 4)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, ct);
                    continue;
                }
                var detail = ExtractError(text);
                throw new InvalidOperationException(resp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Claude API rejected the API key (401). Check ANTHROPIC_API_KEY.",
                    HttpStatusCode.TooManyRequests => "Claude API rate limit reached. Try again in a minute.",
                    _ => $"Claude API error {(int)resp.StatusCode}: {detail}",
                });
            }
        }
    }

    static string ExtractError(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString() ?? body; }
        catch { return body.Length > 200 ? body[..200] : body; }
    }

    static JsonElement ParseJsonObject(string text)
    {
        int a = text.IndexOf('{'), b = text.LastIndexOf('}');
        if (a < 0 || b <= a) throw new InvalidDataException("The model did not return JSON.");
        return JsonDocument.Parse(text[a..(b + 1)]).RootElement.Clone();
    }

    public async Task<List<PointD>> DetectCornersAsync(Mat scene, CancellationToken ct)
    {
        double scale = Math.Min(1.0, 1024.0 / Math.Max(scene.Width, scene.Height));
        int sw = (int)Math.Round(scene.Width * scale), sh = (int)Math.Round(scene.Height * scale);
        var prompt =
            $"This photo is {sw}x{sh} pixels. It contains a framed artwork whose picture area is blank (empty). " +
            "Find the 4 corners of the blank picture area inside the frame (the visible blank paper/canvas, not the outer frame). " +
            "Reply with ONLY a JSON object: {\"corners\": [[x,y],[x,y],[x,y],[x,y]]} using pixel coordinates in this image, " +
            "in the order top-left, top-right, bottom-right, bottom-left.";
        var json = ParseJsonObject(await AskAsync(scene, prompt, 300, ct));
        var pts = new List<PointD>();
        foreach (var p in json.GetProperty("corners").EnumerateArray())
            pts.Add(new PointD(p[0].GetDouble() / scale, p[1].GetDouble() / scale));
        // Clamp tiny overshoots, then validate bounds/shape; throws a clear error otherwise.
        pts = pts.Select(p => new PointD(Math.Clamp(p.X, 0, scene.Width), Math.Clamp(p.Y, 0, scene.Height))).ToList();
        PreparedScene.ValidateCorners(pts, scene.Width, scene.Height);
        return pts;
    }

    public record Caption(string File, string Title, string Text);

    public async Task<Caption> CaptionAsync(string file, Mat artwork, CancellationToken ct)
    {
        var prompt = config.Current.CaptionPrompt +
            $"\n\nArtwork file name: {file}\nReply with ONLY a JSON object: {{\"title\": \"...\", \"caption\": \"...\"}}";
        var json = ParseJsonObject(await AskAsync(artwork, prompt, 600, ct));
        return new Caption(file, json.GetProperty("title").GetString() ?? "", json.GetProperty("caption").GetString() ?? "");
    }

    public static string ToCsv(IEnumerable<Caption> rows)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("﻿filename,title,caption\r\n");
        foreach (var r in rows) sb.Append(Q(r.File)).Append(',').Append(Q(r.Title)).Append(',').Append(Q(r.Text)).Append("\r\n");
        return sb.ToString();
    }
}
