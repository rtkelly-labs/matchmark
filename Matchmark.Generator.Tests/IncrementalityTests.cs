using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;

namespace Matchmark.Generator.Tests;

/// <summary>
/// What an edit makes the generator do again, from Roslyn's step tracking on the named steps (TrackingNames): an edit to
/// C# that isn't a spec re-runs nothing that matters, a spec reformatted without a change re-emits no tag, and an edit to
/// one tag re-emits only that tag. Each test also checks what it examined, so an empty run can't pass.
/// </summary>
[TestFixture]
public sealed class IncrementalityTests
{
    private static string Spec(string petsSummary = "all the pets", int petsLimit = 100) => $$"""
        openapi: 3.1.1
        info: { title: T, version: 1.0.0 }
        paths:
          /pets:
            get: { operationId: listPets, tags: [pets], summary: '{{petsSummary}}', parameters: [{ name: limit, in: query, schema: { type: integer, maximum: {{petsLimit}} } }], responses: { '204': { description: none } } }
          /stores:
            get: { operationId: listStores, tags: [stores], responses: { '204': { description: none } } }
        """;

    private static GeneratorDriver Driver(AdditionalText spec, string parts = "Contract,Server")
        => CSharpGeneratorDriver.Create([new OpenApiSourceGenerator().AsSourceGenerator()], [spec], optionsProvider: new Options(parts),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    private static IReadOnlyList<IncrementalStepRunReason> Reasons(GeneratorDriver driver, string step)
        => driver.GetRunResult().Results.Single().TrackedSteps.TryGetValue(step, out var runs)
            ? runs.SelectMany(r => r.Outputs).Select(o => o.Reason).ToList()
            : [];

    /// <summary>The reason each added source was produced on the last run, by hint name.</summary>
    private static Dictionary<string, IncrementalStepRunReason> Outputs(GeneratorDriver driver)
    {
        var outputs = new Dictionary<string, IncrementalStepRunReason>(StringComparer.Ordinal);
        foreach (var (value, reason) in driver.GetRunResult().Results.Single().TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs))
        {
            if (value.GetType().GetField("Item1")?.GetValue(value) is System.Collections.IEnumerable sources)
            {
                foreach (var source in sources)
                {
                    var hint = (string)source.GetType().GetProperty("HintName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.GetValue(source)!;
                    outputs[hint] = reason;
                }
            }
        }

        return outputs;
    }

    private static bool Reused(IncrementalStepRunReason reason) => reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged;

    [Test]
    public void An_edit_to_CSharp_that_isn_t_a_spec_re_runs_nothing()
    {
        var spec = new Text("/specs/t.yaml", Spec());
        var compilation = CSharpCompilation.Create("T", [CSharpSyntaxTree.ParseText("class A { }")]);
        var driver = Driver(spec).RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class B { }")));

        Reasons(driver, TrackingNames.Specs).Should().NotBeEmpty().And.OnlyContain(r => Reused(r));
        Reasons(driver, TrackingNames.TagSlices).Should().HaveCount(2).And.OnlyContain(r => Reused(r));
        Reasons(driver, TrackingNames.References).Should().OnlyContain(r => Reused(r), "the referenced libraries didn't change");
        Outputs(driver).Should().HaveCountGreaterThan(2).And.OnlyContain(o => Reused(o.Value), "nothing a spec generates depends on class B");
    }

    [Test]
    public void A_spec_reformatted_without_a_change_re_emits_no_tag()
    {
        var before = new Text("/specs/t.yaml", Spec());
        var compilation = CSharpCompilation.Create("T");
        var driver = Driver(before).RunGenerators(compilation);
        // The same document with a comment and blank lines: new text, the same slices.
        driver = driver.ReplaceAdditionalText(before, new Text("/specs/t.yaml", "# Pets and stores.\n" + Spec() + "\n\n")).RunGenerators(compilation);

        Reasons(driver, TrackingNames.Specs).Should().Contain(IncrementalStepRunReason.Modified, "the text changed");
        Reasons(driver, TrackingNames.TagSlices).Should().HaveCount(2).And.OnlyContain(r => Reused(r), "the document didn't");
        var outputs = Outputs(driver);
        outputs.Should().ContainKeys("T.Endpoints.Pets.g.cs", "T.Endpoints.Stores.g.cs");
        Reused(outputs["T.Endpoints.Pets.g.cs"]).Should().BeTrue();
        Reused(outputs["T.Endpoints.Stores.g.cs"]).Should().BeTrue();
    }

    [TestCase(50)]
    [TestCase(1000)]
    public void An_edit_to_one_tag_re_emits_only_that_tag(int limit)
    {
        var before = new Text("/specs/t.yaml", Spec());
        var compilation = CSharpCompilation.Create("T");
        var driver = Driver(before).RunGenerators(compilation);
        driver = driver.ReplaceAdditionalText(before, new Text("/specs/t.yaml", Spec(petsLimit: limit))).RunGenerators(compilation);

        Reasons(driver, TrackingNames.TagSlices).Count(r => r == IncrementalStepRunReason.Modified).Should().Be(1);
        Reasons(driver, TrackingNames.TagSlices).Count(r => Reused(r)).Should().Be(1);
        Reasons(driver, TrackingNames.TagShapes).Count(r => r == IncrementalStepRunReason.Modified).Should().Be(1, "the endpoint checks the new maximum");
        var outputs = Outputs(driver);
        Reused(outputs["T.Endpoints.Stores.g.cs"]).Should().BeTrue();
        Reused(outputs["T.Endpoints.Pets.g.cs"]).Should().BeFalse();
    }

    [TestCase("every pet")]
    [TestCase("all the pets, and more")]
    public void An_edit_to_a_tag_its_endpoints_don_t_read_stops_at_its_shape(string summary)
    {
        var before = new Text("/specs/t.yaml", Spec());
        var compilation = CSharpCompilation.Create("T");
        var driver = Driver(before).RunGenerators(compilation);
        // A summary is documentation: the pets slice changes, and is read again to the same shape.
        driver = driver.ReplaceAdditionalText(before, new Text("/specs/t.yaml", Spec(summary))).RunGenerators(compilation);

        Reasons(driver, TrackingNames.TagSlices).Count(r => r == IncrementalStepRunReason.Modified).Should().Be(1);
        Reasons(driver, TrackingNames.TagShapes).Should().HaveCount(2).And.OnlyContain(r => Reused(r))
            .And.Contain(IncrementalStepRunReason.Unchanged, "the pets slice was read again, to the same shape");
        var outputs = Outputs(driver);
        Reused(outputs["T.Endpoints.Stores.g.cs"]).Should().BeTrue();
        Reused(outputs["T.Endpoints.Pets.g.cs"]).Should().BeTrue("its shape is equal");
    }

    private static string WithComponents(int storeNameLength = 50, string storeCity = "") => $$"""
        openapi: 3.1.1
        info: { title: T, version: 1.0.0 }
        paths:
          /pets:
            get: { operationId: listPets, tags: [pets], responses: { '200': { description: ok, content: { application/json: { schema: { $ref: '#/components/schemas/Pet' } } } } } }
          /stores:
            get: { operationId: listStores, tags: [stores], responses: { '200': { description: ok, content: { application/json: { schema: { $ref: '#/components/schemas/Store' } } } } } }
        components:
          schemas:
            Pet: { type: object, properties: { name: { type: string } } }
            Store: { type: object, properties: { name: { type: string, maxLength: {{storeNameLength}} }{{storeCity}} } }
        """;

    [TestCase(80, "")]
    [TestCase(50, ", city: { type: string }")]
    public void An_edit_to_a_component_reaches_only_the_tags_that_use_it_and_re_emits_the_models_not_their_endpoints(int nameLength, string city)
    {
        // Each tag's slice holds the components it reaches (model.md: a TagShape and the component names it uses), and
        // its endpoints read a component by name alone: a bound or a property added changes the models, not the endpoints.
        var before = new Text("/specs/t.yaml", WithComponents());
        var compilation = CSharpCompilation.Create("T");
        var driver = Driver(before).RunGenerators(compilation);
        driver = driver.ReplaceAdditionalText(before, new Text("/specs/t.yaml", WithComponents(nameLength, city))).RunGenerators(compilation);

        Reasons(driver, TrackingNames.TagSlices).Should().HaveCount(2);
        Reasons(driver, TrackingNames.TagSlices).Count(r => r == IncrementalStepRunReason.Modified).Should().Be(1, "only stores reaches Store");
        Reasons(driver, TrackingNames.TagShapes).Should().HaveCount(2).And.OnlyContain(r => Reused(r))
            .And.Contain(IncrementalStepRunReason.Unchanged, "the stores slice was read again, to the same shape");
        var outputs = Outputs(driver);
        Reused(outputs["T.Endpoints.Pets.g.cs"]).Should().BeTrue("pets doesn't use Store");
        Reused(outputs["T.Endpoints.Stores.g.cs"]).Should().BeTrue("the stores endpoints read Store by name alone");
        outputs.Should().ContainKey("T.Models.g.cs").WhoseValue.Should().Be(IncrementalStepRunReason.Modified, "Store's model changed");
    }

    private const string Handlers = """
        [Microsoft.AspNetCore.Mvc.ApiController]
        [Microsoft.AspNetCore.Mvc.Route("pets")]
        public sealed class PetsController : Microsoft.AspNetCore.Mvc.ControllerBase
        {
            [Microsoft.AspNetCore.Mvc.HttpGet(Name = "listPets")]
            [Microsoft.AspNetCore.Mvc.ProducesResponseType(204)]
            public void List() { }
        }
        """;

    private const string Stores = """
        [Microsoft.AspNetCore.Mvc.ApiController]
        [Microsoft.AspNetCore.Mvc.Route("stores")]
        public sealed class StoresController : Microsoft.AspNetCore.Mvc.ControllerBase
        {
            [Microsoft.AspNetCore.Mvc.HttpGet(Name = "listStores")]
            [Microsoft.AspNetCore.Mvc.ProducesResponseType(204)]
            public void List() { }
        }
        """;

    private static CSharpCompilation WithAspNet(params string[] sources)
        => CSharpCompilation.Create("T", sources.Prepend(CoverageTests.AspNet).Select(s => CSharpSyntaxTree.ParseText(s)),
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    [Test]
    public void Verify_reads_the_parsed_shape_so_an_edit_to_an_endpoint_doesn_t_parse_the_spec_again()
    {
        var spec = new Text("/specs/t.yaml", Spec());
        var compilation = WithAspNet(Handlers);
        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty("the test code should compile");
        var driver = Driver(spec, "Verify").RunGenerators(compilation);
        Reasons(driver, TrackingNames.Shapes).Should().ContainSingle("the spec was parsed once");
        var missing = driver.GetRunResult().Diagnostics.Where(d => d.Id == "MM010").Select(d => d.GetMessage()).ToList();
        missing.Should().ContainSingle().Which.Should().Contain("listStores", "listPets has its endpoint, listStores hasn't: Verify ran on the shape");

        // An endpoint added: the comparison runs again, the parse doesn't.
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(Stores)));
        Reasons(driver, TrackingNames.Shapes).Should().ContainSingle().And.OnlyContain(r => Reused(r), "the spec didn't change");
        driver.GetRunResult().Diagnostics.Should().NotContain(d => d.Id == "MM010", "listStores is implemented now");
    }

