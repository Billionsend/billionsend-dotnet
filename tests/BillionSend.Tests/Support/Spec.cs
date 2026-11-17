using System.Text.Json.Nodes;
using Json.Schema;

namespace BillionSend.Tests.Support;

/// <summary>One operation of the OpenAPI snapshot.</summary>
public sealed record Operation(string Method, string Path, JsonObject Node)
{
    public string Key => $"{Method} {Path}";

    public string OperationId => (string)Node["operationId"]!;

    public IEnumerable<JsonObject> Parameters(string where) =>
        (Node["parameters"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Where(p => (string?)p["in"] == where);

    public bool TakesApiKey => Node["security"] is not JsonArray { Count: 0 };

    public bool Idempotent => Parameters("header").Any(p => ((string)p["name"]!).Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase));

    public bool Stream => SuccessResponse?["content"]?["text/event-stream"] is not null;

    public JsonObject? RequestBody => Node["requestBody"] as JsonObject;

    public string? SuccessStatus =>
        (Node["responses"] as JsonObject)?.Select(r => r.Key).Where(code => code.StartsWith('2')).OrderBy(code => code).FirstOrDefault();

    public JsonObject? SuccessResponse => SuccessStatus is { } status ? Node["responses"]![status] as JsonObject : null;

    public JsonNode? ResponseSchema => SuccessResponse?["content"]?["application/json"]?["schema"];

    public JsonNode? RequestSchema => RequestBody?["content"]?["application/json"]?["schema"];

    public bool Paginated
    {
        get
        {
            var query = Parameters("query").Select(p => (string)p["name"]!).ToHashSet();
            if (!query.Contains("after") || !query.Contains("before") || ResponseSchema is null)
            {
                return false;
            }

            var page = Spec.Resolve(ResponseSchema);
            return page["properties"]?["has_more"] is not null && page["properties"]?["data"] is not null;
        }
    }
}

/// <summary>The OpenAPI snapshot the SDK is generated from: operations, sample values and a schema check.</summary>
public static class Spec
{
    public const string SampleUuid = "6a1f1d38-3c55-4a4e-9a57-0f2b3c4d5e6f";

    private static readonly string[] HttpMethods = ["get", "put", "post", "patch", "delete"];

    public static readonly string SnapshotPath = FindSnapshot();

    public static readonly JsonObject Document = (JsonObject)JsonNode.Parse(File.ReadAllText(SnapshotPath))!;

    public static readonly IReadOnlyList<Operation> Operations = LoadOperations();

    public static readonly IReadOnlySet<string> OperationKeys = Operations.Select(o => o.Key).ToHashSet();

    public static Operation Get(string key) =>
        Operations.FirstOrDefault(o => o.Key == key) ?? throw new KeyNotFoundException($"No operation {key} in the snapshot");

    /// <summary>The repository root: the directory that holds <c>openapi/api.openapi.json</c>.</summary>
    public static string RepositoryRoot => Path.GetDirectoryName(Path.GetDirectoryName(SnapshotPath)!)!;

    public static JsonObject Resolve(JsonNode schema)
    {
        var node = schema;
        while (node is JsonObject obj && obj["$ref"] is JsonValue reference)
        {
            var name = ((string)reference!).Split('/').Last();
            node = Document["components"]!["schemas"]![name]!;
        }

        return node as JsonObject ?? new JsonObject();
    }

    /// <summary>
    /// A deterministic instance of a schema: objects get every property when <paramref name="full"/>,
    /// otherwise only the required ones; arrays one item (or their minimum).
    /// </summary>
    public static JsonNode? Example(JsonNode? schema, bool full, int depth = 0)
    {
        if (schema is not JsonObject || depth > 24)
        {
            return new JsonObject();
        }

        var s = Resolve(schema);
        if (s["allOf"] is JsonArray { Count: 1 } only)
        {
            return Example(only[0], full, depth + 1);
        }

        if (!new[] { "type", "enum", "anyOf", "oneOf", "properties", "const" }.Any(s.ContainsKey))
        {
            return new JsonObject();
        }

        if (s["const"] is { } constant)
        {
            return constant.DeepClone();
        }

        if ((s["anyOf"] ?? s["oneOf"]) is JsonArray variants)
        {
            var chosen = variants.FirstOrDefault(v => (string?)Resolve(v!)["type"] != "null") ?? variants[0];
            return Example(chosen, full, depth + 1);
        }

        if (s["enum"] is JsonArray values)
        {
            return values.FirstOrDefault(v => v is not null)?.DeepClone();
        }

        var types = Types(s);
        if (types.Count == 0 && s["properties"] is not null)
        {
            types = ["object"];
        }

        switch (types.FirstOrDefault(t => t != "null") ?? "null")
        {
            case "string":
                return JsonValue.Create(ExampleString(s));
            case "integer":
                var max = (long?)s["maximum"] ?? 1;
                return JsonValue.Create((long?)s["minimum"] ?? (max >= 1 ? 1 : 0));
            case "number":
                return JsonValue.Create((double?)s["minimum"] ?? 1.0);
            case "boolean":
                return JsonValue.Create(false);
            case "array":
            {
                var count = Math.Max((int?)s["minItems"] ?? 1, 1);
                if ((int?)s["maxItems"] is int maxItems)
                {
                    count = Math.Min(count, maxItems);
                }

                var array = new JsonArray();
                for (var i = 0; i < count; i++)
                {
                    array.Add(Example(s["items"] ?? new JsonObject(), full, depth + 1));
                }

                return array;
            }

            case "object":
            {
                var required = (s["required"] as JsonArray)?.Select(r => (string)r!).ToHashSet() ?? [];
                var obj = new JsonObject();
                foreach (var pair in s["properties"] as JsonObject ?? new JsonObject())
                {
                    if (full || required.Contains(pair.Key))
                    {
                        obj[pair.Key] = Example(pair.Value, full, depth + 1);
                    }
                }

                return obj;
            }

            default:
                return null;
        }
    }

