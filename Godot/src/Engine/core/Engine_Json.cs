using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Refii.Core;

/// <summary>
/// JSON text for the object graphs the game writes as status and diagnostics (anonymous types,
/// records, dictionaries of objects), on every managed runtime.
/// </summary>
/// <remarks>
/// <para>
/// Reflection-based System.Text.Json makes a generic converter per CLR type when it first meets
/// it: <c>JsonPropertyInfo&lt;int?&gt;</c>, <c>ArrayConverter&lt;float[], float&gt;</c>,
/// <c>EnumConverter&lt;T&gt;</c>, the parameterized-constructor converter of every anonymous
/// type. NativeAOT (the linux-bionic Android export) cannot instantiate generic code after
/// compilation, so those throw NotSupportedException ("missing native code or metadata") for
/// any nullable, enum, value-type array or anonymous type — the Android status file, the
/// installer's records and the debug channel's replies all hit one.
/// </para>
/// <para>
/// Where code can be generated at run time (desktop CoreCLR, Android Mono) <see cref="Serialize"/>
/// IS <c>JsonSerializer.Serialize</c>. Without it, the graph is written here by plain reflection
/// reads, following the reflection serializer's contract for what this code base serializes:
/// public instance properties (fields only with <c>IncludeFields</c>), most-derived type first;
/// a property's declared type decides its shape, its runtime type only when declared
/// <see cref="object"/>; enums as numbers; dictionaries as objects; sequences as arrays;
/// <c>byte[]</c> as base64. The writer options (indentation, encoder, depth), the null-ignore
/// condition, the naming policy and named floating-point literals are honored; options the
/// serializer would apply and this writer cannot (custom converters, reference handling,
/// number-as-string) are refused rather than silently written differently.
/// <c>Engine_JsonTests</c> holds the two writers to the same text on the desktop.
/// </para>
/// </remarks>
public static class Engine_Json
{
	/// <summary>The JSON text of <paramref name="value"/>, as <c>JsonSerializer.Serialize</c> writes it.</summary>
	public static string Serialize<T>(T value, JsonSerializerOptions? options = null) =>
		RuntimeFeature.IsDynamicCodeSupported
			? ReflectionSerializer(value, options)
			: SerializeGraph(value, typeof(T), options);

	// Reached only where the runtime can generate code; NativeAOT folds the branch away.
	[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Runtime code generation is available on this path.")]
	[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Runtime code generation is available on this path.")]
	private static string ReflectionSerializer<T>(T value, JsonSerializerOptions? options) =>
		JsonSerializer.Serialize(value, options);

	/// <summary>
	/// The graph writer on its own, whatever the runtime: what <see cref="Serialize"/> uses under
	/// NativeAOT. <paramref name="declared"/> plays the part of the serializer's type argument.
	/// </summary>
	public static string SerializeGraph(object? value, Type declared, JsonSerializerOptions? options = null)
	{
		options ??= DefaultOptions;
		if (options.Converters.Count != 0 || options.ReferenceHandler != null ||
			(options.NumberHandling & JsonNumberHandling.WriteAsString) != 0 ||
			options.DefaultIgnoreCondition is not (JsonIgnoreCondition.Never or JsonIgnoreCondition.WhenWritingNull))
		{
			throw new NotSupportedException("Engine_Json writes default contracts only: no custom converters, " +
				"reference handling, numbers as strings or ignore conditions other than WhenWritingNull");
		}

		var writerOptions = new JsonWriterOptions
		{
			Indented = options.WriteIndented,
			Encoder = options.Encoder,
			MaxDepth = options.MaxDepth == 0 ? 64 : options.MaxDepth,
#if NET9_0_OR_GREATER
			IndentCharacter = options.IndentCharacter,
			IndentSize = options.IndentSize,
			NewLine = options.NewLine,
#endif
		};
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream, writerOptions))
		{
			new GraphWriter(writer, options, writerOptions.MaxDepth).Write(value, declared, 0);
		}

		return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
	}

