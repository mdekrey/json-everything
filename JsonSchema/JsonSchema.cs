﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Json.Pointer;

namespace Json.Schema;

/// <summary>
/// Represents a JSON Schema.
/// </summary>
[JsonConverter(typeof(SchemaJsonConverter))]
[DebuggerDisplay("{ToDebugString()}")]
public class JsonSchema : IBaseDocument
{
	private readonly Dictionary<string, IJsonSchemaKeyword>? _keywords;
	private readonly List<(DynamicScope Scope, SchemaConstraint Constraint)> _constraints = new();

	/// <summary>
	/// The empty schema `{}`.  Functionally equivalent to <see cref="True"/>.
	/// </summary>
	public static readonly JsonSchema Empty = new(Enumerable.Empty<IJsonSchemaKeyword>());
	/// <summary>
	/// The `true` schema.  Passes all instances.
	/// </summary>
	public static readonly JsonSchema True = new(true) { BaseUri = new("https://json-schema.org/true") };
	/// <summary>
	/// The `false` schema.  Fails all instances.
	/// </summary>
	public static readonly JsonSchema False = new(false) { BaseUri = new("https://json-schema.org/false") };

	/// <summary>
	/// Gets the keywords contained in the schema.  Only populated for non-boolean schemas.
	/// </summary>
	public IReadOnlyCollection<IJsonSchemaKeyword>? Keywords => _keywords?.Values;

	/// <summary>
	/// Gets the keyword class by keyword name.
	/// </summary>
	/// <param name="keyword">The keyword name.</param>
	/// <returns>The keyword implementation if it exists in the schema.</returns>
	public IJsonSchemaKeyword? this[string keyword] => _keywords?.TryGetValue(keyword, out var k) ?? false ? k : null;

	/// <summary>
	/// For boolean schemas, gets the value.  Null if the schema isn't a boolean schema.
	/// </summary>
	public bool? BoolValue { get; }

	/// <summary>
	/// Gets the base URI that applies to this schema.  This may be defined by a parent schema.
	/// </summary>
	/// <remarks>
	/// This property is initialized to a generated random value that matches `https://json-everything.net/{random}`
	/// where `random` is 10 hex characters.
	///
	/// It may change after the initial evaluation based on whether the schema contains an `$id` keyword
	/// or is a child of another schema.
	/// </remarks>
	public Uri BaseUri { get; set; } = GenerateBaseUri();

	/// <summary>
	/// Gets whether the schema defines a new schema resource.  This will only be true if it contains an `$id` keyword.
	/// </summary>
	public bool IsResourceRoot { get; private set; }

	/// <summary>
	/// Gets the specification version as determined by analyzing the `$schema` keyword, if it exists.
	/// </summary>
	public SpecVersion DeclaredVersion { get; private set; }

	internal Dictionary<string, (JsonSchema Schema, bool IsDynamic)> Anchors { get; } = new();
	internal JsonSchema? RecursiveAnchor { get; set; }

	private JsonSchema(bool value)
	{
		BoolValue = value;
	}
	internal JsonSchema(IEnumerable<IJsonSchemaKeyword> keywords)
	{
		_keywords = keywords.ToDictionary(x => x.Keyword());
	}

	/// <summary>
	/// Loads text from a file and deserializes a <see cref="JsonSchema"/>.
	/// </summary>
	/// <param name="fileName">The filename to load, URL-decoded.</param>
	/// <param name="options">Serializer options.</param>
	/// <returns>A new <see cref="JsonSchema"/>.</returns>
	/// <exception cref="JsonException">Could not deserialize a portion of the schema.</exception>
	/// <remarks>The filename needs to not be URL-encoded as <see cref="Uri"/> attempts to encode it.</remarks>
	public static JsonSchema FromFile(string fileName, JsonSerializerOptions? options = null)
	{
		var text = File.ReadAllText(fileName);
		var schema = FromText(text, options);
		var path = Path.GetFullPath(fileName);
		// For some reason, full *nix file paths (which start with '/') don't work quite right when
		// being prepended with 'file:///'.  It seems the '////' is interpreted as '//' and the
		// first folder in the path is then interpreted as the host.  To account for this, we
		// need to prepend with 'file://' instead.
		var protocol = path.StartsWith("/") ? "file://" : "file:///";
		schema.BaseUri = new Uri($"{protocol}{path}");
		return schema;
	}