    /// <summary>The ways <paramref name="value"/> breaks <paramref name="schema"/> (JSON Schema 2020-12); empty when it matches.</summary>
    public static List<string> Validate(JsonNode? value, JsonNode? schema, string at = "$")
    {
        if (schema is null)
        {
            return [];
        }

        var compiled = Compiled.GetOrAdd(schema.ToJsonString(), Compile);
        using var instance = System.Text.Json.JsonDocument.Parse(value?.ToJsonString() ?? "null");
        var results = compiled.Evaluate(instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        if (results.IsValid)
        {
            return [];
        }

        return (results.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{at}{d.InstanceLocation}: {e.Value}"))
            .Take(5)
            .DefaultIfEmpty($"{at}: does not match the schema")
            .ToList();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonSchema> Compiled = new();

    private static readonly Lazy<string> Definitions = new(() =>
    {
        var schemas = Document["components"]!["schemas"]!.DeepClone().AsObject();
        foreach (var pair in schemas)
        {
            StripOpenApiKeywords(pair.Value);
        }

        return schemas.ToJsonString().Replace("#/components/schemas/", "#/$defs/");
    });

    /// <summary>OpenAPI's own schema keywords (<c>example</c>), which a strict 2020-12 evaluator refuses.</summary>
    private static readonly string[] OpenApiKeywords = ["example", "discriminator", "xml", "externalDocs"];

    /// <summary>The schema with every snapshot schema beside it under <c>$defs</c>, its references pointed there.</summary>
    private static JsonSchema Compile(string schemaText)
    {
        var root = JsonNode.Parse(schemaText.Replace("#/components/schemas/", "#/$defs/"))!.AsObject();
        StripOpenApiKeywords(root);
        root["$defs"] = JsonNode.Parse(Definitions.Value);
        root["$id"] = $"urn:billionsend:test:{Guid.NewGuid():N}";
        return JsonSchema.FromText(root.ToJsonString());
    }

    private static void StripOpenApiKeywords(JsonNode? schema)
    {
        if (schema is not JsonObject obj)
        {
            return;
        }

        foreach (var keyword in OpenApiKeywords)
        {
            obj.Remove(keyword);
        }

        foreach (var pair in obj.ToList())
        {
            switch (pair.Key)
            {
                case "properties" or "patternProperties" or "$defs" or "dependentSchemas" when pair.Value is JsonObject map:
                    foreach (var entry in map)
                    {
                        StripOpenApiKeywords(entry.Value);
                    }

                    break;
                case "allOf" or "anyOf" or "oneOf" or "prefixItems" when pair.Value is JsonArray list:
                    foreach (var entry in list)
                    {
                        StripOpenApiKeywords(entry);
                    }

                    break;
                case "items" or "additionalProperties" or "not" or "if" or "then" or "else" or "contains" or "propertyNames":
                    StripOpenApiKeywords(pair.Value);
                    break;
            }
        }
    }

    /// <summary>A query, path or header value read back as its schema's type (lists split on commas).</summary>
    public static JsonNode? Coerce(string value, JsonNode schema)
    {
        var s = Resolve(schema);
        if (Types(s).Contains("array"))
        {
            var items = Resolve(s["items"] ?? new JsonObject());
            return new JsonArray(value.Split(',').Select(v => Scalar(v, items)).ToArray());
        }

        return Scalar(value, s);
    }

    private static JsonNode? Scalar(string value, JsonObject s)
    {
        var types = Types(s);
        if (types.Contains("integer") && long.TryParse(value, out var integer))
        {
            return JsonValue.Create(integer);
        }

        if (types.Contains("number") && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return JsonValue.Create(number);
        }

        if (types.Contains("boolean") && value is "true" or "false")
        {
            return JsonValue.Create(value == "true");
        }

        return JsonValue.Create(value);
    }

    private static List<string> Types(JsonObject schema) => schema["type"] switch
    {
        JsonArray list => list.Select(t => (string)t!).ToList(),
        JsonValue one => [(string)one!],
        _ => [],
    };

    private static string ExampleString(JsonObject s)
    {
        var text = (string?)s["format"] switch
        {
            "date-time" => "2026-01-01T00:00:00.000Z",
            "date" => "2026-01-01",
            "email" => "user@example.com",
            "uri" or "url" => "https://example.com/",
            "uuid" => "0b6f5a2e-7c1d-4e8a-9f3b-2d4c6e8a0b1c",
            _ => null,
        };
        if (text is not null)
        {
            return text;
        }

        text = "value";
        if ((int?)s["minLength"] is int min && text.Length < min)
        {
            text = text.PadRight(min, 'x');
        }

        if ((int?)s["maxLength"] is int max && text.Length > max)
        {
            text = text[..max];
        }

        return text;
    }

    private static IReadOnlyList<Operation> LoadOperations()
    {
        var list = new List<Operation>();
        foreach (var path in (JsonObject)Document["paths"]!)
        {
            foreach (var item in (JsonObject)path.Value!)
            {
                if (HttpMethods.Contains(item.Key))
                {
                    list.Add(new Operation(item.Key.ToUpperInvariant(), path.Key, (JsonObject)item.Value!));
                }
            }
        }

        return list;
    }

    private static string FindSnapshot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "openapi", "api.openapi.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("openapi/api.openapi.json not found above the test directory");
    }
}
