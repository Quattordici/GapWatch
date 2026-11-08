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
                    FindingLocation.Operation.Instance,
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
                    FindingLocation.Operation.Instance,
                    $"Operation '{now.Display}' was added.") { Position = now.Position, Document = now.Position?.File ?? now.Document });
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
        void Add(RuleDescriptor rule, FindingLocation location, string message, SourcePosition? position, SchemaOrigin? origin = null)
        {
            var at = position ?? now.Position;
            findings.Add(new Finding(rule, now.Display, location, message)
            {
                Origin = origin,
                Position = at,
                Document = at?.File ?? now.Document,
            });
        }

        if (!was.Deprecated && now.Deprecated)
        {
            Add(RuleCatalog.OperationDeprecated, FindingLocation.Operation.Instance, $"Operation '{now.Display}' was marked deprecated.", null);
        }

        if (!string.IsNullOrEmpty(was.OperationId)
            && !string.Equals(was.OperationId, now.OperationId, StringComparison.Ordinal))
        {
            Add(
                RuleCatalog.OperationIdChanged,
                FindingLocation.Operation.Instance,
                now.OperationId is null
                    ? $"operationId '{was.OperationId}' was removed."
                    : $"operationId changed from '{was.OperationId}' to '{now.OperationId}'.",
                null);
        }

        CompareParameters(was, now, Add);
        CompareRequestBody(was.RequestBody, now.RequestBody, Add);
        CompareResponses(was, now, Add);
        CompareSecurity(was, now, Add);
    }

    private static void CompareParameters(
        OperationContract was, OperationContract now, AddFinding add)
    {
        var before = ParameterIndex(was);
        var after = ParameterIndex(now);

        foreach (var (key, previous) in before)
        {

            if (!after.TryGetValue(key, out var current))
            {
                add(
                    RuleCatalog.ParameterRemoved,
                    new FindingLocation.Parameter(previous.Name, previous.Location),
                    $"Parameter '{previous.Name}' ({previous.Location}) was removed.",
                    null);
                continue;
            }

            if (!previous.Required && current.Required)
            {
                add(
                    RuleCatalog.ParameterBecameRequired,
                    new FindingLocation.Parameter(current.Name, current.Location),
                    $"Parameter '{current.Name}' ({current.Location}) became required.",
                    current.Position);
            }

            SchemaComparer.Compare(
                previous.Schema,
                current.Schema,
                Direction.Request,
                SchemaComparer.Root,
                current.Position,
                (rule, schemaPath, message, position, origin) => add(
                    rule,
                    new FindingLocation.Parameter(current.Name, current.Location, schemaPath),
                    message,
                    position,
                    origin));
        }

        foreach (var (key, current) in after)
        {
            if (current.Required && !before.ContainsKey(key))
            {
                add(
                    RuleCatalog.RequiredParameterAdded,
                    new FindingLocation.Parameter(current.Name, current.Location),
                    $"Required parameter '{current.Name}' ({current.Location}) was added.",
                    current.Position);
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
        RequestBodyContract? was, RequestBodyContract? now, AddFinding add)
    {
        if (was is null && now is null)
        {
            return;
        }

        if (was is null)
        {
            if (now!.Required)
            {
                add(RuleCatalog.RequiredRequestBodyAdded, FindingLocation.RequestBody.Instance, "A required request body was added.", now.Position);
            }

            return;
        }

        if (now is null)
        {
            add(RuleCatalog.RequestBodyRemoved, FindingLocation.RequestBody.Instance, "The request body was removed.", null);
            return;
        }

        if (!was.Required && now.Required)
        {
            add(RuleCatalog.RequestBodyBecameRequired, FindingLocation.RequestBody.Instance, "The request body became required.", now.Position);
        }

        foreach (var (mediaType, previous) in was.Content.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (!now.Content.TryGetValue(mediaType, out var current))
            {
                add(
                    RuleCatalog.MediaTypeRemoved,
                    new FindingLocation.RequestContent(mediaType),
                    $"Request media type '{mediaType}' is no longer accepted.",
                    now.Position);
                continue;
            }

            SchemaComparer.Compare(
                previous,
                current,
                Direction.Request,
                SchemaComparer.Root,
                now.Position,
                (rule, schemaPath, message, position, origin) => add(
                    rule,
                    new FindingLocation.RequestContent(mediaType, schemaPath),
                    message,
                    position,
                    origin));
        }
    }

    private static void CompareResponses(
        OperationContract was, OperationContract now, AddFinding add)
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
                        new FindingLocation.Response(code),
                        $"Success response {code} was removed.",
                        null);
                }
                else
                {
                    add(RuleCatalog.ResponseRemoved, new FindingLocation.Response(code), $"Response {code} was removed.", null);
                }

                continue;
            }

            foreach (var (mediaType, previousSchema) in previous.Content.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                if (!current.Content.TryGetValue(mediaType, out var currentSchema))
                {
                    add(
                        RuleCatalog.MediaTypeRemoved,
                        new FindingLocation.ResponseMediaType(code, mediaType),
                        $"Response {code} no longer produces media type '{mediaType}'.",
                        current.Position);
                    continue;
                }

                SchemaComparer.Compare(
                    previousSchema,
                    currentSchema,
                    Direction.Response,
                    SchemaComparer.Root,
                    current.Position,
                    (rule, schemaPath, message, position, origin) => add(
                        rule,
                        new FindingLocation.ResponseContent(code, mediaType, schemaPath),
                        message,
                        position,
                        origin));
            }
        }
    }

    private static void CompareSecurity(
        OperationContract was, OperationContract now, AddFinding add)
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
                FindingLocation.Security.Instance,
                $"The operation now requires authentication ({string.Join(" OR ", now.Security)}).",
                null);
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
                FindingLocation.Security.Instance,
                $"Previously accepted security ({string.Join(" OR ", was.Security)}) was replaced by ({string.Join(" OR ", now.Security)}).",
                null);
            return;
        }

        foreach (var alternative in removed)
        {
            add(
                RuleCatalog.SecurityRequirementRemoved,
                FindingLocation.Security.Instance,
                $"Security alternative '{alternative}' is no longer accepted.",
                null);
        }
    }

    private static bool IsSuccess(string statusCode) => statusCode.Length > 0 && statusCode[0] == '2';

    /// <summary>Adds a finding; a null <paramref name="position"/> falls back to the head operation's position.</summary>
    private delegate void AddFinding(
        RuleDescriptor rule, FindingLocation location, string message, SourcePosition? position, SchemaOrigin? origin = null);
}
