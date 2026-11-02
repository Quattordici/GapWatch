namespace GapWatch.Core.Findings;

/// <summary>
/// Every rule GapWatch knows. Rule ids are part of the public contract: they never change meaning and are
/// never reused. Direction-specific behaviour (request vs response) is documented per rule.
/// </summary>
public static class RuleCatalog
{
    // --- Operations -------------------------------------------------------------------------------------------

    public static readonly RuleDescriptor OperationRemoved = new("GW001", nameof(OperationRemoved), Severity.Breaking,
        "An operation (method + path) that existed in the base contract no longer exists.");

    public static readonly RuleDescriptor OperationDeprecated = new("GW002", nameof(OperationDeprecated), Severity.Info,
        "An operation was newly marked deprecated.");

    public static readonly RuleDescriptor OperationAdded = new("GW003", nameof(OperationAdded), Severity.Info,
        "A new operation was added.");

    public static readonly RuleDescriptor OperationIdChanged = new("GW004", nameof(OperationIdChanged), Severity.Warning,
        "The operationId changed. Generated clients (NSwag, Kiota, ...) name their methods after it, so this breaks callers of the generated code.");

    // --- Parameters -------------------------------------------------------------------------------------------

    public static readonly RuleDescriptor RequiredParameterAdded = new("GW010", nameof(RequiredParameterAdded), Severity.Breaking,
        "A new required parameter was added; existing callers do not send it.");

    public static readonly RuleDescriptor ParameterBecameRequired = new("GW011", nameof(ParameterBecameRequired), Severity.Breaking,
        "An optional parameter became required; callers that omitted it now fail.");

    public static readonly RuleDescriptor ParameterRemoved = new("GW012", nameof(ParameterRemoved), Severity.Warning,
        "A parameter was removed. Callers that still send it may be rejected or silently ignored.");

    // --- Schemas (shared by parameters, request bodies and responses) -------------------------------------------

    public static readonly RuleDescriptor TypeChanged = new("GW020", nameof(TypeChanged), Severity.Breaking,
        "A type changed incompatibly. Requests: the server stopped accepting a type it accepted. Responses: the server may now return a type consumers do not expect. (integer to number widening is request-safe only.)");

    public static readonly RuleDescriptor FormatChanged = new("GW021", nameof(FormatChanged), Severity.Breaking,
        "A format changed incompatibly (for example int32 to int64 in a response, or date to date-time in either direction).");

    public static readonly RuleDescriptor NullabilityChanged = new("GW022", nameof(NullabilityChanged), Severity.Breaking,
        "Requests: a nullable value became non-nullable. Responses: a non-nullable value became nullable.");

    public static readonly RuleDescriptor EnumValueRemoved = new("GW023", nameof(EnumValueRemoved), Severity.Breaking,
        "Request enum: an accepted value was removed, or an enum constraint was introduced on a previously free value.");

    public static readonly RuleDescriptor EnumValueAdded = new("GW024", nameof(EnumValueAdded), Severity.Warning,
        "Response enum: a new value can be returned (or the enum constraint was dropped). Consumers with exhaustive handling or strict enum deserialization may fail.");

    public static readonly RuleDescriptor RequiredPropertyRemoved = new("GW025", nameof(RequiredPropertyRemoved), Severity.Breaking,
        "Response: a property that was always present was removed.");

    public static readonly RuleDescriptor PropertyRemoved = new("GW026", nameof(PropertyRemoved), Severity.Warning,
        "A property was removed (an optional response property, or any request property).");

    public static readonly RuleDescriptor RequiredPropertyAdded = new("GW027", nameof(RequiredPropertyAdded), Severity.Breaking,
        "Request: a new required property was added; existing callers do not send it.");

    public static readonly RuleDescriptor PropertyBecameRequired = new("GW028", nameof(PropertyBecameRequired), Severity.Breaking,
        "Request: an optional property became required.");

    public static readonly RuleDescriptor PropertyBecameOptional = new("GW029", nameof(PropertyBecameOptional), Severity.Breaking,
        "Response: a property that was always present may now be missing.");

    public static readonly RuleDescriptor VariantRemoved = new("GW030", nameof(VariantRemoved), Severity.Breaking,
        "Request: a oneOf/anyOf alternative that was accepted is no longer accepted.");

    public static readonly RuleDescriptor VariantAdded = new("GW031", nameof(VariantAdded), Severity.Warning,
        "Response: a new oneOf/anyOf alternative can be returned that consumers may not handle.");

    // --- Bodies and responses -----------------------------------------------------------------------------------

    public static readonly RuleDescriptor RequiredRequestBodyAdded = new("GW040", nameof(RequiredRequestBodyAdded), Severity.Breaking,
        "A required request body was added to an operation that had none.");

    public static readonly RuleDescriptor RequestBodyBecameRequired = new("GW041", nameof(RequestBodyBecameRequired), Severity.Breaking,
        "An optional request body became required.");

    public static readonly RuleDescriptor RequestBodyRemoved = new("GW042", nameof(RequestBodyRemoved), Severity.Warning,
        "The request body was removed. Callers that still send one may be rejected or ignored.");

    public static readonly RuleDescriptor MediaTypeRemoved = new("GW043", nameof(MediaTypeRemoved), Severity.Breaking,
        "A media type that consumers could send or receive is no longer supported.");

    public static readonly RuleDescriptor SuccessResponseRemoved = new("GW044", nameof(SuccessResponseRemoved), Severity.Breaking,
        "A success (2xx) response was removed.");

    public static readonly RuleDescriptor ResponseRemoved = new("GW045", nameof(ResponseRemoved), Severity.Info,
        "A non-success response was removed from the contract.");

    // --- Security -----------------------------------------------------------------------------------------------

    public static readonly RuleDescriptor SecurityAdded = new("GW050", nameof(SecurityAdded), Severity.Breaking,
        "An operation that allowed anonymous access now requires authentication.");

    public static readonly RuleDescriptor SecurityRequirementRemoved = new("GW051", nameof(SecurityRequirementRemoved), Severity.Warning,
        "A previously accepted security alternative (scheme or scope set) is no longer accepted.");

    public static readonly RuleDescriptor SecurityRequirementsReplaced = new("GW052", nameof(SecurityRequirementsReplaced), Severity.Breaking,
        "None of the previously accepted security alternatives is accepted any more, so existing callers can no longer authenticate.");

    /// <summary>All rules, ordered by id.</summary>
    public static IReadOnlyList<RuleDescriptor> All { get; } =
        typeof(RuleCatalog)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(RuleDescriptor))
            .Select(f => (RuleDescriptor)f.GetValue(null)!)
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
}