	/// <summary>
	/// Deserializes a <see cref="JsonSchema"/> from text.
	/// </summary>
	/// <param name="jsonText">The text to parse.</param>
	/// <param name="options">Serializer options.</param>
	/// <returns>A new <see cref="JsonSchema"/>.</returns>
	/// <exception cref="JsonException">Could not deserialize a portion of the schema.</exception>
	public static JsonSchema FromText(string jsonText, JsonSerializerOptions? options = null)
	{
		return JsonSerializer.Deserialize<JsonSchema>(jsonText, options)!;
	}

	/// <summary>
	/// Deserializes a <see cref="JsonSchema"/> from a stream.
	/// </summary>
	/// <param name="source">A stream.</param>
	/// <param name="options">Serializer options.</param>
	/// <returns>A new <see cref="JsonSchema"/>.</returns>
	public static ValueTask<JsonSchema> FromStream(Stream source, JsonSerializerOptions? options = null)
	{
		return JsonSerializer.DeserializeAsync<JsonSchema>(source, options)!;
	}

	/// <summary>
	/// Evaluates an instance by automatically determining the schema to use by examining
	/// the instance's `$schema` key.
	/// </summary>
	/// <param name="root">The root instance.</param>
	/// <param name="options">The options to use for this evaluation.</param>
	/// <returns>A <see cref="EvaluationResults"/> that provides the outcome of the evaluation.</returns>
	/// <exception cref="ArgumentException">
	/// Throw when the instance doesn't have a `$schema` key, when the value under `$schema` is not
	/// an absolute URI, or when the URI is not associated with a registered schema.
	/// </exception>
	// TODO: Not quite ready to release this.  Is it a good practice?  https://github.com/orgs/json-schema-org/discussions/473
	internal static EvaluationResults AutoEvaluate(JsonNode? root, EvaluationOptions? options = null)
	{
		string? schemaId = null;
		(root as JsonObject)?[SchemaKeyword.Name]?.AsValue().TryGetValue(out schemaId);
		if (schemaId == null || !Uri.TryCreate(schemaId, UriKind.Absolute, out var schemaUri))
			throw new ArgumentException("JSON must contain `$schema` with an absolute URI.", nameof(root));

		options ??= EvaluationOptions.Default;

		var schema = options.SchemaRegistry.Get(schemaUri) as JsonSchema;
		if (schema == null)
			throw new ArgumentException($"Schema URI {schemaId} unrecognized", nameof(root));

		return schema.Evaluate(root, options);
	}

	private static Uri GenerateBaseUri() => new($"https://json-everything.net/{Guid.NewGuid().ToString("N").Substring(0, 10)}");

	/// <summary>
	/// Gets a specified keyword if it exists.
	/// </summary>
	/// <typeparam name="T">The type of the keyword to get.</typeparam>
	/// <returns>The keyword if it exists; otherwise null.</returns>
	public T? GetKeyword<T>()
		where T : IJsonSchemaKeyword
	{
		var keyword = typeof(T).Keyword();
		return (T?)this[keyword];
	}

	/// <summary>
	/// Gets a specified keyword if it exists.
	/// </summary>
	/// <param name="keyword">The keyword if it exists; otherwise null.</param>
	/// <typeparam name="T">The type of the keyword to get.</typeparam>
	/// <returns>true if the keyword exists; otherwise false.</returns>
	public bool TryGetKeyword<T>(out T? keyword)
		where T : IJsonSchemaKeyword
	{
		var name = typeof(T).Keyword();
		return TryGetKeyword(name, out keyword);

	}

