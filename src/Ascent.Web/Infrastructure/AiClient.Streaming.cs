using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ascent.Web.Planning;

namespace Ascent.Web.Infrastructure;

public interface IStreamingPlanningAi
{
    Task<ExtractedRequirements> StreamRequirementsAsync(string system, string context,
        Action<ExtractedRequirements> validate, Func<ExtractedRequirements, Task> progress,
        IReadOnlyDictionary<string,List<string>> references, CancellationToken ct);
}

public sealed partial class CompatiblePlanningAi
{
    public async Task<ExtractedRequirements> StreamRequirementsAsync(string system, string context,
        Action<ExtractedRequirements> validate, Func<ExtractedRequirements,Task> progress,
        IReadOnlyDictionary<string,List<string>> references, CancellationToken ct)
    {
        var settings=options.Value;
        if(settings.Provider is not ("KoboldCpp" or "OpenAI"))throw new PlanningException("Choose a supported AI provider.");
        if(!Uri.TryCreate(settings.BaseUrl.TrimEnd('/')+"/",UriKind.Absolute,out var root)||root.Scheme is not("http" or "https")||!string.IsNullOrEmpty(root.UserInfo))
            throw new PlanningException("Configure a valid AI endpoint.");
        if(settings.Provider=="OpenAI"&&(root.Scheme!="https"||string.IsNullOrWhiteSpace(settings.ApiKey)))throw new PlanningException("Configure the OpenAI endpoint and API key.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var token=timeout.Token;var key=Guid.NewGuid().ToString("N");var started=false;var finished=false;
        HttpRequestMessage Request(string path,object body)=>new(HttpMethod.Post,new Uri(root,path)) {
            Content=new StringContent(JsonSerializer.Serialize(body,Json),Encoding.UTF8,"application/json"),
            Version=System.Net.HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact
        };
        void Authenticate(HttpRequestMessage request){if(!string.IsNullOrWhiteSpace(settings.ApiKey))request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",settings.ApiKey);}
        var contract=AiContract.For<ExtractedRequirements>(references);
        var messages=new List<object>{new{role="system",content=system+"\nRequired JSON schema:\n"+contract.Schema},new{role="user",content=context+(settings.PromptSuffix is null?"":"\n"+settings.PromptSuffix)}};
        var maxTokens=4096;
        JsonObject body;
        if(settings.Provider=="KoboldCpp")
        {
            var prompt=KoboldPrompt(messages,settings.KoboldPromptFormat);
            using var count=Request("/api/extra/tokencount",new{prompt});Authenticate(count);count.Headers.ConnectionClose=true;
            using var counted=await http.SendAsync(count,token);
            if(!counted.IsSuccessStatusCode)throw new PlanningException("The model could not check the document size.");
            using var counts=JsonDocument.Parse(await counted.Content.ReadAsStringAsync(token));
            maxTokens=Math.Min(maxTokens,settings.MaxContextTokens-counts.RootElement.GetProperty("value").GetInt32()-256);
            if(maxTokens<512)throw new PlanningException("This brief exceeds the configured model context; no document text was removed.");
            body=new(){["prompt"]=prompt,["grammar"]=contract.Grammar,["max_context_length"]=settings.MaxContextTokens,["max_length"]=maxTokens,
                ["rep_pen"]=1.02,["sampler_seed"]=42,["genkey"]=key,["stop_sequence"]=settings.KoboldPromptFormat=="llama3"?new JsonArray("<|eot_id|>","<|end_of_text|>"):new JsonArray("<|im_end|>")};
        }
        else
        {
            var limit=settings.TokenLimitParameter??"max_completion_tokens";
            if(limit is not("max_tokens" or "max_completion_tokens"))throw new PlanningException("Invalid AI token limit parameter.");
            body=new(){["model"]=settings.Model,["messages"]=JsonSerializer.SerializeToNode(messages,Json),[limit]=maxTokens,["stream"]=true};
            if(settings.JsonMode??true)body["response_format"]=new JsonObject{["type"]="json_object"};
        }
        if(settings.Temperature is not null)body["temperature"]=settings.Temperature.Value;
        try
        {
            using var request=Request(settings.Provider=="KoboldCpp"?"/api/extra/generate/stream":"chat/completions",body);Authenticate(request);
            if(settings.Provider=="KoboldCpp")request.Headers.ConnectionClose=true;
            started=true;
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
            if(!response.IsSuccessStatusCode)throw new PlanningException($"AI returned HTTP {(int)response.StatusCode} while extracting tasks.");
            using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(token));
            var output=new StringBuilder();var shown=0;
            while(await reader.ReadLineAsync(token) is {} line)
            {
                if(line.Length>200000)throw new PlanningException("AI stream event exceeded its limit.");
                if(!line.StartsWith("data:",StringComparison.Ordinal))continue;
                var data=line[5..].Trim();if(data=="[DONE]"){finished=true;break;}if(data.Length==0)continue;
                using var packet=JsonDocument.Parse(data);var value=packet.RootElement;string? text=null;
                if(value.TryGetProperty("error",out _))throw new PlanningException("AI reported an extraction error.");
                if(settings.Provider=="KoboldCpp")
                {
                    if(value.TryGetProperty("token",out var part))text=part.GetString();
                    if(value.TryGetProperty("finish_reason",out var end)&&end.ValueKind==JsonValueKind.String)finished=true;
                }
                else if(value.TryGetProperty("choices",out var choices)&&choices.GetArrayLength()>0)
                {
                    var choice=choices[0];
                    if(choice.TryGetProperty("delta",out var delta)&&delta.TryGetProperty("content",out var part)&&part.ValueKind==JsonValueKind.String)text=part.GetString();
                    if(choice.TryGetProperty("finish_reason",out var end)&&end.ValueKind==JsonValueKind.String)finished=true;
                }
                output.Append(text);if(output.Length>200000)throw new PlanningException("AI extraction exceeded its output limit.");
                var snapshot=RequirementStream.Snapshot(output.ToString());
                if(snapshot is not null&&snapshot.Requirements.Count>shown){shown=snapshot.Requirements.Count;await progress(snapshot);}
            }
            if(!finished)throw new PlanningException("The task stream ended before completion.");
            var result=JsonSerializer.Deserialize<ExtractedRequirements>(output.ToString(),AiJson)??throw new PlanningException("The model returned no tasks.");
            validate(result);return result;
        }
        finally
        {
            if(started&&!finished&&settings.Provider=="KoboldCpp")
            {
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try{using var abort=Request("/api/extra/abort",new{genkey=key});Authenticate(abort);using var ignored=await http.SendAsync(abort,cleanup.Token);}
                catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException){ }
            }
        }
    }
}

