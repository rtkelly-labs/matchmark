using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Matchmark.Generator
{
    /// <summary>
    /// An array compared by its items, so a shape holding one is equal to another with the same items: what Roslyn's
    /// incremental pipeline needs to skip a step whose input didn't change (model.md, its shape in the pipeline).
    /// </summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T> where T : IEquatable<T>
    {
        private readonly ImmutableArray<T> _items;

        public EquatableArray(IEnumerable<T> items) => _items = items.ToImmutableArray();

        public static EquatableArray<T> Empty => new(Array.Empty<T>());

        public int Count => _items.IsDefault ? 0 : _items.Length;

        public bool Equals(EquatableArray<T> other) => this.SequenceEqual(other);

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            var hash = 17;
            foreach (var item in this)
            {
                hash = unchecked((hash * 31) + (item?.GetHashCode() ?? 0));
            }

            return hash;
        }

        public IEnumerator<T> GetEnumerator() => (_items.IsDefault ? ImmutableArray<T>.Empty : _items).AsEnumerable().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal static class EquatableArray
    {
        public static EquatableArray<T> ToEquatable<T>(this IEnumerable<T> items) where T : IEquatable<T> => new(items);
    }

    /// <summary>Where a diagnostic goes, as data: a Location holds its syntax tree, so it's never equal across edits.</summary>
    internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan Lines)
    {
        public static LocationInfo From(Location location)
            => new(location.SourceTree?.FilePath ?? location.GetLineSpan().Path, location.SourceSpan, location.GetLineSpan().Span);

        public Location ToLocation() => Location.Create(FilePath, Span, Lines);
    }

    /// <summary>A validation keyword and its value as text (<c>maxLength</c>, <c>50</c>).</summary>
    internal sealed record ConstraintShape(string Keyword, string Value);

    /// <summary>
    /// How a schema is used where it appears: a reference to a component, an array of something, a primitive (its JSON type
    /// and format, as <c>TypeShape.Primitive</c> names them), or anything else.
    /// </summary>
    internal sealed record SchemaShape(string Kind, string? Ref = null, string? Primitive = null, SchemaShape? Items = null)
    {
        public const string RefKind = "Ref";
        public const string ArrayKind = "Array";
        public const string PrimitiveKind = "Primitive";
        public const string OtherKind = "Other";

        /// <summary>"Pet", "Pet[]", "string": how the schema is named in messages.</summary>
        public string Name => Kind switch
        {
            RefKind => Ref!,
            ArrayKind => Items!.Name + "[]",
            PrimitiveKind => Primitive!,
            _ => "object",
        };
    }

    /// <summary>One property of an object component: its JSON name, its schema, and what the schema says of it.</summary>
    internal sealed record PropertyShape(string Json, SchemaShape Schema, EquatableArray<ConstraintShape> Constraints, bool Required, bool AllowsNull);

    /// <summary>A member of a union, and the discriminator value that selects it.</summary>
    internal sealed record UnionMemberShape(string Id, string Value);

    /// <summary>
    /// A component schema, decided: the C# type generated for it (or the framework type it maps to), and what it is: a
    /// string enum and its values, a discriminated union and its members, or an object and its properties.
    /// </summary>
    internal sealed record ComponentShape(
        string Id,
        string Generated,
        bool WellKnown,
        string Kind,
        EquatableArray<string> Values,
        string? Discriminator,
        EquatableArray<UnionMemberShape> Members,
        EquatableArray<PropertyShape> Properties,
        string? UnionProperty)
    {
        public const string EnumKind = "Enum";
        public const string UnionKind = "Union";
        public const string ObjectKind = "Object";
    }

    /// <summary>
    /// What an endpoint checks of a parameter or a text body, as the schema writes it: its bounds, lengths, pattern and
    /// format. Unlike <see cref="ConstraintShape"/>s, which are normalised for comparison, these are written out as read.
    /// </summary>
    internal sealed record BoundsShape(string? Minimum, string? Maximum, int? MinLength, int? MaxLength, string? Pattern, string? Format)
    {
        /// <summary>A bound, length or pattern the endpoint checks (a format alone doesn't make an operation validate).</summary>
        public bool Checked => Minimum is not null || Maximum is not null || MinLength is not null || MaxLength is not null || Pattern is not null;
    }

    /// <summary>
    /// A path, query or header parameter: where it binds from, its wire name, its C# type, its constraints, and what the
    /// endpoint does with it: its identifier, decided once, whether it's an enum read from text, a deep object read from
    /// the query (the whole query, for a querystring), one of the generated types, and the bounds it checks (null when it
    /// has no schema).
    /// </summary>
    internal sealed record ParameterShape(
        string Source,
        string Name,
        string Type,
        bool Required,
        EquatableArray<ConstraintShape> Constraints,
        string Identifier,
        bool Enum,
        bool Deep,
        bool Whole,
        bool Generated,
        BoundsShape? Bounds)
    {
        /// <summary>FromQuery, FromRoute, FromHeader.</summary>
        public string Attribute => "From" + Source;

        /// <summary>Its C# type as bound: nullable when it isn't required.</summary>
        public string BoundType => Required ? Type : Type + "?";
    }

    /// <summary>A request body: its C# type and media type, whether it's required or an enum, its schema, and a text body's bounds.</summary>
    internal sealed record BodyShape(string Type, string MediaType, bool Required, bool Enum, SchemaShape Schema, BoundsShape Bounds)
    {
        /// <summary>Sent as application/x-www-form-urlencoded, nested values in brackets (card[number]), as Stripe does.</summary>
        public bool Form => IsForm(MediaType);

        /// <summary>Text (text/plain, text/markdown...): a string, read as it is.</summary>
        public bool Text => IsText(MediaType);

        /// <summary>A generated object type, so the validation class has a Validate for it (a list or a primitive body has none).</summary>
        public bool Validated => IsValidated(Type, Enum);

        public static bool IsForm(string mediaType) => mediaType == "application/x-www-form-urlencoded";

        public static bool IsText(string mediaType) => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);

        public static bool IsValidated(string type, bool isEnum)
            => !isEnum && type.IndexOf("global::", StringComparison.Ordinal) < 0 && type is not ("string" or "int" or "long" or "float" or "double" or "decimal" or "bool");
    }

    /// <summary>
    /// A documented response, decided: its status (0 for default, -4 and -5 for the 4XX and 5XX ranges), its body's schema
    /// and C# type (null when it has none), its media type, whether it's a success, the spec's validation problem, a
    /// redirect, or a stream of items, and how it's written: the result type and the TypedResults call.
    /// </summary>
    internal sealed record OutcomeShape(int Status, SchemaShape? Schema, string? BodyType, string? MediaType, bool Success, bool IsValidation, bool IsRedirect, string? ItemType)
    {
        public string CaseName => Status switch { 0 => "Default", -4 => "ClientError", -5 => "ServerError", _ => Naming.StatusNames[Status] };

        /// <summary>default, 4XX or 5XX: the case carries the status it was answered with.</summary>
        public bool CarriesStatus => Status <= 0;

        public bool IsStream => ItemType is not null;

        /// <summary>Server-sent events: each item is an event.</summary>
        public bool IsEventStream => IsStream && MediaType == "text/event-stream";

        /// <summary>
        /// A ProblemDetails body. TypedResults.NotFound(problem) and friends write it as application/json, so it
        /// goes out through TypedResults.Problem, which writes application/problem+json.
        /// </summary>
        public bool IsProblem => BodyType is { } type && Emitter.IsWellKnownType(type);

        /// <summary>The <c>Microsoft.AspNetCore.Http.HttpResults</c> type this response is written as.</summary>
        public string ResultType => IsRedirect ? "RedirectHttpResult"
            : IsEventStream ? $"ServerSentEventsResult<{ItemType}>"
            : IsStream ? "PushStreamHttpResult"
            : Status == 401 && BodyType is null
            ? "UnauthorizedHttpResult"
            : IsValidation ? "ValidationProblem"
            : IsProblem ? "ProblemHttpResult"
            : !HasFactory ? BodyType is null ? "StatusCodeHttpResult" : $"JsonHttpResult<{BodyType}>"
            : BodyType is null ? CaseName : $"{CaseName}<{BodyType}>";

        /// <summary>A status with a TypedResults factory of its own (TypedResults.Conflict); the rest go out by number.</summary>
        public bool HasFactory => !IsStream && !CarriesStatus && !IsRedirect && (Status is 200 or 201 or 202 or 204 or 400 or 404 or 409 or 422 or 500 || Status == 401 && BodyType is null);

        /// <summary>
        /// Written by number (TypedResults.Json or StatusCode), so the result type carries no response metadata: the
        /// endpoint declares it with .Produces, as it does for problems.
        /// </summary>
        public bool NeedsProduces => !IsValidation && !IsProblem && !HasFactory && !CarriesStatus;

        /// <summary>The status a default case carries, from the expression for its body ("__case.Body" → "__case.Status").</summary>
        public static string StatusOf(string body) => (body.EndsWith(".Body", StringComparison.Ordinal) ? body.Substring(0, body.Length - 5) : body) + ".Status";

        /// <summary>The TypedResults call that produces it, given the body expression.</summary>
        public string Factory(string body) => (Status, BodyType is null) switch
        {
            _ when IsRedirect => $"TypedResults.Redirect({body}, permanent: {(Status is 301 or 308 ? "true" : "false")}, preserveMethod: {(Status is 307 or 308 ? "true" : "false")})",
            _ when CarriesStatus => BodyType is null ? $"TypedResults.StatusCode({StatusOf(body)})"
                : IsProblem ? $"Problem({body}, {StatusOf(body)})"
                : $"TypedResults.Json({body}, statusCode: {StatusOf(body)})",
            _ when IsEventStream => $"TypedResults.ServerSentEvents({body})",
            _ when IsStream => $"TypedResults.Stream(async __stream => {{ await foreach (var __item in {body}) {{ await global::System.Text.Json.JsonSerializer.SerializeAsync(__stream, __item, global::System.Text.Json.JsonSerializerOptions.Web); await __stream.WriteAsync(new byte[] {{ 10 }}); }} }}, {Naming.Literal(MediaType!)})",
            _ when IsValidation => $"TypedResults.ValidationProblem({body}.Errors, {body}.Detail, {body}.Instance, {body}.Title, {body}.Type)",
            _ when IsProblem => $"Problem({body}, {Status.Invariant()})",
            (_, true) when !HasFactory => $"TypedResults.StatusCode({Status.Invariant()})",
            (_, false) when !HasFactory => $"TypedResults.Json({body}, statusCode: {Status.Invariant()})",
            (201 or 202, false) => $"TypedResults.{CaseName}((string?)null, {body})",
            (201 or 202, true) => $"TypedResults.{CaseName}((string?)null)",
            (_, false) => $"TypedResults.{CaseName}({body})",
            _ => $"TypedResults.{CaseName}()",
        };
    }

    /// <summary>
    /// An operation, decided: what an endpoint must bind and answer to implement it. Its outcomes are its successes, then
    /// its errors, each in the spec's order; its dependencies are its x-dependencies, and its response headers the names
    /// of the headers its responses document.
    /// </summary>
    internal sealed record OperationShape(
        string Id,
        string Method,
        string Path,
        string Tag,
        EquatableArray<ParameterShape> Parameters,
        BodyShape? Body,
        EquatableArray<OutcomeShape> Outcomes,
        EquatableArray<string> Dependencies,
        EquatableArray<string> ResponseHeaders)
    {
        public string Name => Naming.Pascal(Id);

        public IEnumerable<int> Statuses => Outcomes.Select(r => r.Status).OrderBy(s => s);

        public IReadOnlyList<OutcomeShape> Successes => Outcomes.Where(o => o.Success).ToList();

        public IReadOnlyList<OutcomeShape> Errors => Outcomes.Where(o => !o.Success).ToList();
    }

    /// <summary>
    /// One tag's operations, decided: what its endpoints are emitted from (Map{Api}{Name}, in {Api}.Endpoints.{Name}.g.cs),
    /// so an edit that leaves them equal, such as a property added to a component the tag uses, re-emits nothing.
    /// </summary>
    internal sealed record TagShape(string ApiName, string Namespace, string Name, EquatableArray<OperationShape> Operations);

    /// <summary>
    /// A spec, parsed once: its operations and components, everything decided and nothing from Microsoft.OpenApi in it,
    /// so the steps that read it are reused when an edit leaves it equal.
    /// </summary>
    internal sealed record ApiShape(string ApiName, string Namespace, string Path, EquatableArray<OperationShape> Operations, EquatableArray<ComponentShape> Components);
}
