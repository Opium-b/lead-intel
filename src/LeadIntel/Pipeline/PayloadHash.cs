using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LeadIntel.Pipeline;

/// <summary>
/// SHA-256 of a payload, byte-identical to the Python version's
/// <c>hashlib.sha256(json.dumps(payload, sort_keys=True).encode()).hexdigest()</c>,
/// so rows stored by Python aren't all rewritten as "changed" on the first C# run.
/// </summary>
public static class PayloadHash
{
    public static string Of(IReadOnlyDictionary<string, JsonElement> payload)
    {
        var sb = new StringBuilder();
        WriteObject(sb, payload.Select(kv => (kv.Key, kv.Value)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    static void WriteObject(StringBuilder sb, IEnumerable<(string Key, JsonElement Value)> props)
    {
        sb.Append('{');
        var first = true;
        foreach (var (key, value) in props.OrderBy(p => p.Key, StringComparer.Ordinal))  // Python sorts by code point
        {
            if (!first) sb.Append(", ");
            first = false;
            WriteString(sb, key);
            sb.Append(": ");
            WriteValue(sb, value);
        }
        sb.Append('}');
    }

    static void WriteValue(StringBuilder sb, JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Object: WriteObject(sb, v.EnumerateObject().Select(p => (p.Name, p.Value))); break;
            case JsonValueKind.Array:
                sb.Append('[');
                var first = true;
                foreach (var item in v.EnumerateArray())
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String: WriteString(sb, v.GetString()!); break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.Null or JsonValueKind.Undefined: sb.Append("null"); break;
            default: sb.Append(v.GetRawText()); break;  // numbers: Socrata sends strings, so this is rare
        }
    }

    /// <summary>json.dumps string escaping with ensure_ascii=True.</summary>
    static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7f) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        sb.Append('"');
    }
}
