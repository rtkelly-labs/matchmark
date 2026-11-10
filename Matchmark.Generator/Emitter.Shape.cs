using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Matchmark.Generator
{
    /// <summary>The spec as an <see cref="ApiShape"/>: the parse step's output, made from the same decisions as every output.</summary>
    internal sealed partial class Emitter
    {
        public ApiShape Shape(string path)
        {
            CollectSchemas();
            var operations = CollectOperations().Select(OperationShapeOf);

            // The framework's problem types stand for their schemas whether or not the spec defines them as components.
            var wellKnown = WellKnownSchemas.Keys.Where(id => !_schemas.ContainsKey(id)).Select(id => new ComponentShape(
                id, Generated(id), true, ComponentShape.ObjectKind, EquatableArray<string>.Empty, null, EquatableArray<UnionMemberShape>.Empty, EquatableArray<PropertyShape>.Empty, null));
            var components = _schemas.Select(s => ComponentShapeOf(s.Key, s.Value)).Concat(wellKnown).OrderBy(c => c.Id, StringComparer.Ordinal);
            return new ApiShape(_apiName, _namespace, path, operations.ToEquatable(), components.ToEquatable());
        }

        /// <summary>A tag's slice of the spec as the shape its endpoints are emitted from, filed under <paramref name="name"/>.</summary>
        public TagShape TagShape(string name)
        {
            CollectSchemas();
            return new TagShape(_apiName, _namespace, name, CollectOperations().Select(OperationShapeOf).ToEquatable());
        }

        private OperationShape OperationShapeOf(Operation o) => new(
            o.Id,
            o.Method,
            o.Path,
            o.Tag,
            o.Parameters.Select(ParameterShapeOf).ToEquatable(),
            o.Body is { } body ? new BodyShape(body.Type, body.MediaType, o.RequiredBody, body.Enum, SchemaShapeOf(body.Schema), BoundsOf(body.Schema)) : null,
            o.Successes.Select(r => r.Outcome(true, r.Schema is { } s ? SchemaShapeOf(s) : null))
                .Concat(o.Errors.Select(r => r.Outcome(false, r.Schema is { } s ? SchemaShapeOf(s) : null))).ToEquatable(),
            o.Dependencies.ToEquatable(),
            o.ResponseHeaders.ToEquatable());

        /// <summary>A parameter as its shape: what Verify compares, and what an endpoint, a client or a controller reads and checks.</summary>
        private ParameterShape ParameterShapeOf(Param p) => new(
            p.Attribute.Substring(4),
            p.Name,
            p.Type.TrimEnd('?'),
            !p.Type.EndsWith("?", StringComparison.Ordinal),
            (p.Schema is null ? Array.Empty<(string, string)>() : Constraints.Of(p.Schema)).Select(c => new ConstraintShape(c.Item1, c.Item2)).ToEquatable(),
            p.Identifier,
            p.Enum,
            p.Deep,
            p.Whole,
            _schemas.Keys.Select(Naming.Pascal).Contains(p.Type.TrimEnd('?')),
            p.Schema is { } schema ? BoundsOf(schema) : null);

        private static BoundsShape BoundsOf(IOpenApiSchema schema)
            => new(schema.Minimum, schema.Maximum, schema.MinLength, schema.MaxLength, schema.Pattern, schema.Format);

        private static SchemaShape SchemaShapeOf(IOpenApiSchema schema)
        {
            if (RefId(schema) is { } id)
            {
                return new SchemaShape(SchemaShape.RefKind, Ref: id);
            }

            if (Has(schema, JsonSchemaType.Array) && schema.Items is { } items)
            {
                return new SchemaShape(SchemaShape.ArrayKind, Items: SchemaShapeOf(items));
            }

            return Canonical(schema) is { } primitive ? new SchemaShape(SchemaShape.PrimitiveKind, Primitive: primitive) : new SchemaShape(SchemaShape.OtherKind);
        }

        private ComponentShape ComponentShapeOf(string id, IOpenApiSchema schema)
        {
            var wellKnown = WellKnownSchemas.ContainsKey(id);
            var kind = IsEnum(schema) ? ComponentShape.EnumKind : IsUnion(schema) ? ComponentShape.UnionKind : ComponentShape.ObjectKind;
            var values = kind == ComponentShape.EnumKind
                ? schema.Enum!.Select(v => v is JsonValue value && value.TryGetValue<string>(out var text) ? text : v?.ToJsonString() ?? "null")
                : Enumerable.Empty<string>();
            var members = kind == ComponentShape.UnionKind
                ? _unionMembers.Where(m => m.Value.Base == id).Select(m => new UnionMemberShape(m.Key, m.Value.Value))
                : Enumerable.Empty<UnionMemberShape>();
            var properties = (schema.Properties ?? new Dictionary<string, IOpenApiSchema>()).Select(p => new PropertyShape(
                p.Key,
                SchemaShapeOf(p.Value),
                (RefId(p.Value) is null ? Constraints.Of(p.Value) : Array.Empty<(string, string)>()).Select(c => new ConstraintShape(c.Item1, c.Item2)).ToEquatable(),
                schema.Required?.Contains(p.Key) == true,
                Has(p.Value, JsonSchemaType.Null)));
            return new ComponentShape(
                id,
                Generated(id),
                wellKnown,
                kind,
                values.ToEquatable(),
                kind == ComponentShape.UnionKind ? schema.Discriminator?.PropertyName : null,
                members.ToEquatable(),
                properties.ToEquatable(),
                _unionMembers.TryGetValue(id, out var union) ? union.Property : null);
        }
    }
}
