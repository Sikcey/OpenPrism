using System.Text;
using System.Text.RegularExpressions;

namespace WGTun.Core;

// Converts a deliberately small strategy language into an argument vector. Never invokes a shell.
public static class DpiCustomArguments
{
    public static string[] Parse(string engine, string text)
    {
        if (text == null || text.Length > 4096) throw Invalid();
        var tokens = Tokenize(text);
        if (tokens.Count > 128) throw Invalid();
        var result = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            int eq = token.IndexOf('=');
            string name = eq < 0 ? token : token[..eq];
            string? value = eq < 0 ? null : token[(eq + 1)..];
            if (engine == "zapret" && name == "--new" && value == null) { result.Add(name); continue; }
            if (value == null && ++i < tokens.Count) value = tokens[i];
            if (value == null || !Allowed(engine, name, value)) throw Invalid();
            result.Add(name + "=" + value);
        }
        if (result.Count == 0) throw Invalid();
        return result.ToArray();
    }

    private static bool Allowed(string engine, string name, string value)
    {
        if (value.Length is 0 or > 128) return false;
        if (engine == "byedpi") return name switch
        {
            "--split" or "--disorder" or "--oob" or "--disoob" or "--tlsrec" or "--fake" => Regex.IsMatch(value, @"^-?[0-9]{1,4}(\+[sm])?$") && int.TryParse(value.Split('+')[0], out int pos) && pos is >= -4096 and <= 4096,
            "--ttl" => Number(value, 1, 255),
            "--timeout" => Number(value, 1, 3600),
            "--mod-http" => List(value, "h", "d", "r", "m"),
            "--auto" => List(value, "torst", "redirect", "ssl_err", "none"),
            "--proto" => List(value, "http", "tls", "udp"),
            _ => false
        };
        if (engine == "zapret") return name switch
        {
            "--filter-tcp" => List(value, "80", "443"),
            "--filter-udp" => value == "443",
            "--filter-l7" => List(value, "http", "tls", "quic"),
            "--dpi-desync" => List(value, "fake", "multisplit", "multidisorder", "fakedsplit", "fakeddisorder", "syndata"),
            "--dpi-desync-split-pos" => Regex.IsMatch(value, @"^[0-9]{1,3}$") && Number(value, 1, 255),
            "--dpi-desync-repeats" => Number(value, 1, 20),
            "--dpi-desync-ttl" => Number(value, 1, 255),
            "--dpi-desync-autottl" => Number(value, 1, 255),
            "--dpi-desync-fooling" => List(value, "md5sig", "badsum", "badseq", "datanoack", "hopbyhop", "destopt", "ipfrag1"),
            _ => false
        };
        return false;
    }
    private static bool Number(string value, int min, int max) => Regex.IsMatch(value, @"^[0-9]+$") && int.TryParse(value, out int n) && n >= min && n <= max;
    private static bool List(string value, params string[] allowed) => value.Split(',').All(v => allowed.Contains(v)) && value.Split(',').Distinct().Count() == value.Split(',').Length;
    private static InvalidOperationException Invalid() => new("Invalid or unsupported custom DPI arguments.");
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>(); var token = new StringBuilder(); char quote = '\0'; bool started = false;
        foreach (char c in text)
        {
            if (char.IsControl(c) && !char.IsWhiteSpace(c)) throw Invalid();
            if (c is ';' or '|' or '&' or '`' or '$' or '<' or '>' or '\\') throw Invalid();
            if (c is '\'' or '"') { if (quote == '\0') quote = c; else if (quote == c) quote = '\0'; else token.Append(c); started = true; continue; }
            if (char.IsWhiteSpace(c) && quote == '\0') { if (started) { tokens.Add(token.ToString()); token.Clear(); started = false; } continue; }
            token.Append(c); started = true;
        }
        if (quote != '\0') throw Invalid();
        if (started) tokens.Add(token.ToString());
        return tokens;
    }
}
