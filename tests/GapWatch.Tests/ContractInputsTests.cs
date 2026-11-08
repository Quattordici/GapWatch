using System.Text;
using GapWatch.Cli;
using GapWatch.Core;
using Shouldly;

namespace GapWatch.Tests;

/// <summary>The input rules of <c>compare</c>, through in-memory trees: no file system and no git.</summary>
public sealed class ContractInputsTests
{
    /// <summary>A source tree held in memory: paths with <c>/</c> separators, labelled with a prefix such as <c>rev:</c>.</summary>
    private sealed class MemoryTree(string labelPrefix, params (string Path, string Text)[] files) : ISourceTree
    {
        public EntryKind Kind(string path) =>
            files.Any(f => f.Path == path) ? EntryKind.File
            : files.Any(f => f.Path.StartsWith(path + "/", StringComparison.Ordinal)) ? EntryKind.Folder
            : EntryKind.Missing;

        public SourceFile Read(string path) => ToFile(files.Single(f => f.Path == path), path);

        public IReadOnlyList<SourceFile> ReadFolder(string folder, bool recursive, Func<string, bool> include) =>
            files
                .Where(f => f.Path.StartsWith(folder + "/", StringComparison.Ordinal))
                .Select(f => (File: f, Relative: f.Path[(folder.Length + 1)..]))
                .Where(f => (recursive || !f.Relative.Contains('/', StringComparison.Ordinal)) && include(f.Relative))
                .OrderBy(f => f.Relative, StringComparer.Ordinal)
                .Select(f => ToFile(f.File, f.File.Path))
                .ToList();

        private SourceFile ToFile((string Path, string Text) file, string path) =>
            new(labelPrefix + path, path[(path.LastIndexOf('/') + 1)..], Encoding.UTF8.GetBytes(file.Text));
    }

    private static string Version(string version, params string[] paths) =>
        TestSpec.Document(
            "{ " + string.Join(", ", paths.Select(p => $$"""
                "{{p}}": { "get": { "responses": { "204": { "description": "d" } } } }
                """)) + " }")
            .Replace("\"version\": \"1\"", $"\"version\": \"{version}\"", StringComparison.Ordinal);

    private static ComparisonInputs Load(ISourceTree baseTree, ISourceTree headTree, string path = "api", InputKind kind = InputKind.OpenApi) =>
        ContractInputs.Load(baseTree, path, headTree, path, kind);

    [Test]
    public void Two_files_are_read_with_the_reader_their_extension_picks()
    {
        var inputs = Load(
            new MemoryTree("rev:", ("api.json", TestSpec.Operation("get", "/pets"))),
            new MemoryTree("", ("api.json", TestSpec.Operation("get", "/owners"))),
            path: "api.json");

        inputs.Base.Operations.ShouldHaveSingleItem().Display.ShouldBe("GET /pets");
        inputs.Head.Operations.ShouldHaveSingleItem().Display.ShouldBe("GET /owners");
        inputs.FallbackHeadDocument.ShouldBeNull();
    }

    [Test]
    public void Folders_pair_documents_by_info_version_and_skip_other_files()
    {
        var inputs = Load(
            new MemoryTree("rev:", ("api/a.json", Version("1.0", "/pets")), ("api/b.yaml", Version("2.0", "/pets")), ("api/readme.md", "#")),
            new MemoryTree("", ("api/x.json", Version("2.0", "/pets")), ("api/nested/y.json", Version("3.0", "/pets"))));

        string.Join(", ", inputs.Base.Operations.Select(o => $"{o.Display} {o.Document}")).ShouldBe("GET /pets [1.0] rev:api/a.json, GET /pets [2.0] rev:api/b.yaml");
        inputs.Head.Operations.ShouldHaveSingleItem().Display.ShouldBe("GET /pets [2.0]");
        inputs.FallbackHeadDocument.ShouldBe("api/x.json");
    }

    [Test]
    public void Folders_skip_json_and_yaml_files_that_are_not_openapi_documents()
    {
        // The usual ASP.NET Core project folder: a build-time document next to appsettings.json (with a byte order
        // mark and a comment, as Visual Studio writes it) and a YAML file of something else.
        const string AppSettings = "\uFEFF{\n  // logging\n  \"Logging\": { \"LogLevel\": { \"Default\": \"Information\" } },\n}";
        var tree = new MemoryTree(
            "",
            ("api/Api.json", Version("1.0", "/pets")),
            ("api/appsettings.json", AppSettings),
            ("api/docker.yml", "services:\n  api:\n    image: x\n"));

        var inputs = Load(tree, tree);

        inputs.Head.Operations.ShouldHaveSingleItem().Display.ShouldBe("GET /pets [1.0]");
        string.Join(", ", inputs.Skipped).ShouldBe("api/appsettings.json, api/docker.yml, api/appsettings.json, api/docker.yml");
    }

    [Test]
    public void A_document_that_cannot_be_read_is_named_in_the_error()
    {
        var broken = new MemoryTree("rev:", ("api/v1.json", Version("1.0", "/pets")), ("api/v2.json", """{ "openapi": "3.0.1", "paths": """));
        var good = new MemoryTree("", ("api/v1.json", Version("1.0", "/pets")));

        Should.Throw<ContractReadException>(() => Load(broken, good)).Message.ShouldStartWith("rev:api/v2.json: ");
    }

    [Test]
    public void A_file_on_one_side_and_a_folder_on_the_other_is_an_input_error()
    {
        var file = new MemoryTree("", ("api", TestSpec.Operation("get", "/pets")));
        var folder = new MemoryTree("", ("api/v1.json", Version("1", "/pets")));

        Should.Throw<InputException>(() => Load(file, folder)).Message.ShouldContain("not one of each");
    }

    [Test]
    public void A_missing_path_or_a_folder_without_documents_cannot_be_read()
    {
        var empty = new MemoryTree("", ("api/readme.md", "#"));

        Should.Throw<ContractReadException>(() => Load(new MemoryTree(""), empty)).Message.ShouldBe("File not found: api");
        Should.Throw<ContractReadException>(() => Load(empty, empty)).Message.ShouldContain("No OpenAPI documents");
    }

    [Test]
    public void Csharp_input_reads_source_files_below_the_folder_except_build_output()
    {
        const string Controller = """
            [ApiController, Route("pets")]
            public sealed class PetsController : ControllerBase { [HttpGet] public IActionResult List() => Ok(); }
            """;
        var tree = new MemoryTree("rev:", ("src/Api/PetsController.cs", Controller), ("src/bin/Generated.cs", "this is not C#"), ("src/notes.txt", "x"));

        var inputs = Load(tree, tree, path: "src", kind: InputKind.CSharp);

        var operation = inputs.Base.Operations.ShouldHaveSingleItem();
        operation.Display.ShouldBe("GET /pets");
        operation.Position!.File.ShouldBe("rev:src/Api/PetsController.cs");
    }

    [Test]
    public void Csharp_input_needs_folders_on_both_sides()
    {
        var file = new MemoryTree("", ("src", "class A;"));

        Should.Throw<InputException>(() => Load(file, file, path: "src", kind: InputKind.CSharp)).Message.ShouldContain("folders of C# source");
    }
}
