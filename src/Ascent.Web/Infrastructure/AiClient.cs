using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ascent.Web.Planning;
using Microsoft.Extensions.Options;

namespace Ascent.Web.Infrastructure;

public sealed class AiOptions
{
    public string Provider { get; set; } = "KoboldCpp";
    public string BaseUrl { get; set; } = "http://localhost:5001/v1/";
    public string Model { get; set; } = "koboldcpp";
    public string ApiKey { get; set; } = "";
    public int MaxOutputTokens { get; set; } = 6144;
    public int MaxContextTokens { get; set; } = 12288;
    public int TimeoutSeconds { get; set; } = 180;
    public bool? JsonMode { get; set; }
    public string? TokenLimitParameter { get; set; }
    public double? Temperature { get; set; }
    public string? PromptSuffix { get; set; }
    public string KoboldPromptFormat { get; set; } = "llama3";
    public bool StripThinkingTags { get; set; }
}

public interface IPlanningAi
{
    Task<T> GenerateAsync<T>(string system, string context, Action<T> validate, CancellationToken ct, int? maxOutputTokens = null, IReadOnlyDictionary<string, List<string>>? references = null);
}

public sealed partial class CompatiblePlanningAi(HttpClient http, IOptions<AiOptions> options) : IPlanningAi, IStreamingPlanningAi
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly JsonSerializerOptions AiJson = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
          Converters = { new PrintedDateConverter(), new PrintedDeadlineConverter() } };
    public async Task<T> GenerateAsync<T>(string system, string context, Action<T> validate, CancellationToken ct, int? maxOutputTokens = null, IReadOnlyDictionary<string, List<string>>? references = null)
    {
        var settings = options.Value;
        if (settings.Provider is not ("KoboldCpp" or "OpenAI")) throw new PlanningException("Choose KoboldCpp or OpenAI in server configuration.");
        if (!Uri.TryCreate(settings.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseUri.UserInfo))
            throw new PlanningException("Configure a valid AI base URL without credentials in the URL.");
        if (settings.Provider == "OpenAI" && (baseUri.Scheme != "https" || string.IsNullOrWhiteSpace(settings.ApiKey)))
            throw new PlanningException("OpenAI needs an HTTPS endpoint and an API key configured on the server.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var contract = AiContract.For<T>(references);
        var messages = new List<object> { new { role = "system", content = system + "\nRequired JSON schema:\n" + contract.Schema }, new { role = "user", content = context + (settings.PromptSuffix is null ? "" : "\n" + settings.PromptSuffix) } };
        var generationKey=Guid.NewGuid().ToString("N");
        var generationActive=false;
        try
        {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var tokenParameter = settings.TokenLimitParameter ?? (settings.Provider == "OpenAI" ? "max_completion_tokens" : "max_tokens");
            if (tokenParameter is not ("max_tokens" or "max_completion_tokens")) throw new PlanningException("The token limit setting is invalid.");
            var body = new JsonObject
            {
                ["model"] = settings.Model, ["messages"] = JsonSerializer.SerializeToNode(messages, Json),
                [tokenParameter] = maxOutputTokens ?? settings.MaxOutputTokens, ["stream"] = false
            };
            if (settings.JsonMode ?? settings.Provider == "OpenAI") body["response_format"] = new JsonObject { ["type"] = "json_object" };
            if (settings.Temperature is not null) body["temperature"] = settings.Temperature.Value;
            if (settings.Provider == "KoboldCpp")
            {
                body["grammar"] = contract.Grammar;
                body["max_context_length"] = settings.MaxContextTokens;
                var countUri = new Uri(baseUri, "/api/extra/tokencount");
                var countBody = JsonSerializer.Serialize(new { prompt = KoboldPrompt(messages, settings.KoboldPromptFormat) });
                int? inputTokens = null;
                for (var countAttempt = 0; countAttempt < 3; countAttempt++)
                {
                    using var countRequest = new HttpRequestMessage(HttpMethod.Post, countUri)
                    {
                        Version = System.Net.HttpVersion.Version11,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                        Content = new StringContent(countBody, Encoding.UTF8, "application/json")
                    };
                    countRequest.Headers.ConnectionClose = true;
                    if (!string.IsNullOrWhiteSpace(settings.ApiKey)) countRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
                    try
                    {
                        using var counted = await http.SendAsync(countRequest, timeout.Token);
                        if (!counted.IsSuccessStatusCode) throw new PlanningException("Kobold could not check the prompt length. No truncated brief was submitted.");
                        using var tokens = JsonDocument.Parse(await counted.Content.ReadAsStringAsync(timeout.Token));
                        inputTokens = tokens.RootElement.GetProperty("value").GetInt32();
                        break;
                    }
                    catch (HttpRequestException ex) when (countAttempt < 2 && ex.HttpRequestError == HttpRequestError.ResponseEnded)
                    {
                        // Tokenization is read-only and safe to repeat on a fresh connection.
                    }
                    catch (JsonException) when (countAttempt < 2) { /* Read-only tokenizer responses can be retried safely. */ }
                    catch (JsonException) { throw new PlanningException("Kobold returned an unreadable token-count response. No generation was submitted."); }
                    catch (HttpRequestException ex) { throw new PlanningException($"Kobold's token-count endpoint failed ({ex.HttpRequestError}). No generation was submitted."); }
                }
                if (inputTokens is null) throw new PlanningException("Kobold could not check the prompt length.");
                var availableOutput = Math.Min(maxOutputTokens ?? settings.MaxOutputTokens, settings.MaxContextTokens - inputTokens.Value - 256);
                if (availableOutput < 512)
                    throw new PlanningException("This brief and extraction contract exceed the configured model context. Use a larger context or split the assessment; no text was silently discarded.");
                body[tokenParameter] = availableOutput;
            }
            if (settings.Provider == "KoboldCpp")
            {
                body = new JsonObject {
                    ["prompt"] = KoboldPrompt(messages, settings.KoboldPromptFormat),
                    ["grammar"] = contract.Grammar, ["max_context_length"] = settings.MaxContextTokens,
                    ["max_length"] = body[tokenParameter]!.GetValue<int>(),
                    ["rep_pen"] = 1.02, ["sampler_seed"] = 42, ["genkey"] = generationKey,
                    ["stop_sequence"] = settings.KoboldPromptFormat == "llama3"
                        ? new JsonArray("<|eot_id|>", "<|end_of_text|>") : new JsonArray("<|im_end|>")
                };
                if (settings.Temperature is not null) body["temperature"] = settings.Temperature.Value;
            }
            // Inference has no application writes. A broken transport may repeat the
            // identical prompt once; no partial output or plan is ever committed.
            HttpResponseMessage? response = null;
            for (var transportAttempt = 0; transportAttempt < 2; transportAttempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    settings.Provider == "KoboldCpp" ? new Uri(baseUri, "/api/v1/generate") : new Uri(baseUri, "chat/completions"))
                    { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                if (settings.Provider == "KoboldCpp") { request.Headers.ConnectionClose = true; request.Version = System.Net.HttpVersion.Version11; request.VersionPolicy = HttpVersionPolicy.RequestVersionExact; }
                if (!string.IsNullOrWhiteSpace(settings.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
                try { generationActive=settings.Provider=="KoboldCpp"; response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); break; }
                catch (HttpRequestException ex) when (settings.Provider == "KoboldCpp" && transportAttempt == 0 && ex.HttpRequestError == HttpRequestError.ResponseEnded) { }
                catch (HttpRequestException ex) { throw new PlanningException($"The AI connection failed ({ex.HttpRequestError}). Check the server and network."); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PlanningException("The AI request timed out. Try again or raise Ai:TimeoutSeconds."); }
            }
            if (response is null) throw new PlanningException("Kobold did not return a complete HTTP response.");
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new PlanningException($"The {settings.Provider} server returned HTTP {(int)response.StatusCode}. Check credentials, model and supported settings. No provider fallback was attempted.");
                // Bound the response body rather than accumulating an unbounded provider response.
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var limited = new MemoryStream();
                var buffer = new byte[8192]; int count;
                while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (limited.Length + count > 2_000_000) throw new PlanningException("The AI response exceeded the demo's safety limit.");
                    await limited.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                }
                generationActive=false;
                JsonDocument envelope;
                try { envelope = JsonDocument.Parse(limited.ToArray()); }
                catch (JsonException) { throw new PlanningException("The AI server returned an invalid API response."); }
                using (envelope)
                {
                    string text;
                    if (settings.Provider == "KoboldCpp")
                    {
                        if (!envelope.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0 ||
                            !results[0].TryGetProperty("text", out var nativeText) || nativeText.ValueKind != JsonValueKind.String)
                            throw new PlanningException("Kobold returned no generation text.");
                        text = nativeText.GetString()!.Trim();
                    }
                    else
                    {
                        if (!envelope.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                            throw new PlanningException("The AI server returned no completion choices.");
                        var choice = choices[0];
                        if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() == "length")
                            throw new PlanningException("The AI output was truncated. Increase the output token limit or use a shorter brief.");
                        if (!choice.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                            throw new PlanningException("The AI returned no text.");
                        text = content.GetString()!.Trim();
                    }
                    if (settings.StripThinkingTags && text.StartsWith("<think>", StringComparison.Ordinal))
                    {
                        var end = text.IndexOf("</think>", StringComparison.Ordinal);
                        if (end < 0) throw new PlanningException("The model returned incomplete thinking text without a final answer.");
                        text = text[(end + 8)..].Trim();
                    }
                    try
                    {
                        if (text.StartsWith("```"))
                        {
                            var newline = text.IndexOf('\n');
                            if (newline >= 0 && text.EndsWith("```")) text = text[(newline + 1)..^3].Trim();
                        }
                        var value = JsonSerializer.Deserialize<T>(text, AiJson) ?? throw new JsonException("Empty JSON.");
                        validate(value);
                        return value;
                    }
                    catch (Exception ex) when (ex is JsonException or PlanningException or NullReferenceException or ArgumentException)
                    {
                        var detail = ex is JsonException json
                            ? $"Invalid JSON field {json.Path ?? "$"} in {typeof(T).Name}: {json.Message}"
                            : ex is PlanningException ? ex.Message : $"Invalid {typeof(T).Name} value: {ex.Message}";
                        if (attempt == 1) throw new PlanningException("The AI output failed validation after one correction attempt. " + detail);
                        // Re-extract from the original sources plus the validation error.
                        // Echoing a full failed plan would consume the local model's context.
                        messages.Add(new { role = "user", content = "The extraction failed validation: " + detail + ". Generate the complete corrected JSON object from the original supplied sources. Do not invent evidence or omit supported work." });
                    }
                }
            }
        }
        throw new PlanningException("No valid AI result was returned.");
        }
        finally
        {
            if(generationActive)
            {
                // Cancel only this request so a timed-out date stage does not
                // leave the model busy when the questionnaire starts.
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var abort=new HttpRequestMessage(HttpMethod.Post,new Uri(baseUri,"/api/extra/abort")) {
                    Content=new StringContent(JsonSerializer.Serialize(new{genkey=generationKey}),Encoding.UTF8,"application/json")
                };
                if(!string.IsNullOrWhiteSpace(settings.ApiKey))abort.Headers.Authorization=new AuthenticationHeaderValue("Bearer",settings.ApiKey);
                try{using var ignored=await http.SendAsync(abort,cleanup.Token);}
                catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException){ }
            }
        }
    }
    static string KoboldPrompt(List<object> messages, string format)
    {
        if (format is not ("llama3" or "chatml")) throw new PlanningException("Choose a supported Kobold prompt format: llama3 or chatml.");
        var prompt = new StringBuilder(format == "llama3" ? "<|begin_of_text|>" : "");
        foreach (var item in messages)
        {
            var value = JsonSerializer.SerializeToElement(item, Json);
            var role = value.GetProperty("role").GetString();
            var content = value.GetProperty("content").GetString();
            if (format == "llama3") prompt.Append($"<|start_header_id|>{role}<|end_header_id|>\n\n{content}<|eot_id|>");
            else prompt.Append($"<|im_start|>{role}\n{content}<|im_end|>\n");
        }
        return prompt.Append(format == "llama3" ? "<|start_header_id|>assistant<|end_header_id|>\n\n" : "<|im_start|>assistant\n").ToString();
    }
}