	/// <summary>
	/// Gets a specified keyword if it exists.
	/// </summary>
	/// <typeparam name="T">The type of the keyword to get.</typeparam>
	/// <param name="keywordName">The name of the keyword.</param>
	/// <param name="keyword">The keyword if it exists; otherwise null.</param>
	/// <returns>true if the keyword exists; otherwise false.</returns>
	public bool TryGetKeyword<T>(string keywordName, out T? keyword)
		where T : IJsonSchemaKeyword
	{
		if (BoolValue.HasValue)
		{
			keyword = default;
			return false;
		}

		if (_keywords!.TryGetValue(keywordName, out var k))
		{
			keyword = (T)k!;
			return true;
		}

		keyword = default;
		return false;
	}

	/// <summary>
	/// Evaluates an instance against this schema.
	/// </summary>
	/// <param name="root">The root instance.</param>
	/// <param name="options">The options to use for this evaluation.</param>
	/// <returns>A <see cref="EvaluationResults"/> that provides the outcome of the evaluation.</returns>
	public EvaluationResults Evaluate(JsonNode? root, EvaluationOptions? options = null)
	{
		options = EvaluationOptions.From(options ?? EvaluationOptions.Default);

		// BaseUri may change if $id is present
		// TODO: remove options.EvaluatingAs
		var evaluatingAs = options.EvaluatingAs = DetermineSpecVersion(this, options.SchemaRegistry, options.EvaluateAs);
		PopulateBaseUris(this, this, BaseUri, options.SchemaRegistry, evaluatingAs, true);


		var context = new EvaluationContext(options, evaluatingAs, BaseUri);
		var constraint = BuildConstraint(JsonPointer.Empty, JsonPointer.Empty, JsonPointer.Empty, context.Scope);
		if (!BoolValue.HasValue)
			PopulateConstraint(constraint, context);

		var evaluation = constraint.BuildEvaluation(root, JsonPointer.Empty, JsonPointer.Empty, options);
		evaluation.Evaluate(context);


		var results = evaluation.Results;
		switch (options.OutputFormat)
		{
			case OutputFormat.Flag:
				results.ToFlag();
				break;
			case OutputFormat.List:
				results.ToList();
				break;
			case OutputFormat.Hierarchical:
				break;
			default:
				throw new ArgumentOutOfRangeException();
		}

		return results;
	}

	private bool IsDynamic()
	{
		if (BoolValue.HasValue) return false;
		if (Keywords!.Any(x => x is DynamicRefKeyword or RecursiveRefKeyword)) return true;

		return Keywords!.SelectMany(GetSubschemas).Any(x => x.IsDynamic());
	}

	/// <summary>
	/// Builds a constraint for the schema.
	/// </summary>
	/// <param name="relativeEvaluationPath">
	/// The relative evaluation path in JSON Pointer form.  Generally this will be a keyword name,
	/// but may have other segments, such as in the case of `properties` which also has the property name.
	/// </param>
	/// <param name="baseInstanceLocation">The base location within the instance that is being evaluated.</param>
	/// <param name="relativeInstanceLocation">
	/// The location relative to <paramref name="baseInstanceLocation"/> within the instance that
	/// is being evaluated.
	/// </param>
	/// <param name="context">The evaluation context.</param>
	/// <returns>A schema constraint.</returns>
	/// <remarks>
	/// The constraint returned by this method is cached by the <see cref="JsonSchema"/> object.
	/// Different evaluation paths to this schema object may result in different constraints, so
	/// a new constraint is saved for each dynamic scope.
	/// </remarks>
	public SchemaConstraint GetConstraint(JsonPointer relativeEvaluationPath, JsonPointer baseInstanceLocation, JsonPointer relativeInstanceLocation, EvaluationContext context)
	{
		var baseUri = BoolValue.HasValue ? context.Scope.LocalScope : BaseUri;
	
		var scopedConstraint = CheckScopedConstraints(context.Scope);
		if (scopedConstraint != null)
			return new SchemaConstraint(relativeEvaluationPath, baseInstanceLocation.Combine(relativeInstanceLocation), relativeInstanceLocation, baseUri, this)
			{
				Source = scopedConstraint
			};

		var constraint = BuildConstraint(relativeEvaluationPath, baseInstanceLocation, relativeInstanceLocation, context.Scope);
		if (!BoolValue.HasValue) 
			PopulateConstraint(constraint, context);

		return constraint;
	}

