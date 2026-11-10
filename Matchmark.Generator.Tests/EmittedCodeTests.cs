using AwesomeAssertions;
using NUnit.Framework;

namespace Matchmark.Generator.Tests;

/// <summary>
/// The emitted-code gate: every sample's generated code compiles with no compiler warning at any warning level, nullable
/// included, with each outcome style (plain classes, Resultful, and C# 15 unions). The baseline is empty and must stay empty: a warning a consumer would see in code
/// they didn't write is a generator bug. (A consumer's analyzers see generated code as generated, so this is the
/// compiler's own checks, which they can't opt out of.)
/// </summary>
[TestFixture]
public sealed class EmittedCodeTests
{
    public static IEnumerable<TestCaseData> Cases()
        => DerivedEvidenceTests.Samples().Where(s => s != "split").SelectMany(s => new[] { new TestCaseData(s, "plain"), new TestCaseData(s, "resultful"), new TestCaseData(s, "union") });

    [TestCaseSource(nameof(Cases))]
    public void Generated_code_compiles_without_a_warning(string sample, string outcomes)
    {
        var (spec, overlays) = DerivedEvidenceTests.Paths(sample);
        var api = char.ToUpperInvariant(sample[0]) + sample[1..];
        var (_, _, sources, warnings) = GeneratedServer.BuildWithWarnings(api, spec, overlays, string.Empty, withRules: true, resultful: outcomes == "resultful", csharp15: outcomes == "union");

        sources.Should().NotBeEmpty("the gate examined something");

        // The compiler hides nullable warnings in an auto-generated file unless the file turns them on: without this,
        // the gate would pass on code full of them.
        sources.Where(s => !s.Text.Contains("#nullable enable", StringComparison.Ordinal)).Select(s => s.Hint).Should().BeEmpty();
        warnings.Select(w => w.ToString()).Should().BeEmpty();
    }

    /// <summary>
    /// No generated switch dispatches on a tuple. Roslyn checks a tuple switch's patterns in time cubic in its arms: the
    /// fake's (operation, status) switches took most of an hour to compile for GitHub's spec. Each switch dispatches on
    /// one value, and nests where it needs a second.
    /// </summary>
    [Test]
    public void No_generated_switch_dispatches_on_a_tuple()
    {
        var examined = 0;
        var fakes = 0;
        var tuples = new List<string>();
        foreach (var sample in DerivedEvidenceTests.Samples().Where(s => s != "split"))
        {
            var (spec, overlays) = DerivedEvidenceTests.Paths(sample);
            var api = char.ToUpperInvariant(sample[0]) + sample[1..];
            var (_, _, sources, _) = GeneratedServer.BuildWithWarnings(api, spec, overlays, string.Empty, withRules: true);
            fakes += sources.Count(s => s.Hint.EndsWith(".Fake.g.cs", StringComparison.Ordinal));
            foreach (var (hint, text) in sources)
            {
                foreach (var node in Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes())
                {
                    var governing = node switch
                    {
                        Microsoft.CodeAnalysis.CSharp.Syntax.SwitchStatementSyntax s => s.Expression,
                        Microsoft.CodeAnalysis.CSharp.Syntax.SwitchExpressionSyntax s => s.GoverningExpression,
                        _ => null,
                    };
                    if (governing is null)
                    {
                        continue;
                    }

                    examined++;
                    if (governing is Microsoft.CodeAnalysis.CSharp.Syntax.TupleExpressionSyntax)
                    {
                        tuples.Add($"{sample}: {hint}: switch ({governing})");
                    }
                }
            }
        }

        TestContext.Out.WriteLine($"examined {examined} switches, {fakes} fakes");
        fakes.Should().BeGreaterThan(0, "the fake, which dispatches on an operation and a status, was examined");
        examined.Should().BeGreaterThan(fakes);
        tuples.Should().BeEmpty();
    }
}
