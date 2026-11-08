namespace GapWatch.Core.Findings;

/// <summary>
/// Where inside an operation a finding is, as structured data. <see cref="ToString"/> is the only place the location
/// strings are built: suppressions, baselines and SARIF fingerprints are built from those strings, so their format is
/// part of the public contract (<c>docs/compatibility.md</c>) and <c>PublicContractTests</c> pins every shape.
/// </summary>
/// <remarks>
/// Code that needs to know which parameter, media type or status a finding is about (the version analyzer, the code
/// map) reads <c>Finding.At</c> instead of parsing <c>Finding.Location</c>.
/// </remarks>
public abstract record FindingLocation
{
    /// <summary>The path of the schema root, <c>$</c>.</summary>
    public const string Root = "$";

    private FindingLocation()
    {
    }

    /// <summary>The stable location string, e.g. <c>response 200 application/json: $.name</c>.</summary>
    public abstract override string ToString();

    private static string WithSchemaPath(string prefix, string schemaPath) =>
        schemaPath == Root ? prefix : $"{prefix}: {schemaPath}";

    /// <summary>The operation as a whole: <c>operation</c>.</summary>
    public sealed record Operation : FindingLocation
    {
        /// <summary>The single instance.</summary>
        public static Operation Instance { get; } = new();

        /// <inheritdoc />
        public override string ToString() => "operation";
    }

    /// <summary>A parameter or a schema inside it: <c>parameter 'id' (path)</c>, <c>parameter 'q' (query): $[]</c>.</summary>
    /// <param name="Name">Parameter name as declared.</param>
    /// <param name="In">One of <c>path</c>, <c>query</c>, <c>header</c>, <c>cookie</c>.</param>
    /// <param name="SchemaPath">Path inside the parameter's schema; <see cref="Root"/> for the parameter itself.</param>
    public sealed record Parameter(string Name, string In, string SchemaPath = Root) : FindingLocation
    {
        /// <inheritdoc />
        public override string ToString() => WithSchemaPath($"parameter '{Name}' ({In})", SchemaPath);
    }

    /// <summary>The request body as a whole: <c>request body</c>.</summary>
    public sealed record RequestBody : FindingLocation
    {
        /// <summary>The single instance.</summary>
        public static RequestBody Instance { get; } = new();

        /// <inheritdoc />
        public override string ToString() => "request body";
    }

    /// <summary>One media type of the request body, or a schema inside it: <c>request body (application/json): $.name</c>.</summary>
    public sealed record RequestContent(string MediaType, string SchemaPath = Root) : FindingLocation
    {
        /// <inheritdoc />
        public override string ToString() => WithSchemaPath($"request body ({MediaType})", SchemaPath);
    }

    /// <summary>A response as a whole: <c>response 200</c>.</summary>
    public sealed record Response(string Status) : FindingLocation
    {
        /// <inheritdoc />
        public override string ToString() => $"response {Status}";
    }

    /// <summary>
    /// A media type a response no longer produces. The string is <c>response 200</c>, without the media type, as it has
    /// been since 0.1; the media type is only available here.
    /// </summary>
    public sealed record ResponseMediaType(string Status, string MediaType) : FindingLocation
    {
        /// <inheritdoc />
        public override string ToString() => $"response {Status}";
    }

    /// <summary>A schema in one media type of a response: <c>response 200 application/json: $.name</c>.</summary>
    public sealed record ResponseContent(string Status, string MediaType, string SchemaPath = Root) : FindingLocation
    {
        /// <inheritdoc />
        public override string ToString() => WithSchemaPath($"response {Status} {MediaType}", SchemaPath);
    }

    /// <summary>The operation's security requirements: <c>security</c>.</summary>
    public sealed record Security : FindingLocation
    {
        /// <summary>The single instance.</summary>
        public static Security Instance { get; } = new();

        /// <inheritdoc />
        public override string ToString() => "security";
    }
}
