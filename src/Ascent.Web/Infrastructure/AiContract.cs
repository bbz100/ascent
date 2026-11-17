using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ascent.Web.Planning;

namespace Ascent.Web.Infrastructure;

// The same DTO contract drives the prompt and Kobold grammar. No subject-specific
// fields, questions, dates or examples are embedded in this compiler.
public sealed record AiContract(string Schema, string Grammar)
{
    public static AiContract For<T>(IReadOnlyDictionary<string, List<string>>? references = null) => references is null ? Cache<T>.Value : Build(typeof(T), references);
    static class Cache<T> { public static readonly AiContract Value = Build(typeof(T)); }
    static AiContract Build(Type type, IReadOnlyDictionary<string, List<string>>? references = null)
    {
        var rules = new Dictionary<string, string>
        {
            ["ws"] = "[ \\t\\n\\r]*",
            ["character"] = "[^\"\\\\\\x00-\\x1F] | \"\\\\\" ([\"\\\\/bfnrt] | \"u\" [0-9a-fA-F]{4})",
            ["number"] = "\"-\"? (\"0\" | [1-9] [0-9]*) (\".\" [0-9]+)? ([eE] [+-]? [0-9]+)? ws",
            ["integer"] = "\"-\"? (\"0\" | [1-9] [0-9]*) ws",
            ["boolean"] = "(\"true\" | \"false\") ws"
        };
        var nullability = new NullabilityInfoContext();
        static string Literal(string s) => JsonSerializer.Serialize(s);
        (JsonNode Schema, string Rule) Visit(Type t, string name, bool nullable = false)
        {
            if ((t == typeof(string) && nullable || Nullable.GetUnderlyingType(t) is not null) && references?.TryGetValue(name + "-value", out var permitted) == true && permitted.Count == 0)
                return (new JsonObject { ["type"] = "null" }, "\"null\" ws");
            if (t == typeof(string) && !nullable && references?.TryGetValue(name, out var values) == true && values.Count > 0)
                return (values.Count <= 32
                    ? new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values.Distinct().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) }
                    : new JsonObject { ["type"] = "string", ["description"] = "Select an exact value from the supplied source or identifier registry." },
                    "(" + string.Join(" | ", values.Distinct().Select(v => Literal(JsonSerializer.Serialize(v)))) + ") ws");
            var underlying = Nullable.GetUnderlyingType(t);
            if (underlying is not null || nullable)
            {
                var inner = Visit(underlying ?? t, name + "-value");
                return (new JsonObject { ["anyOf"] = new JsonArray(inner.Schema, new JsonObject { ["type"] = "null" }) }, $"({inner.Rule} | \"null\" ws)");
            }
            if (t == typeof(DateOnly))
            {
                rules[name] = "\"\\\"\" character{1,100} \"\\\"\" ws";
                return (new JsonObject { ["type"] = "string", ["maxLength"] = 100, ["description"] = "Copy the complete printed calendar date including its year, such as 12th January 2027. ISO dates are also accepted. Numeric dates use day/month/year. Do not calculate dates." }, name);
            }
            if (t == typeof(TimeOnly))
            {
                rules[name] = "\"\\\"\" ([01] [0-9] | \"2\" [0-3]) \":\" [0-5] [0-9] \":\" [0-5] [0-9] \"\\\"\" ws";
                return (new JsonObject { ["type"] = "string", ["description"] = "Time in HH:mm:ss format." }, name);
            }
            if (t == typeof(string))
            {
                var field = (name.EndsWith("-value") ? name[..^6] : name).Split('-').Last();
                var length = field is "id" or "paragraphid" or "gapkey" or "questionid" or "optionid" or "targetid" or "deadlineid" ? 80
                    : field is "title" or "moduletitle" or "label" ? 160 : field == "quote" ? 300 : field == "summary" ? 700 : 500;
                rules[name] = $"\"\\\"\" character{{0,{length}}} \"\\\"\" ws";
                return (new JsonObject { ["type"] = "string", ["maxLength"] = length }, name);
            }
            if (t == typeof(bool)) return (new JsonObject { ["type"] = "boolean" }, "boolean");
            if (t == typeof(int)) return (new JsonObject { ["type"] = "integer" }, "integer");
            if (t == typeof(double) || t == typeof(decimal)) return (new JsonObject { ["type"] = "number" }, "number");
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var itemType = t.GetGenericArguments()[0];
                var item = Visit(itemType, name + "-item");
                var limit = name.EndsWith("-paragraphids") ? 16 : itemType == typeof(Clarification) || itemType == typeof(QuestionOption) || itemType == typeof(QuestionField) ? 8 : 160;
                var sourceIds = name.EndsWith("-paragraphids");
                rules[name] = sourceIds
                    ? $"\"[\" ws {item.Rule} (\",\" ws {item.Rule}){{0,{limit - 1}}} \"]\" ws"
                    : $"\"[\" ws ({item.Rule} (\",\" ws {item.Rule}){{0,{limit - 1}}})? \"]\" ws";
                var arraySchema = new JsonObject { ["type"] = "array", ["items"] = item.Schema, ["maxItems"] = limit };
                if (sourceIds) { arraySchema["minItems"] = 1; arraySchema["uniqueItems"] = true; }
                return (arraySchema, name);
            }
            var properties = new JsonObject(); var required = new JsonArray(); var fields = new List<string>();
            foreach (var property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() is null))
            {
                // The model cites IDs only. C# supplies original text; legacy fields remain readable in saved projects.
                if (t == typeof(Evidence) && property.Name != nameof(Evidence.ParagraphIds)) continue;
                var key = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                var part = Visit(property.PropertyType, name + "-" + key.ToLowerInvariant(), !property.PropertyType.IsValueType && nullability.Create(property).ReadState == NullabilityState.Nullable);
                if (property.GetCustomAttribute<JsonClockTimeAttribute>() is not null)
                {
                    var clockRule = "\"\\\"\" character{1,40} \"\\\"\" ws";
                    var clockSchema = new JsonObject { ["type"] = "string", ["maxLength"] = 40, ["description"] = "Copy the printed clock time, such as 2pm or 2 pm. HH:mm is also accepted." };
                    var optional = nullability.Create(property).ReadState == NullabilityState.Nullable;
                    part = optional
                        ? (new JsonObject { ["anyOf"] = new JsonArray(clockSchema, new JsonObject { ["type"] = "null" }) }, $"({clockRule} | \"null\" ws)")
                        : (clockSchema, clockRule);
                }
                if (property.GetCustomAttribute<JsonChoicesAttribute>() is { } choices)
                {
                    part = (new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(choices.Values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) },
                        "(" + string.Join(" | ", choices.Values.Select(v => Literal(JsonSerializer.Serialize(v)))) + ") ws");
                }
                properties[key] = part.Schema; required.Add(key);
                fields.Add($"{Literal(JsonSerializer.Serialize(key))} ws \":\" ws {part.Rule}");
            }
            rules[name] = "\"{\" ws " + string.Join(" \",\" ws ", fields) + " \"}\" ws";
            return (new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false }, name);
        }
        var root = Visit(type, "result");
        // A completed root must end generation. Allowing trailing whitespace lets
        // some local models spend the remaining token budget emitting blank lines.
        if (rules.TryGetValue("result", out var rootRule) && rootRule.EndsWith(" ws")) rules["result"] = rootRule[..^3];
        return new(root.Schema.ToJsonString(), "root ::= ws result\n" + string.Join('\n', rules.Select(r => $"{r.Key} ::= {r.Value}")));
    }
}