	private SchemaConstraint BuildConstraint(JsonPointer evaluationPath, JsonPointer baseInstanceLocation, JsonPointer relativeInstanceLocation, DynamicScope scope)
	{
		lock (_constraints)
		{
			var scopedConstraint = CheckScopedConstraints(scope);
			if (scopedConstraint != null) return scopedConstraint;

			var baseUri = BoolValue.HasValue ? scope.LocalScope : BaseUri;

			var constraint = new SchemaConstraint(evaluationPath, baseInstanceLocation.Combine(relativeInstanceLocation), relativeInstanceLocation, baseUri, this);
			_constraints.Add((new DynamicScope(scope), constraint));
		
			return constraint;
		}
	}

	private SchemaConstraint? CheckScopedConstraints(DynamicScope scope)
	{
		SchemaConstraint? scopedConstraint;
		// ReSharper disable InconsistentlySynchronizedField
		// We only need to worry about synchronization when potentially adding new constraints
		// which only happens in BuildConstrain().
		if (IsDynamic())
			(_, scopedConstraint) = _constraints.FirstOrDefault(x => x.Scope.Equals(scope));
		else
			scopedConstraint = _constraints.SingleOrDefault().Constraint;
		// ReSharper restore InconsistentlySynchronizedField
		return scopedConstraint;
	}

	private void PopulateConstraint(SchemaConstraint constraint, EvaluationContext context)
	{
		if (context.EvaluatingAs is SpecVersion.Draft6 or SpecVersion.Draft7)
		{
			// base URI doesn't change for $ref schemas in draft 6/7
			var refKeyword = (RefKeyword?) Keywords!.FirstOrDefault(x => x is RefKeyword);
			if (refKeyword != null)
			{
				var refConstraint = refKeyword.GetConstraint(constraint, Array.Empty<KeywordConstraint>(), context);
				constraint.Constraints = new[] { refConstraint };
				return;
			}
		}

		var dynamicScopeChanged = false;
		if (context.Scope.LocalScope != BaseUri)
		{
			dynamicScopeChanged = true;
			context.Scope.Push(BaseUri);
		}
		var localConstraints = new List<KeywordConstraint>();
		var version = DeclaredVersion == SpecVersion.Unspecified ? context.EvaluatingAs : DeclaredVersion;
		var keywords = context.Options.FilterKeywords(context.GetKeywordsToProcess(this, context.Options), version);
		foreach (var keyword in keywords.OrderBy(x => x.Priority()))
		{
			var keywordConstraint = keyword.GetConstraint(constraint, localConstraints, context);
			localConstraints.Add(keywordConstraint);
		}

		constraint.Constraints = localConstraints.ToArray();
		if (dynamicScopeChanged)
			context.Scope.Pop();
	}

	internal static void Initialize(JsonSchema schema, SchemaRegistry registry, Uri? baseUri = null)
	{
		PopulateBaseUris(schema, schema, baseUri ?? schema.BaseUri, registry, DetermineSpecVersion(schema, registry, SpecVersion.Unspecified), true);
	}

