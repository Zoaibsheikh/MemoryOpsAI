using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MemoryOpsAI.Services
{
    public interface IHindsightService
    {
        // Store a new incident (or its resolution) as a memory.
        Task RetainAsync(string content, Dictionary<string, string>? metadata = null);

        // Store many memories in one call (used for demo data).
        Task RetainManyAsync(IEnumerable<string> contents);

        // Pull back similar past incidents given the current issue text.
        Task<List<string>> RecallAsync(string query, int topK = 5);
    }

    /// <summary>
    /// Talks to Hindsight Cloud's REST API directly (no SDK needed).
    /// Endpoints per https://hindsight.vectorize.io/api-reference:
    ///   POST /v1/default/banks/{bank_id}/memories          -> retain
    ///   POST /v1/default/banks/{bank_id}/memories/recall    -> recall
    /// </summary>
    public class HindsightService : IHindsightService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;

        public HindsightService(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _config = config;
        }

        private string BaseUrl => _config["Hindsight:BaseUrl"]!.TrimEnd('/');
        private string BankId => _config["Hindsight:MemoryBankId"]!;
        private string ApiKey => _config["Hindsight:ApiKey"]!;

        private HttpRequestMessage BuildRequest(HttpMethod method, string path, object? body = null)
        {
            var request = new HttpRequestMessage(method, $"{BaseUrl}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            if (body != null)
            {
                request.Content = JsonContent.Create(body);
            }
            return request;
        }

        public async Task RetainAsync(string content, Dictionary<string, string>? metadata = null)
        {
            var path = $"/v1/default/banks/{BankId}/memories";

            var body = new
            {
                items = new[]
                {
                    new { content = content }
                },
                async = false
            };

            using var request = BuildRequest(HttpMethod.Post, path, body);
            var response = await _httpClient.SendAsync(request);

            // Don't let a Hindsight hiccup break the incident-logging flow;
            // just surface it so it shows up in the console during the hackathon.
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[Hindsight] retain failed ({(int)response.StatusCode}): {errorText}");
            }
        }

        public async Task RetainManyAsync(IEnumerable<string> contents)
        {
            var body = new
            {
                items = contents.Select(c => new { content = c }).ToArray(),
                async = true
            };
            using var request = BuildRequest(HttpMethod.Post, $"/v1/default/banks/{BankId}/memories", body);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                Console.WriteLine($"[Hindsight] batch retain failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        }

        public async Task<List<string>> RecallAsync(string query, int topK = 5)
        {
            var path = $"/v1/default/banks/{BankId}/memories/recall";

            var body = new
            {
                query = query,
                max_tokens = 2048
            };

            using var request = BuildRequest(HttpMethod.Post, path, body);
            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[Hindsight] recall failed ({(int)response.StatusCode}): {errorText}");
                return new List<string>();
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var results = new List<string>();
            if (doc.RootElement.TryGetProperty("results", out var resultsElement))
            {
                foreach (var item in resultsElement.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var textElement))
                    {
                        var text = textElement.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            results.Add(text);
                        }
                    }
                    if (results.Count >= topK * 2) break;
                }
            }

            return results.Distinct().Take(topK).ToList();
        }
    }
}
