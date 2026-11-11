using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using TFWRRemoteAPI;

// Minimal test runner for MiniJson. Exit code 0 = all passed.
internal static class Program
{
    private static int _failed;
    private static int _passed;

    private static void Check(string name, bool condition)
    {
        if (condition) { _passed++; return; }
        _failed++;
        Console.WriteLine("FAIL: " + name);
    }

    private static void Throws(string name, string json)
    {
        try
        {
            MiniJson.Parse(json);
            _failed++;
            Console.WriteLine("FAIL (no exception): " + name);
        }
        catch (MiniJson.JsonException)
        {
            _passed++;
        }
    }

    private static int Main()
    {
        // The plugin runs under whatever locale the player uses; numbers must not depend on it.
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

        // Strings and escapes
        var obj = MiniJson.ParseObject("{\"code\": \"a\\nb\\t\\\"q\\\" \\\\ \\/\", \"filename\": \"f\"}");
        Check("basic escapes", MiniJson.GetString(obj, "code") == "a\nb\t\"q\" \\ /");
        Check("second key", MiniJson.GetString(obj, "filename") == "f");

        // \uXXXX is what Python's json.dumps emits for every non-ASCII character
        obj = MiniJson.ParseObject("{\"code\": \"caf\\u00e9 \\ud83d\\ude00\"}");
        Check("unicode escape", MiniJson.GetString(obj, "code") == "caf\u00e9 \ud83d\ude00");

        // Raw UTF-8 passes through
        obj = MiniJson.ParseObject("{\"code\": \"# naïve ✓\"}");
        Check("raw non-ascii", MiniJson.GetString(obj, "code") == "# naïve ✓");

        // A key name appearing inside a string value must not be mistaken for the key
        obj = MiniJson.ParseObject("{\"code\": \"d = {'mode': 1, \\\"filename\\\": 2}\", \"mode\": \"simulate\", \"filename\": \"real\"}");
        Check("key text inside value: mode", MiniJson.GetString(obj, "mode") == "simulate");
        Check("key text inside value: filename", MiniJson.GetString(obj, "filename") == "real");

        // Pretty-printed input (newlines after colons and commas)
        obj = MiniJson.ParseObject("{\n  \"seed\":\n    42,\r\n  \"open_editor\" :\tfalse,\n  \"items\": {\n    \"cactus\": 1e9,\n    \"power\": 2.5\n  }\n}\n");
        Check("pretty seed", MiniJson.GetNumber(obj, "seed", -1) == 42);
        Check("pretty bool", MiniJson.GetBool(obj, "open_editor", true) == false);
        var items = MiniJson.GetObject(obj, "items");
        Check("nested object", items != null && items.Count == 2);
        Check("exponent number", (double)items["cactus"] == 1e9);
        Check("decimal number under comma-decimal culture", (double)items["power"] == 2.5);

        // Defaults and typed accessors
        obj = MiniJson.ParseObject("{\"a\": null, \"n\": \"5000\", \"neg\": -3}");
        Check("missing string", MiniJson.GetString(obj, "zzz") == null);
        Check("null string", MiniJson.GetString(obj, "a", "dflt") == "dflt");
        Check("missing bool default", MiniJson.GetBool(obj, "zzz", true));
        Check("numeric string accepted", MiniJson.GetNumber(obj, "n", 1) == 5000);
        Check("negative number", MiniJson.GetNumber(obj, "neg", 0) == -3);
        Check("missing object", MiniJson.GetObject(obj, "zzz") == null);

        // Arrays and literals
        var arr = MiniJson.Parse("[1, \"two\", true, null, [], {}]") as List<object>;
        Check("array length", arr != null && arr.Count == 6);
        Check("array values", (double)arr[0] == 1 && (string)arr[1] == "two" && (bool)arr[2] && arr[3] == null);

        // Empty containers and nested braces inside strings
        obj = MiniJson.ParseObject("{\"items\": {}, \"code\": \"x = {1: {2: 3}}, y\"}");
        Check("empty object", MiniJson.GetObject(obj, "items").Count == 0);
        Check("braces and commas in string", MiniJson.GetString(obj, "code") == "x = {1: {2: 3}}, y");

        // Malformed input is rejected rather than half-read
        Throws("empty", "");
        Throws("truncated", "{\"code\": \"abc");
        Throws("trailing garbage", "{} x");
        Throws("missing colon", "{\"a\" 1}");
        Throws("bad escape", "{\"a\": \"\\x\"}");
        Throws("bad literal", "{\"a\": tru}");
        Throws("trailing comma", "{\"a\": 1,}");
        try { MiniJson.ParseObject("[1]"); Check("array is not an object", false); }
        catch (MiniJson.JsonException) { Check("array is not an object", true); }
        try { MiniJson.GetString(MiniJson.ParseObject("{\"a\": 1}"), "a"); Check("wrong type rejected", false); }
        catch (MiniJson.JsonException) { Check("wrong type rejected", true); }

        // Writing
        Check("quote control chars", MiniJson.Quote("a\u0001b\u001f") == "\"a\\u0001b\\u001f\"");
        Check("quote common", MiniJson.Quote("\"\\\n\r\t") == "\"\\\"\\\\\\n\\r\\t\"");
        Check("number invariant culture", MiniJson.Number(1234.5) == "1234.5");
        Check("number nan", MiniJson.Number(double.NaN) == "null");
        Check("number inf", MiniJson.Number(double.PositiveInfinity) == "null");
        Check("number integer", MiniJson.Number(1000000000) == "1000000000");

        string written = new MiniJson.ObjectWriter()
            .Str("s", "line1\nline2 \u0002")
            .Str("nul", null)
            .Num("n", 0.25)
            .Bool("b", true)
            .Raw("raw", "{\"x\":1}")
            .StrArray("arr", new[] { "a", "b\"" })
            .NumArray("nums", new[] { 1.5, 2.0 })
            .ToString();
        var round = MiniJson.ParseObject(written);
        Check("round trip string", MiniJson.GetString(round, "s") == "line1\nline2 \u0002");
        Check("round trip null", round["nul"] == null);
        Check("round trip number", MiniJson.GetNumber(round, "n", 0) == 0.25);
        Check("round trip bool", MiniJson.GetBool(round, "b", false));
        Check("round trip raw", (double)MiniJson.GetObject(round, "raw")["x"] == 1);
        Check("round trip array", ((List<object>)round["arr"]).Count == 2 && (string)((List<object>)round["arr"])[1] == "b\"");
        Check("round trip nums", (double)((List<object>)round["nums"])[0] == 1.5);
        Check("empty writer", new MiniJson.ObjectWriter().ToString() == "{}");

        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
