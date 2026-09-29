using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MemoryOpsAI.Models;

namespace MemoryOpsAI.Services
{
    public interface IAgentService
    {
        // Given the current incident + recalled memories, ask Groq to reflect
        // and produce a recommendation. Returns (fix, reasoning).
        Task<(string fix, string reasoning)> ReflectAsync(IncidentRequest incident, List<string> similarIncidents);
    }

    /// <summary>
    /// Calls Groq's OpenAI-compatible chat completions endpoint
    /// (https://api.groq.com/openai/v1/chat/completions) and asks the model
    /// to reason over the current incident plus whatever Hindsight recalled.
    /// </summary>
    public class AgentService : IAgentService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;

        public AgentService(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _config = config;
        }

        public async Task<(string fix, string reasoning)> ReflectAsync(IncidentRequest incident, List<string> similarIncidents)
        {
            var apiKey = _config["Groq:ApiKey"];
            var baseUrl = _config["Groq:BaseUrl"];
            var model = _config["Groq:Model"];

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return ("Groq API key not configured.", "Set Groq:ApiKey in appsettings.json.");
            }

            var memoryContext = similarIncidents.Count > 0
                ? string.Join("\n", similarIncidents.Select((m, i) => $"{i + 1}. {m}"))
                : "No similar past incidents found in memory.";

            var systemPrompt =
                "You are an incident-response assistant for a hospital-software support team. " +
                "You get a NEW incident and notes recalled from the team's memory of past incidents. " +
                "Use only past incidents that are truly relevant (same module or same root cause) and prefer fixes confirmed as working; " +
                "if a past suggestion is noted as NOT working, avoid it. Ignore irrelevant or junk notes. " +
                "If nothing relevant exists, give general best-practice troubleshooting steps and say no past match was found. " +
                "Respond ONLY with JSON, no markdown: " +
                "{\"fix\": \"<3-5 short numbered steps as one string>\", \"reasoning\": \"<1-2 sentences; mention which past incident you relied on, if any>\"}";

            var userPrompt =
                $"New incident:\n" +
                $"Customer: {incident.Customer}\n" +
                $"Module: {incident.Module}\n" +
                $"Issue: {incident.Issue}\n" +
                $"Error: {incident.Error}\n\n" +
                $"Similar past incidents from memory:\n{memoryContext}";

            var requestBody = new
            {
                model = model,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.3
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = JsonContent.Create(requestBody);

            // Groq/open models occasionally hiccup on function-calling-style requests;
            // this is a plain chat completion, but we still guard against transient errors.
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request);
            }
            catch (Exception ex)
            {
                return ("Could not reach Groq.", $"Request error: {ex.Message}");
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[Groq] request failed ({(int)response.StatusCode}): {errorText}");
                return ("Groq request failed.", $"HTTP {(int)response.StatusCode}. See server console for details.");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            string? content = null;
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var contentElement))
            {
                content = contentElement.GetString();
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return ("No response from model.", "Groq returned an empty completion.");
            }

            // Try to parse the model's JSON reply. Fall back to raw text if it
            // didn't follow instructions perfectly (small/free models sometimes wrap
            // JSON in markdown fences or add stray text).
            var cleaned = content.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned.Trim('`').Replace("json\n", "").Replace("json\r\n", "");
            }

            try
            {
                using var parsed = JsonDocument.Parse(cleaned);
                var fix = parsed.RootElement.TryGetProperty("fix", out var fixEl) ? fixEl.GetString() ?? "" : "";
                var reasoning = parsed.RootElement.TryGetProperty("reasoning", out var reasonEl) ? reasonEl.GetString() ?? "" : "";
                return (fix, reasoning);
            }
            catch (JsonException)
            {
                // Model didn't return valid JSON — return the raw text as the fix.
                return (cleaned, "Model did not return structured JSON; showing raw response.");
            }
        }
    }
}
