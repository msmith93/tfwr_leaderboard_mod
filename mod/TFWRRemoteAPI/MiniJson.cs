using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TFWRRemoteAPI
{
    /// <summary>
    /// Small dependency-free JSON reader/writer (BepInEx on net46 ships no JSON library).
    /// Pure C# with no Unity or game references so it can be unit tested outside the game
    /// (see mod/TFWRRemoteAPI.Tests).
    ///
    /// Parse() maps JSON to: Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, double, bool, null.
    /// </summary>
    public static class MiniJson
    {
        public sealed class JsonException : Exception
        {
            public JsonException(string message) : base(message) { }
        }

        // ── Reading ─────────────────────────────────────────────

        public static object Parse(string json)
        {
            if (json == null) throw new JsonException("JSON body is empty");
            int i = 0;
            SkipWs(json, ref i);
            if (i >= json.Length) throw new JsonException("JSON body is empty");
            object value = ParseValue(json, ref i, 0);
            SkipWs(json, ref i);
            if (i != json.Length)
                throw new JsonException($"Unexpected character '{json[i]}' at position {i}");
            return value;
        }

        public static Dictionary<string, object> ParseObject(string json)
        {
            var obj = Parse(json) as Dictionary<string, object>;
            if (obj == null) throw new JsonException("Request body must be a JSON object");
            return obj;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '﻿') i++;
                else break;
            }
        }

        private static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > 64) throw new JsonException("JSON is nested too deeply");
            SkipWs(s, ref i);
            if (i >= s.Length) throw new JsonException("Unexpected end of JSON");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObjectValue(s, ref i, depth);
                case '[': return ParseArray(s, ref i, depth);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref i);
                    throw new JsonException($"Unexpected character '{c}' at position {i}");
            }
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new JsonException($"Invalid literal at position {i}");
            i += literal.Length;
        }

        private static Dictionary<string, object> ParseObjectValue(string s, ref int i, int depth)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return result; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"')
                    throw new JsonException($"Expected a string key at position {i}");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':')
                    throw new JsonException($"Expected ':' at position {i}");
                i++;
                result[key] = ParseValue(s, ref i, depth + 1);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new JsonException("Unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw new JsonException($"Expected ',' or '}}' at position {i}");
            }
        }

        private static List<object> ParseArray(string s, ref int i, int depth)
        {
            var result = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }
            while (true)
            {
                result.Add(ParseValue(s, ref i, depth + 1));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new JsonException("Unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw new JsonException($"Expected ',' or ']' at position {i}");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new JsonException("Unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) throw new JsonException("Unterminated escape sequence");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new JsonException("Truncated \\u escape");
                        int code;
                        if (!int.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier,
                                CultureInfo.InvariantCulture, out code))
                            throw new JsonException($"Invalid \\u escape at position {i - 2}");
                        // Surrogate pairs arrive as two consecutive \u escapes, which
                        // append as two UTF-16 chars and so recombine on their own.
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw new JsonException($"Invalid escape '\\{e}' at position {i - 2}");
                }
            }
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            double value;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value))
                throw new JsonException($"Invalid number at position {start}");
            return value;
        }

        // ── Typed accessors ─────────────────────────────────────

        public static string GetString(Dictionary<string, object> obj, string key, string defaultValue = null)
        {
            object v;
            if (!obj.TryGetValue(key, out v) || v == null) return defaultValue;
            var str = v as string;
            if (str != null) return str;
            throw new JsonException($"'{key}' must be a string");
        }

        public static bool GetBool(Dictionary<string, object> obj, string key, bool defaultValue)
        {
            object v;
            if (!obj.TryGetValue(key, out v) || v == null) return defaultValue;
            if (v is bool) return (bool)v;
            throw new JsonException($"'{key}' must be true or false");
        }

        /// <summary>Reads a number; numeric strings ("5000") are accepted for convenience.</summary>
        public static double GetNumber(Dictionary<string, object> obj, string key, double defaultValue)
        {
            object v;
            if (!obj.TryGetValue(key, out v) || v == null) return defaultValue;
            if (v is double) return (double)v;
            var str = v as string;
            double parsed;
            if (str != null && double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            throw new JsonException($"'{key}' must be a number");
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> obj, string key)
        {
            object v;
            if (!obj.TryGetValue(key, out v) || v == null) return null;
            var dict = v as Dictionary<string, object>;
            if (dict != null) return dict;
            throw new JsonException($"'{key}' must be an object");
        }

        // ── Writing ─────────────────────────────────────────────

        /// <summary>Returns a quoted, escaped JSON string literal.</summary>
        public static string Quote(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
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
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Culture-independent JSON number. NaN and infinities have no JSON form, so they become null.</summary>
        public static string Number(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Bool(bool b)
        {
            return b ? "true" : "false";
        }

        /// <summary>Incremental JSON object writer that keeps the handlers readable.</summary>
        public sealed class ObjectWriter
        {
            private readonly StringBuilder _sb = new StringBuilder("{");
            private bool _first = true;

            private ObjectWriter Key(string key)
            {
                if (!_first) _sb.Append(',');
                _first = false;
                _sb.Append(Quote(key)).Append(':');
                return this;
            }

            public ObjectWriter Str(string key, string value)
            {
                Key(key);
                _sb.Append(value == null ? "null" : Quote(value));
                return this;
            }

            public ObjectWriter Num(string key, double value)
            {
                Key(key);
                _sb.Append(Number(value));
                return this;
            }

            public ObjectWriter Bool(string key, bool value)
            {
                Key(key);
                _sb.Append(MiniJson.Bool(value));
                return this;
            }

            /// <summary>Appends an already-serialized JSON value.</summary>
            public ObjectWriter Raw(string key, string json)
            {
                Key(key);
                _sb.Append(string.IsNullOrEmpty(json) ? "null" : json);
                return this;
            }

            public ObjectWriter StrArray(string key, IEnumerable<string> values)
            {
                Key(key);
                _sb.Append('[');
                bool first = true;
                foreach (var v in values)
                {
                    if (!first) _sb.Append(',');
                    first = false;
                    _sb.Append(Quote(v));
                }
                _sb.Append(']');
                return this;
            }

            public ObjectWriter NumArray(string key, IEnumerable<double> values)
            {
                Key(key);
                _sb.Append('[');
                bool first = true;
                foreach (var v in values)
                {
                    if (!first) _sb.Append(',');
                    first = false;
                    _sb.Append(Number(v));
                }
                _sb.Append(']');
                return this;
            }

            public override string ToString()
            {
                return _sb.ToString() + "}";
            }
        }
    }
}
