using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Matchmark.Generator.Tests;

/// <summary>
/// The endpoints as a pure function of a shape (docs/reference/model.md, its shape in the pipeline): emitted from a
/// hand-built <see cref="TagShape"/> with no spec and no Roslyn, and a tag's shape, read from a small spec, holding what
/// its endpoints bind and check.
/// </summary>
[TestFixture]
public sealed class EndpointShapeTests
{
    private const string Pets = "global::System.Collections.Generic.List<Pet>";

    private static TagShape Shelter(string? maximum) => new("Shelter", "Shelter.Api", "Pets", new[]
    {
        new OperationShape(
            "listPets",
            "GET",
            "/pets",
            "pets",
            new[]
            {
                new ParameterShape("Query", "limit", "int", false, EquatableArray<ConstraintShape>.Empty, "limit", false, false, false, false,
                    new BoundsShape(null, maximum, null, null, null, null)),
            }.ToEquatable(),
            null,
            new[] { new OutcomeShape(200, null, Pets, "application/json", true, false, false, null) }.ToEquatable(),
            EquatableArray<string>.Empty,
            EquatableArray<string>.Empty),
    }.ToEquatable());

    [Test]
    public void An_endpoint_is_emitted_from_a_hand_built_shape_alone()
    {
        var code = Emitter.EmitTagEndpoints(Shelter("100"));

        code.Should().Contain("public static IEndpointRouteBuilder MapShelterPets(this IEndpointRouteBuilder app)")
            .And.Contain("app.MapMethods(\"/pets\", new[] { \"GET\" }")
            .And.Contain("[FromQuery(Name = \"limit\")] int? limit")
            .And.Contain("[FromServices] IPetsHandlers __handlers")
            .And.Contain("> 100) __invalid[\"limit\"] = new[] { \"Must be at most 100.\" };")
            .And.Contain($"Results<Ok<{Pets}>, ValidationProblem>")
            .And.Contain(".WithName(\"listPets\")");
        Emitter.EmitTagEndpoints(Shelter(null)).Should().NotContain("__invalid", "without a bound there's nothing to check")
            .And.Contain($"Task<Ok<{Pets}>>");
    }

    [Test]
    public void Equal_shapes_compare_equal_and_emit_the_same_code()
    {
        Shelter("100").Should().Be(Shelter("100"));
        Shelter("100").Should().NotBe(Shelter("50"));
        Emitter.EmitTagEndpoints(Shelter("100")).Should().Be(Emitter.EmitTagEndpoints(Shelter("100")));
    }

    private const string Spec = """
        openapi: 3.1.1
        info: { title: Shelter, version: 1.0.0 }
        paths:
          /pets:
            post:
              operationId: createPet
              tags: [pets]
              parameters:
                - { name: limit, in: query, schema: { type: integer, maximum: 100 } }
                - { name: kind, in: query, required: true, schema: { $ref: '#/components/schemas/Kind' } }
              requestBody: { required: true, content: { application/json: { schema: { $ref: '#/components/schemas/NewPet' } } } }
              responses:
                '201': { description: Created, content: { application/json: { schema: { $ref: '#/components/schemas/NewPet' } } } }
                '400': { description: Invalid, content: { application/problem+json: { schema: { $ref: '#/components/schemas/HttpValidationProblemDetails' } } } }
                '409': { description: Taken }
        components:
          schemas:
            Kind: { type: string, enum: [cat, dog] }
            NewPet: { type: object, required: [name], properties: { name: { type: string } } }
            HttpValidationProblemDetails: { type: object }
        """;

    [Test]
    public void A_tag_s_shape_holds_what_its_endpoints_bind_and_check()
    {
        var document = SpecReader.Read(Spec, "/specs/shelter.yaml", out var problems);
        document.Should().NotBeNull(string.Join("; ", problems));
        var shape = new Emitter(document!, "Shelter.Api", "Shelter", Location.None).TagShape("Pets");

        shape.Should().Be(new Emitter(SpecReader.Read(Spec, "/specs/shelter.yaml", out _)!, "Shelter.Api", "Shelter", Location.None).TagShape("Pets"),
            "the same spec read twice is the same shape");
        var operation = shape.Operations.Should().ContainSingle().Subject;
        operation.Parameters.Should().HaveCount(2);
        var limit = operation.Parameters.Single(p => p.Name == "limit");
        (limit.Source, limit.Identifier, limit.BoundType, limit.Enum).Should().Be(("Query", "limit", "int?", false));
        limit.Bounds.Should().Be(new BoundsShape(null, "100", null, null, null, null));
        var kind = operation.Parameters.Single(p => p.Name == "kind");
        (kind.Enum, kind.Required, kind.Type).Should().Be((true, true, "Kind"));
        operation.Body.Should().NotBeNull();
        (operation.Body!.Type, operation.Body.Required, operation.Body.Validated, operation.Body.MediaType).Should().Be(("NewPet", true, true, "application/json"));
        operation.Outcomes.Select(o => (o.Status, o.Success, o.IsValidation)).Should().Equal((201, true, false), (400, false, true), (409, false, false));
    }
}