	private static SpecVersion DetermineSpecVersion(JsonSchema schema, SchemaRegistry registry, SpecVersion desiredDraft)
	{
		if (schema.BoolValue.HasValue) return SpecVersion.DraftNext;
		if (schema.DeclaredVersion != SpecVersion.Unspecified) return schema.DeclaredVersion;
		if (!Enum.IsDefined(typeof(SpecVersion), desiredDraft)) return desiredDraft;

		if (schema.TryGetKeyword<SchemaKeyword>(SchemaKeyword.Name, out var schemaKeyword))
		{
			var metaSchemaId = schemaKeyword?.Schema;
			while (metaSchemaId != null)
			{
				var version = metaSchemaId.OriginalString switch
				{
					MetaSchemas.Draft6IdValue => SpecVersion.Draft6,
					MetaSchemas.Draft7IdValue => SpecVersion.Draft7,
					MetaSchemas.Draft201909IdValue => SpecVersion.Draft201909,
					MetaSchemas.Draft202012IdValue => SpecVersion.Draft202012,
					MetaSchemas.DraftNextIdValue => SpecVersion.DraftNext,
					_ => SpecVersion.Unspecified
				};
				if (version != SpecVersion.Unspecified)
				{
					schema.DeclaredVersion = version;
					return version;
				}

				var metaSchema = registry.Get(metaSchemaId) as JsonSchema;
				if (metaSchema == null)
					throw new JsonSchemaException("Cannot resolve custom meta-schema.  Make sure meta-schemas are registered in the global registry.");

				if (metaSchema.TryGetKeyword<SchemaKeyword>(SchemaKeyword.Name, out var newMetaSchemaKeyword) &&
				    newMetaSchemaKeyword!.Schema == metaSchemaId)
					throw new JsonSchemaException("Custom meta-schema `$schema` keywords must eventually resolve to a meta-schema for a supported specification version.");

				metaSchemaId = newMetaSchemaKeyword!.Schema;
			}
		}

		if (desiredDraft != SpecVersion.Unspecified) return desiredDraft;

		var allDraftsArray = Enum.GetValues(typeof(SpecVersion)).Cast<SpecVersion>().ToArray();
		var allDrafts = allDraftsArray.Aggregate(SpecVersion.Unspecified, (a, x) => a | x);
		var commonDrafts = schema.Keywords!.Aggregate(allDrafts, (a, x) => a & x.VersionsSupported());
		var candidates = allDraftsArray.Where(x => commonDrafts.HasFlag(x)).ToArray();

		return candidates.Any() ? candidates.Max() : SpecVersion.DraftNext;
	}

	private static void PopulateBaseUris(JsonSchema schema, JsonSchema resourceRoot, Uri currentBaseUri, SchemaRegistry registry, SpecVersion evaluatingAs, bool selfRegister = false)
	{
		if (schema.BoolValue.HasValue) return;
		if (evaluatingAs is SpecVersion.Draft6 or SpecVersion.Draft7 &&
			schema.TryGetKeyword<RefKeyword>(RefKeyword.Name, out _))
		{
			schema.BaseUri = currentBaseUri;
			if (selfRegister)
				registry.RegisterSchema(schema.BaseUri, schema);
		}
		else
		{
			var idKeyword = (IIdKeyword?)schema.Keywords!.FirstOrDefault(x => x is IIdKeyword);
			if (idKeyword != null)
			{
				if (evaluatingAs <= SpecVersion.Draft7 &&
				    idKeyword.Id.OriginalString[0] == '#' &&
				    AnchorKeyword.AnchorPattern.IsMatch(idKeyword.Id.OriginalString.Substring(1)))
				{
					schema.BaseUri = currentBaseUri;
					resourceRoot.Anchors[idKeyword.Id.OriginalString.Substring(1)] = (schema, false);
				}
				else
				{
					schema.IsResourceRoot = true;
					schema.DeclaredVersion = DetermineSpecVersion(schema, registry, evaluatingAs);
					resourceRoot = schema;
					schema.BaseUri = new Uri(currentBaseUri, idKeyword.Id);
					registry.RegisterSchema(schema.BaseUri, schema);
				}
			}
			else
			{
				schema.BaseUri = currentBaseUri;
				if (selfRegister)
					registry.RegisterSchema(schema.BaseUri, schema);
			}

			if (schema.TryGetKeyword<AnchorKeyword>(AnchorKeyword.Name, out var anchorKeyword))
			{
				resourceRoot.Anchors[anchorKeyword!.Anchor] = (schema, false);
			}

			if (schema.TryGetKeyword<DynamicAnchorKeyword>(DynamicAnchorKeyword.Name, out var dynamicAnchorKeyword))
			{
				resourceRoot.Anchors[dynamicAnchorKeyword!.Value] = (schema, true);
			}

			schema.TryGetKeyword<RecursiveAnchorKeyword>(RecursiveAnchorKeyword.Name, out var recursiveAnchorKeyword);
			if (recursiveAnchorKeyword is { Value: true })
				resourceRoot.RecursiveAnchor = schema;
		}

		var subschemas = schema.Keywords!.SelectMany(GetSubschemas);

		foreach (var subschema in subschemas)
		{
			PopulateBaseUris(subschema, resourceRoot, schema.BaseUri, registry, evaluatingAs);
		}
	}

