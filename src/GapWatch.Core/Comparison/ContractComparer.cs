using GapWatch.Core.Findings;
using GapWatch.Core.Model;

namespace GapWatch.Core.Comparison;

/// <summary>Compares a baseline contract with a head contract and reports every consumer-visible difference.</summary>
public sealed class ContractComparer
{
    /// <summary>Compares <paramref name="baseline"/> (what consumers rely on today) against <paramref name="head"/> (the proposed change).</summary>
    public ComparisonResult Compare(ApiContract baseline, ApiContract head)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(head);

        var findings = new List<Finding>();
        var baselineOps = Index(baseline);
        var headOps = Index(head);

        foreach (var (key, was) in baselineOps)
        {
            if (!headOps.TryGetValue(key, out var now))
            {
                findings.Add(new Finding(
                    RuleCatalog.OperationRemoved,
                    was.Display,
                    "operation",
                    $"Operation '{was.Display}' was removed."));
                continue;
            }

            CompareOperation(was, now, findings);
        }

        foreach (var (key, now) in headOps)
        {
            if (!baselineOps.ContainsKey(key))
            {
                findings.Add(new Finding(
                    RuleCatalog.OperationAdded,
                    now.Display,
                    "operation",
                    $"Operation '{now.Display}' was added."));
            }
        }

