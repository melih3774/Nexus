using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcCore.Capabilities.Storage;

/// <summary>
/// Reads enums out of state files without caring how they were spelled when they were written.
/// <para>
/// <b>Why the disk needs this and the wire does not.</b> The wire format is a contract between two
/// programs that ship together and are checked against shared vectors: it should be exact, and
/// <c>ProtocolCodec</c> keeps it exact. A state file is a contract with <em>the past</em> — with
/// whatever this program wrote months ago, on a version nobody is going to change now. Those are
/// different problems, and only one of them is served by strictness.
/// </para>
/// <para>
/// v0.7.1 is where that distinction stopped being theoretical. <c>IncidentTrigger</c> had been
/// serialising through the naming policy as <c>pointer_activity</c>; pinning it to
/// <c>POINTER_ACTIVITY</c> to match the phone made every previously recorded incident unreadable,
/// because <c>System.Text.Json</c> stops accepting the policy spelling once a member name is pinned
/// by attribute. The store would have quarantined the history file and started empty — discarding
/// the index for camera images, screenshots and audio still sitting on disk. Losing the record of a
/// security incident in order to fix the display of security incidents is not a trade worth making.
/// </para>
/// <para>
/// So: <b>write the exact name, accept any reasonable spelling of it.</b> Matching ignores case and
/// underscores, which covers every form the naming policies in this codebase have ever produced —
/// <c>PointerActivity</c>, <c>pointer_activity</c>, <c>POINTER_ACTIVITY</c> — and nothing else.
/// It is deliberately not a fuzzy match: an unrecognised value is still an error, because a value
/// this program never wrote is corruption rather than history.
/// </para>
/// </summary>
public sealed class PersistedEnumConverter : JsonConverterFactory
{
    private readonly JsonNamingPolicy _policy;

    /// <param name="policy">
    /// How to spell members that carry no <see cref="JsonStringEnumMemberNameAttribute"/>. Matches
    /// the policy the store has always used, so files written before this converter existed are
    /// written identically after it.
    /// </param>
    public PersistedEnumConverter(JsonNamingPolicy policy) => _policy = policy;

    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert),
            _policy)!;

    private sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        /// <summary>Value to the one spelling this program writes.</summary>
        private readonly FrozenDictionary<TEnum, string> _write;

        /// <summary>Normalised spelling to value. Several spellings can reach the same member.</summary>
        private readonly FrozenDictionary<string, TEnum> _read;

        public TolerantEnumConverter(JsonNamingPolicy policy)
        {
            var write = new Dictionary<TEnum, string>();
            var read = new Dictionary<string, TEnum>(StringComparer.Ordinal);

            foreach (var value in Enum.GetValues<TEnum>())
            {
                var name = value.ToString();

                var pinned = typeof(TEnum).GetField(name)
                    ?.GetCustomAttributes(typeof(JsonStringEnumMemberNameAttribute), false)
                    .Cast<JsonStringEnumMemberNameAttribute>()
                    .FirstOrDefault()
                    ?.Name;

                write[value] = pinned ?? policy.ConvertName(name);

                // Every spelling this codebase could have produced for the member, reduced to one
                // key. The C# name is included because it is what a policy-less serializer writes.
                read[Normalise(write[value])] = value;
                read[Normalise(name)] = value;
                read[Normalise(policy.ConvertName(name))] = value;
            }

            _write = write.ToFrozenDictionary();
            _read = read.ToFrozenDictionary(StringComparer.Ordinal);
        }

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException(
                    $"Expected a string for {typeof(TEnum).Name}, found {reader.TokenType}.");
            }

            var raw = reader.GetString();

            if (raw is not null && _read.TryGetValue(Normalise(raw), out var value))
            {
                return value;
            }

            // Named, not echoed: the message reaches a log, and a state file is the owner's data.
            throw new JsonException($"'{typeof(TEnum).Name}' has no member matching the stored value.");
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            if (!_write.TryGetValue(value, out var name))
            {
                throw new JsonException($"Cannot write undefined {typeof(TEnum).Name} value.");
            }

            writer.WriteStringValue(name);
        }

        /// <summary>Case and underscores removed, which is the whole of the tolerance on offer.</summary>
        private static string Normalise(string value)
        {
            Span<char> buffer = value.Length <= 64 ? stackalloc char[value.Length] : new char[value.Length];
            var length = 0;

            foreach (var c in value)
            {
                if (c != '_')
                {
                    buffer[length++] = char.ToUpperInvariant(c);
                }
            }

            return new string(buffer[..length]);
        }
    }
}
