using GapWatch.Core.Model;

namespace GapWatch.Core.Comparison;

/// <summary>A contract read from one file of a folder, in multi-document mode.</summary>
/// <param name="Path">The file, as it is shown in reports.</param>
/// <param name="Contract">What was read from it.</param>
public sealed record NamedContract(string Path, ApiContract Contract);

/// <summary>
/// Multi-document mode: one OpenAPI document per API version (the usual Asp.Versioning setup), on both sides. Each side
/// becomes one contract whose operations carry <see cref="OperationContract.ApiVersion"/>, so the version document
/// pairs are compared together and a document only on one side shows up as a removed or added version.
/// </summary>
/// <remarks>
/// Documents pair by <c>info.version</c> when every document on both sides has one and it is unique on its side.
/// Otherwise all of them pair by file name without extension, so that one missing or duplicated <c>info.version</c>
/// cannot pair <c>v1.json</c> with <c>v2.json</c>.
/// </remarks>
public static class VersionedDocuments
{
    /// <summary>Combines the documents of each side into one contract per side.</summary>
    public static (ApiContract Baseline, ApiContract Head) Combine(
        IReadOnlyList<NamedContract> baseline, IReadOnlyList<NamedContract> head)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(head);

        var byInfoVersion = UniqueInfoVersions(baseline) && UniqueInfoVersions(head);
        return (Merge(baseline, byInfoVersion), Merge(head, byInfoVersion));
    }

    /// <summary>The label a document pairs by.</summary>
    public static string Label(NamedContract document, bool byInfoVersion)
    {
        ArgumentNullException.ThrowIfNull(document);
        return byInfoVersion ? document.Contract.Version!.Trim() : FileStem(document.Path);
    }

    private static bool UniqueInfoVersions(IReadOnlyList<NamedContract> documents) =>
        documents.All(d => !string.IsNullOrWhiteSpace(d.Contract.Version))
        && documents.Select(d => d.Contract.Version!.Trim()).Distinct(StringComparer.Ordinal).Count() == documents.Count;

    private static ApiContract Merge(IReadOnlyList<NamedContract> documents, bool byInfoVersion)
    {
        var operations = documents
            .SelectMany(d => d.Contract.Operations.Select(o => o with { ApiVersion = Label(d, byInfoVersion), Document = d.Path }))
            .ToList();
        return new ApiContract(documents.FirstOrDefault()?.Contract.Title, null, operations);
    }

    private static string FileStem(string path)
    {
        var name = path.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