	private readonly struct GraphWriter
	{
		private readonly Utf8JsonWriter _writer;
		private readonly JsonSerializerOptions _options;
		private readonly int _maxDepth;

		public GraphWriter(Utf8JsonWriter writer, JsonSerializerOptions options, int maxDepth)
		{
			_writer = writer;
			_options = options;
			_maxDepth = maxDepth;
		}

		public void Write(object? value, Type declared, int depth)
		{
			if (value is null)
			{
				_writer.WriteNullValue();
				return;
			}

			// The declared type decides the contract, as it does for the serializer; only an
			// object (or an unrelated declaration) defers to what the value actually is.
			Type contract = Nullable.GetUnderlyingType(declared) ?? declared;
			if (contract == typeof(object) || !contract.IsInstanceOfType(value))
			{
				contract = value.GetType();
			}

			if (WriteScalar(value))
			{
				return;
			}

			if (depth >= _maxDepth)
			{
				throw new JsonException(
					$"A possible object cycle was detected, or the depth exceeds the maximum allowed depth of {_maxDepth}.");
			}

			switch (value)
			{
				case IDictionary dictionary:
					WriteDictionary(dictionary, contract, depth);
					return;
				case IEnumerable sequence:
					WriteSequence(sequence, contract, depth);
					return;
				default:
					WriteObject(value, contract, depth);
					return;
			}
		}

		/// <summary>Writes the values the serializer has built-in converters for; false for composites.</summary>
		private bool WriteScalar(object value)
		{
			switch (value)
			{
				case string text: _writer.WriteStringValue(text); return true;
				case bool flag: _writer.WriteBooleanValue(flag); return true;
				case int number: _writer.WriteNumberValue(number); return true;
				case long number: _writer.WriteNumberValue(number); return true;
				case uint number: _writer.WriteNumberValue(number); return true;
				case ulong number: _writer.WriteNumberValue(number); return true;
				case short number: _writer.WriteNumberValue(number); return true;
				case ushort number: _writer.WriteNumberValue(number); return true;
				case byte number: _writer.WriteNumberValue(number); return true;
				case sbyte number: _writer.WriteNumberValue(number); return true;
				case decimal number: _writer.WriteNumberValue(number); return true;
				case float number:
					if (float.IsFinite(number) || !NamedLiterals) _writer.WriteNumberValue(number);
					else WriteNamedLiteral(number);
					return true;
				case double number:
					if (double.IsFinite(number) || !NamedLiterals) _writer.WriteNumberValue(number);
					else WriteNamedLiteral(number);
					return true;
				case char character: _writer.WriteStringValue(character.ToString()); return true;
				case Enum enumeration: WriteEnum(enumeration); return true;
				case Guid guid: _writer.WriteStringValue(guid); return true;
				case DateTime time: _writer.WriteStringValue(time); return true;
				case DateTimeOffset time: _writer.WriteStringValue(time); return true;
				case TimeSpan span: _writer.WriteStringValue(span.ToString("c", CultureInfo.InvariantCulture)); return true;
				case DateOnly date: _writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture)); return true;
				case Uri uri: _writer.WriteStringValue(uri.OriginalString); return true;
				case Version version: _writer.WriteStringValue(version.ToString()); return true;
				case byte[] bytes: _writer.WriteBase64StringValue(bytes); return true;
				case JsonNode node: node.WriteTo(_writer, _options); return true;
				case JsonElement element: element.WriteTo(_writer); return true;
				case JsonDocument document: document.WriteTo(_writer); return true;
				case Type or Delegate or MemberInfo or IntPtr or UIntPtr:
					throw new NotSupportedException(
						$"Serialization and deserialization of '{value.GetType()}' instances is not supported.");
				default:
					return false;
			}
		}

		private bool NamedLiterals => (_options.NumberHandling & JsonNumberHandling.AllowNamedFloatingPointLiterals) != 0;

		private void WriteNamedLiteral(double number) =>
			_writer.WriteStringValue(double.IsNaN(number) ? "NaN" : number > 0 ? "Infinity" : "-Infinity");

		private void WriteEnum(Enum value)
		{
			TypeCode code = Type.GetTypeCode(Enum.GetUnderlyingType(value.GetType()));
			if (code is TypeCode.Byte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64)
				_writer.WriteNumberValue(Convert.ToUInt64(value, CultureInfo.InvariantCulture));
			else
				_writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
		}

		private void WriteDictionary(IDictionary dictionary, Type contract, int depth)
		{
			Type valueType = GenericArgument(contract, typeof(IDictionary<,>), 1) ??
				GenericArgument(contract, typeof(IReadOnlyDictionary<,>), 1) ?? typeof(object);
			_writer.WriteStartObject();
			foreach (DictionaryEntry entry in dictionary)
			{
				_writer.WritePropertyName(KeyName(entry.Key));
				Write(entry.Value, valueType, depth + 1);
			}

			_writer.WriteEndObject();
		}

		private string KeyName(object key)
		{
			string name = key switch
			{
				string text => text,
				Enum enumeration => enumeration.ToString(),
				IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
				_ => key.ToString() ?? string.Empty,
			};
			return _options.DictionaryKeyPolicy?.ConvertName(name) ?? name;
		}

		private void WriteSequence(IEnumerable sequence, Type contract, int depth)
		{
			Type element = contract.IsArray ? contract.GetElementType()! :
				GenericArgument(contract, typeof(IEnumerable<>), 0) ?? typeof(object);
			_writer.WriteStartArray();
			foreach (object? item in sequence)
			{
				Write(item, element, depth + 1);
			}

			_writer.WriteEndArray();
		}

		private void WriteObject(object value, Type contract, int depth)
		{
			_writer.WriteStartObject();
			foreach (MemberInfo member in Members(contract, _options.IncludeFields))
			{
				object? item;
				Type declared;
				if (member is PropertyInfo property)
				{
					item = property.GetValue(value, BindingFlags.DoNotWrapExceptions, null, null, null);
					declared = property.PropertyType;
				}
				else
				{
					var field = (FieldInfo)member;
					item = field.GetValue(value);
					declared = field.FieldType;
				}

				if (item is null && _options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull)
				{
					continue;
				}

				string name = member.Name;
				_writer.WritePropertyName(_options.PropertyNamingPolicy?.ConvertName(name) ?? name);
				Write(item, declared, depth + 1);
			}

			_writer.WriteEndObject();
		}
	}

	private static readonly JsonSerializerOptions DefaultOptions = new();

	private static readonly Dictionary<(Type, bool), MemberInfo[]> MemberCache = new();

	private const string ReflectedGraph = "Reads members of the runtime types in the graph. The game assembly and GodotSharp are trimmer roots, so their members stay; a trimmed framework type writes fewer members.";

	/// <summary>
	/// The serialized members in the serializer's order: each type's declared public readable
	/// non-indexer properties, most-derived type first, a name seen once only; then, with
	/// IncludeFields, the public fields the same way. An interface contributes its own and its
	/// base interfaces' properties.
	/// </summary>
	[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = ReflectedGraph)]
	[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = ReflectedGraph)]
	private static MemberInfo[] Members(Type type, bool includeFields)
	{
		lock (MemberCache)
		{
			if (MemberCache.TryGetValue((type, includeFields), out MemberInfo[]? cached))
				return cached;
		}

		var hierarchy = new List<Type>();
		if (type.IsInterface)
		{
			hierarchy.Add(type);
			hierarchy.AddRange(type.GetInterfaces());
		}
		else
		{
			for (Type? current = type; current != null && current != typeof(object) && current != typeof(ValueType);
				 current = current.BaseType)
				hierarchy.Add(current);
		}

		const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
			BindingFlags.DeclaredOnly;
		var names = new HashSet<string>(StringComparer.Ordinal);
		var members = new List<MemberInfo>();
		foreach (Type current in hierarchy)
		{
			foreach (PropertyInfo property in current.GetProperties(Declared))
			{
				if (property.GetIndexParameters().Length == 0 && property.GetMethod is { IsPublic: true } &&
					names.Add(property.Name))
					members.Add(property);
			}

			if (!includeFields)
				continue;

			foreach (FieldInfo field in current.GetFields(Declared))
			{
				if (field.IsPublic && names.Add(field.Name))
					members.Add(field);
			}
		}

		MemberInfo[] result = members.ToArray();
		lock (MemberCache)
			MemberCache[(type, includeFields)] = result;
		return result;
	}

	/// <summary>The argument at <paramref name="index"/> of the type's implementation of a generic interface, or null.</summary>
	[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = ReflectedGraph)]
	private static Type? GenericArgument(Type type, Type generic, int index)
	{
		if (type.IsGenericType && type.GetGenericTypeDefinition() == generic)
			return type.GetGenericArguments()[index];

		foreach (Type candidate in type.GetInterfaces())
		{
			if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == generic)
				return candidate.GetGenericArguments()[index];
		}

		return null;
	}
}
