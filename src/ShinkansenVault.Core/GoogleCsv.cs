using System.Text;

namespace ShinkansenVault;

/// <summary>One row of a Google Password Manager export (columns: name,url,username,password,note).</summary>
public sealed record GoogleRow(string Name, string Url, string Username, string Password, string Note);

/// <summary>
/// Reads and writes the CSV format of Google Password Manager (passwords.google.com and Chrome).
/// Export:  Google Password Manager → Settings → Export passwords.
/// Import:  Google Password Manager → Settings → Import passwords.
/// </summary>
public static class GoogleCsv
{
    public static readonly string[] Columns = { "name", "url", "username", "password", "note" };

    public static List<GoogleRow> Parse(string text)
    {
        var records = ReadRecords(text);
        if (records.Count == 0) throw new VaultException("The CSV file is empty.");
        var header = records[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name);
        int iName = Col("name"), iUrl = Col("url"), iUser = Col("username"), iPass = Col("password"), iNote = Col("note");
        if (iUrl < 0 || iUser < 0 || iPass < 0)
            throw new VaultException("This is not a Google Password Manager export (expected columns name,url,username,password,note).");

        string Get(List<string> r, int i) => i >= 0 && i < r.Count ? r[i] : "";
        var rows = new List<GoogleRow>();
        foreach (var r in records.Skip(1))
        {
            if (r.Count == 1 && r[0].Length == 0) continue; // blank line
            var pass = Get(r, iPass);
            if (pass.Length == 0) continue; // nothing to store (e.g. "never save" sites)
            rows.Add(new GoogleRow(Get(r, iName), Get(r, iUrl), Get(r, iUser), pass, Get(r, iNote)));
        }
        return rows;
    }

    public static string Write(IEnumerable<GoogleRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Columns)).Append("\r\n");
        foreach (var r in rows)
            sb.Append(string.Join(",", new[] { r.Name, r.Url, r.Username, r.Password, r.Note }.Select(Quote))).Append("\r\n");
        return sb.ToString();
    }

    private static string Quote(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || s.StartsWith(' ') || s.EndsWith(' ')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    /// <summary>RFC 4180 reader: quoted fields, doubled quotes, newlines inside quotes, optional BOM.</summary>
    private static List<List<string>> ReadRecords(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': record.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    record.Add(field.ToString()); field.Clear();
                    records.Add(record); record = new List<string>();
                    break;
                default: field.Append(c); break;
            }
        }
        if (inQuotes) throw new VaultException("The CSV file is malformed (unterminated quote).");
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }

    /// <summary>Builds a reference path like <c>google/github.com</c> from a login URL.</summary>
    public static string RefBase(string url, string name)
    {
        string host = "";
        if (url.StartsWith("android://", StringComparison.OrdinalIgnoreCase))
        {
            // android://<hash>@com.example.app/
            var at = url.IndexOf('@');
            host = "android." + (at >= 0 ? url[(at + 1)..] : url["android://".Length..]).Trim('/');
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
        {
            host = u.Host;
            if (!u.IsDefaultPort) host += "-" + u.Port;
        }
        if (host.Length == 0) host = name;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        var clean = new string(host.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '-').ToArray()).Trim('-', '.', '_');
        if (clean.Length == 0) clean = "site";
        if (clean.Length > 150) clean = clean[..150];
        return "google/" + clean;
    }

    /// <summary>Key used to match the same login across imports.</summary>
    public static string MatchKey(string url, string username) =>
        url.Trim().TrimEnd('/').ToLowerInvariant() + "\n" + username.Trim().ToLowerInvariant();
}