public static class RequirementStream
{
    // Parse only complete JSON values. Never display a half-written task or scrape model prose.
    public static ExtractedRequirements? Snapshot(string json)
    {
        string title="",summary="";List<Requirement> requirements=[];
        try
        {
            var reader=new Utf8JsonReader(Encoding.UTF8.GetBytes(json),isFinalBlock:false,state:default);
            if(!reader.Read()||reader.TokenType!=JsonTokenType.StartObject)return null;
            while(reader.Read())
            {
                if(reader.TokenType!=JsonTokenType.PropertyName)continue;
                var name=reader.GetString();if(!reader.Read())break;
                if(name=="requirements"&&reader.TokenType==JsonTokenType.StartArray)
                {
                    while(reader.Read()&&reader.TokenType!=JsonTokenType.EndArray)
                    {
                        if(!JsonDocument.TryParseValue(ref reader,out var item))break;
                        using(item){var requirement=item.RootElement.Deserialize<Requirement>(CompatiblePlanningAi.AiJson);if(requirement is not null)requirements.Add(requirement);}
                    }
                    break;
                }
                if(!JsonDocument.TryParseValue(ref reader,out var field))break;
                using(field){if(name=="moduleTitle")title=field.RootElement.GetString()??"";if(name=="summary")summary=field.RootElement.GetString()??"";}
            }
        }
        catch(JsonException){return null;}
        return title.Length>0&&requirements.Count>0?new(title,summary,requirements,[]):null;
    }
}