        return new ComparisonResult(FindingOrder.Sort(findings));
    }

    private static Dictionary<string, OperationContract> Index(ApiContract contract)
    {
        var map = new Dictionary<string, OperationContract>(StringComparer.Ordinal);
        foreach (var operation in contract.Operations)
        {
            map.TryAdd(operation.Key, operation);
        }

        return map;
    }

    private static void CompareOperation(OperationContract was, OperationContract now, List<Finding> findings)
    {
        void Add(RuleDescriptor rule, string location, string message) =>
            findings.Add(new Finding(rule, now.Display, location, message));

        if (!was.Deprecated && now.Deprecated)
        {
            Add(RuleCatalog.OperationDeprecated, "operation", $"Operation '{now.Display}' was marked deprecated.");
        }

        if (!string.IsNullOrEmpty(was.OperationId)
            && !string.Equals(was.OperationId, now.OperationId, StringComparison.Ordinal))
        {
            Add(
                RuleCatalog.OperationIdChanged,
                "operation",
                now.OperationId is null
                    ? $"operationId '{was.OperationId}' was removed."
                    : $"operationId changed from '{was.OperationId}' to '{now.OperationId}'.");
        }

        CompareParameters(was, now, Add);
        CompareRequestBody(was.RequestBody, now.RequestBody, Add);
        CompareResponses(was, now, Add);
        CompareSecurity(was, now, Add);
    }

    private static void CompareParameters(
        OperationContract was, OperationContract now, Action<RuleDescriptor, string, string> add)
    {
        var before = ParameterIndex(was);
        var after = ParameterIndex(now);

        foreach (var (key, previous) in before)
        {
            var location = $"parameter '{previous.Name}' ({previous.Location})";

            if (!after.TryGetValue(key, out var current))
            {
                add(
                    RuleCatalog.ParameterRemoved,
                    location,
                    $"Parameter '{previous.Name}' ({previous.Location}) was removed.");
                continue;
            }

            if (!previous.Required && current.Required)
            {
                add(
                    RuleCatalog.ParameterBecameRequired,
                    $"parameter '{current.Name}' ({current.Location})",
                    $"Parameter '{current.Name}' ({current.Location}) became required.");
            }

            SchemaComparer.Compare(
                previous.Schema,
                current.Schema,
                Direction.Request,
                SchemaComparer.Root,
                (rule, schemaPath, message) => add(
                    rule,
                    FormatLocation($"parameter '{current.Name}' ({current.Location})", schemaPath),
                    message));
        }

        foreach (var (key, current) in after)
        {
            if (current.Required && !before.ContainsKey(key))
            {
                add(
                    RuleCatalog.RequiredParameterAdded,
                    $"parameter '{current.Name}' ({current.Location})",
                    $"Required parameter '{current.Name}' ({current.Location}) was added.");
            }
        }
    }

    private static Dictionary<string, ParameterContract> ParameterIndex(OperationContract operation)
    {
        var pathNames = PathTemplate.ParameterNames(operation.Path).ToList();
        var map = new Dictionary<string, ParameterContract>(StringComparer.Ordinal);

        foreach (var parameter in operation.Parameters)
        {
            string key;
            if (parameter.Location == "path")
            {
                // Pair path parameters by position so that renaming {id} to {petId} is not a change.
                var index = pathNames.IndexOf(parameter.Name);
                key = index >= 0 ? $"path#{index}" : $"path:{parameter.Name}";
            }
            else if (parameter.Location == "header")
            {
                key = $"header:{parameter.Name.ToLowerInvariant()}";
            }
            else
            {
                key = $"{parameter.Location}:{parameter.Name}";
            }

            map.TryAdd(key, parameter);
        }

        return map;
    }

    private static void CompareRequestBody(
        RequestBodyContract? was, RequestBodyContract? now, Action<RuleDescriptor, string, string> add)
    {
        if (was is null && now is null)
        {
            return;
        }

        if (was is null)
        {
            if (now!.Required)
            {
                add(RuleCatalog.RequiredRequestBodyAdded, "request body", "A required request body was added.");
            }

            return;
        }

        if (now is null)
        {
            add(RuleCatalog.RequestBodyRemoved, "request body", "The request body was removed.");
            return;
        }

        if (!was.Required && now.Required)
        {
            add(RuleCatalog.RequestBodyBecameRequired, "request body", "The request body became required.");
        }

        foreach (var (mediaType, previous) in was.Content.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (!now.Content.TryGetValue(mediaType, out var current))
            {
                add(
                    RuleCatalog.MediaTypeRemoved,
                    $"request body ({mediaType})",
                    $"Request media type '{mediaType}' is no longer accepted.");
                continue;
            }

            SchemaComparer.Compare(
                previous,
                current,
                Direction.Request,
                SchemaComparer.Root,
                (rule, schemaPath, message) => add(
                    rule,
                    FormatLocation($"request body ({mediaType})", schemaPath),
                    message));
        }
    }

    private static void CompareResponses(
        OperationContract was, OperationContract now, Action<RuleDescriptor, string, string> add)
    {
        var before = was.Responses.GroupBy(r => r.StatusCode.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var after = now.Responses.GroupBy(r => r.StatusCode.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var (code, previous) in before.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            if (!after.TryGetValue(code, out var current))
            {
                if (IsSuccess(code))
                {
                    add(
                        RuleCatalog.SuccessResponseRemoved,
                        $"response {code}",
                        $"Success response {code} was removed.");
                }
                else
                {
                    add(RuleCatalog.ResponseRemoved, $"response {code}", $"Response {code} was removed.");
                }

                continue;
            }

            foreach (var (mediaType, previousSchema) in previous.Content.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                if (!current.Content.TryGetValue(mediaType, out var currentSchema))
                {
                    add(
                        RuleCatalog.MediaTypeRemoved,
                        $"response {code}",
                        $"Response {code} no longer produces media type '{mediaType}'.");
                    continue;
                }

                SchemaComparer.Compare(
                    previousSchema,
                    currentSchema,
                    Direction.Response,
                    SchemaComparer.Root,
                    (rule, schemaPath, message) => add(
                        rule,
                        FormatLocation($"response {code} {mediaType}", schemaPath),
                        message));
            }
        }
    }

    private static void CompareSecurity(
        OperationContract was, OperationContract now, Action<RuleDescriptor, string, string> add)
    {
        const string None = "none";

        static bool IsOpen(IReadOnlyList<string> alternatives) =>
            alternatives.Count == 0 || alternatives.Contains(None, StringComparer.Ordinal);

        var wasOpen = IsOpen(was.Security);
        var nowOpen = IsOpen(now.Security);

        if (wasOpen && !nowOpen)
        {
            add(
                RuleCatalog.SecurityAdded,
                "security",
                $"The operation now requires authentication ({string.Join(" OR ", now.Security)}).");
            return;
        }

        if (wasOpen || nowOpen)
        {
            return;
        }

        var removed = was.Security.Except(now.Security, StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
        if (removed.Count == was.Security.Count)
        {
            add(
                RuleCatalog.SecurityRequirementsReplaced,
                "security",
                $"Previously accepted security ({string.Join(" OR ", was.Security)}) was replaced by ({string.Join(" OR ", now.Security)}).");
            return;
        }

        foreach (var alternative in removed)
        {
            add(
                RuleCatalog.SecurityRequirementRemoved,
                "security",
                $"Security alternative '{alternative}' is no longer accepted.");
        }
    }

    private static bool IsSuccess(string statusCode) => statusCode.Length > 0 && statusCode[0] == '2';

    private static string FormatLocation(string prefix, string schemaPath) =>
        schemaPath == SchemaComparer.Root ? prefix : $"{prefix}: {schemaPath}";
}