	internal static IEnumerable<JsonSchema> GetSubschemas(IJsonSchemaKeyword keyword)
	{
		switch (keyword)
		{
			case ISchemaContainer { Schema: { } } container:
				yield return container.Schema;
				break;
			case ISchemaCollector collector:
				foreach (var schema in collector.Schemas)
				{
					yield return schema;
				}
				break;
			case IKeyedSchemaCollector collector:
				foreach (var schema in collector.Schemas.Values)
				{
					yield return schema;
				}
				break;
			case ICustomSchemaCollector collector:
				foreach (var schema in collector.Schemas)
				{
					yield return schema;
				}
				break;
		}
	}

	JsonSchema? IBaseDocument.FindSubschema(JsonPointer pointer, EvaluationOptions options)
	{
		object? CheckResolvable(object localResolvable, ref int i, string pointerSegment)
		{
			int index;
			object? newResolvable = null;
			switch (localResolvable)
			{
				case ISchemaContainer container and ISchemaCollector collector:
					if (container.Schema != null!)
					{
						newResolvable = container.Schema;
						i--;
					}
					else if (int.TryParse(pointerSegment, out index) &&
					         index >= 0 && index < collector.Schemas.Count)
						newResolvable = collector.Schemas[index];

					break;
				case ISchemaContainer container:
					newResolvable = container.Schema;
					// need to reprocess the segment
					i--;
					break;
				case ISchemaCollector collector:
					if (int.TryParse(pointerSegment, out index) &&
					    index >= 0 && index < collector.Schemas.Count)
						newResolvable = collector.Schemas[index];
					break;
				case IKeyedSchemaCollector keyedCollector:
					if (keyedCollector.Schemas.TryGetValue(pointerSegment, out var subschema))
						newResolvable = subschema;
					break;
				case ICustomSchemaCollector customCollector:
					(newResolvable, var segmentsConsumed) = customCollector.FindSubschema(pointer.Segments.Skip(i).ToReadOnlyList());
					i += segmentsConsumed;
					break;
				case JsonSchema { _keywords: not null } schema:
					schema._keywords.TryGetValue(pointerSegment, out var k);
					newResolvable = k;
					break;
			}

			if (newResolvable is UnrecognizedKeyword unrecognized)
			{
				var newPointer = JsonPointer.Create(pointer.Segments.Skip(i + 1));
				i += newPointer.Segments.Length;
				newPointer.TryEvaluate(unrecognized.Value, out var value);
				var asSchema = FromText(value?.ToString() ?? "null");
				var hostSchema = (JsonSchema)localResolvable;
				asSchema.BaseUri = hostSchema.BaseUri;
				PopulateBaseUris(asSchema, hostSchema, hostSchema.BaseUri, options.SchemaRegistry, options.EvaluatingAs);
				return asSchema;
			}

			return newResolvable;
		}

		object? resolvable = this;
		for (var i = 0; i < pointer.Segments.Length; i++)
		{
			var segment = pointer.Segments[i];

			resolvable = CheckResolvable(resolvable, ref i, segment.Value);
			if (resolvable == null) return null;
		}

		if (resolvable is JsonSchema target) return target;

		var count = pointer.Segments.Length;
		// These parameters don't really matter.  This extra check only captures the case where the
		// last segment of the pointer is an ISchemaContainer.
		return CheckResolvable(resolvable, ref count, null!) as JsonSchema;
	}