    [Test]
    public void A_spec_edit_that_leaves_its_shape_equal_stops_at_the_parse_step()
    {
        var before = new Text("/specs/t.yaml", Spec());
        var compilation = WithAspNet(Handlers);
        var driver = Driver(before, "Verify").RunGenerators(compilation);
        // A summary is documentation: the shape Verify reads doesn't hold it.
        driver = driver.ReplaceAdditionalText(before, new Text("/specs/t.yaml", Spec("every pet"))).RunGenerators(compilation);

        Reasons(driver, TrackingNames.Specs).Should().Contain(IncrementalStepRunReason.Modified, "the text changed");
        Reasons(driver, TrackingNames.Shapes).Should().ContainSingle().And.OnlyContain(r => r == IncrementalStepRunReason.Unchanged, "parsed again, to the same shape");
    }

    [Test]
    public void Running_twice_on_the_same_input_gives_the_same_bytes()
    {
        // Generated code is commit-deterministic evidence (Diffra): the same input must give the same output, every time.
        string Digest() => string.Join("\n", CSharpGeneratorDriver.Create([new OpenApiSourceGenerator().AsSourceGenerator()], [new Text("/specs/t.yaml", Spec())], optionsProvider: new Options())
            .RunGenerators(CSharpCompilation.Create("T")).GetRunResult().Results.Single().GeneratedSources.OrderBy(s => s.HintName, StringComparer.Ordinal)
            .Select(s => s.HintName + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s.SourceText.ToString())))));
        var first = Digest();
        first.Split('\n').Should().HaveCountGreaterThan(2);
        Digest().Should().Be(first);
    }

    private sealed class Text(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private sealed class Options(string parts = "Contract,Server") : AnalyzerConfigOptionsProvider
    {
        private readonly Values _spec = new(new Dictionary<string, string>
        {
            ["build_metadata.AdditionalFiles.Matchmark"] = "true",
            ["build_metadata.AdditionalFiles.MatchmarkApiName"] = "T",
            ["build_metadata.AdditionalFiles.MatchmarkNamespace"] = "T.Api",
            ["build_metadata.AdditionalFiles.MatchmarkParts"] = parts,
        });

        public override AnalyzerConfigOptions GlobalOptions { get; } = new Values([]);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _spec;

        private sealed class Values(Dictionary<string, string> values) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
        }
    }
}