	/// <summary>
	/// Gets a defined anchor.
	/// </summary>
	/// <param name="anchorName">The name of the anchor (excluding the `#`)</param>
	/// <returns>The associated subschema, if the anchor exists, or null.</returns>
	public JsonSchema? GetAnchor(string anchorName) =>
		Anchors.TryGetValue(anchorName, out var anchorDefinition)
			? anchorDefinition.IsDynamic
				? null
				: anchorDefinition.Schema
			: null;

	/// <summary>
	/// Implicitly converts a boolean value into one of the boolean schemas. 
	/// </summary>
	/// <param name="value">The boolean value.</param>
	public static implicit operator JsonSchema(bool value)
	{
		return value ? True : False;
	}

	private string ToDebugString()
	{
		if (BoolValue.HasValue) return BoolValue.Value ? "true" : "false";
		var idKeyword = Keywords!.OfType<IIdKeyword>().SingleOrDefault();
		return idKeyword?.Id.OriginalString ?? BaseUri.OriginalString;
	}
}

internal class SchemaJsonConverter : JsonConverter<JsonSchema>
{
	public override JsonSchema Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.True) return JsonSchema.True;
		if (reader.TokenType == JsonTokenType.False) return JsonSchema.False;

		if (reader.TokenType != JsonTokenType.StartObject)
			throw new JsonException("JSON Schema must be true, false, or an object");

		if (!reader.Read())
			throw new JsonException("Expected token");

		var keywords = new List<IJsonSchemaKeyword>();

		do
		{
			switch (reader.TokenType)
			{
				case JsonTokenType.Comment:
					break;
				case JsonTokenType.PropertyName:
					var keyword = reader.GetString()!;
					reader.Read();
					var keywordType = SchemaKeywordRegistry.GetImplementationType(keyword);
					if (keywordType == null)
					{
						var node = JsonSerializer.Deserialize<JsonNode>(ref reader, options);
						var unrecognizedKeyword = new UnrecognizedKeyword(keyword, node);
						keywords.Add(unrecognizedKeyword);
						break;
					}

					IJsonSchemaKeyword implementation;
					if (reader.TokenType == JsonTokenType.Null)
						implementation = SchemaKeywordRegistry.GetNullValuedKeyword(keywordType) ??
										 throw new InvalidOperationException($"No null instance registered for keyword `{keyword}`");
					else
						implementation = (IJsonSchemaKeyword)JsonSerializer.Deserialize(ref reader, keywordType, options)! ??
										 throw new InvalidOperationException($"Could not deserialize expected keyword `{keyword}`");
					keywords.Add(implementation);
					break;
				case JsonTokenType.EndObject:
					return new JsonSchema(keywords);
				default:
					throw new JsonException("Expected keyword or end of schema object");
			}
		} while (reader.Read());

		throw new JsonException("Expected token");
	}

	public override void Write(Utf8JsonWriter writer, JsonSchema value, JsonSerializerOptions options)
	{
		if (value.BoolValue == true)
		{
			writer.WriteBooleanValue(true);
			return;
		}

		if (value.BoolValue == false)
		{
			writer.WriteBooleanValue(false);
			return;
		}

		writer.WriteStartObject();
		foreach (var keyword in value.Keywords!)
		{
			JsonSerializer.Serialize(writer, keyword, keyword.GetType(), options);
		}

		writer.WriteEndObject();
	}
}

public static partial class ErrorMessages
{
	private static string? _falseSchema;

	/// <summary>
	/// Gets or sets the error message for the "false" schema.
	/// </summary>
	/// <remarks>No tokens are supported.</remarks>
	public static string FalseSchema
	{
		get => _falseSchema ?? Get();
		set => _falseSchema = value;
	}
}