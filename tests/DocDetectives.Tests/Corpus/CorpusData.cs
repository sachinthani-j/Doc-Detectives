using DocDetectives.Domain.Entities;
using System.Text;

namespace DocDetectives.Tests.Corpus;

public sealed record GoldenRow(
    string File,
    string DocClass,
    string? Manufacturer,
    IReadOnlyList<string> Models,
    string? AssetType,
    IReadOnlyList<string> ExpectedAssetIds,
    string ExpectedStatus)
{
    /// <summary>Metadata-only manual (no PDF): this is what the matching skill receives after extraction.</summary>
    public ExtractedManual ToManual() => new(
        ManualId: Path.GetFileNameWithoutExtension(File).ToLowerInvariant().Replace(' ', '_'),
        FileName: File,
        MarkdownContent: $"# {Path.GetFileNameWithoutExtension(File)}\n{Manufacturer} {string.Join(", ", Models)}\n{AssetType}",
        Manufacturer: Manufacturer,
        ModelNumbers: Models,
        EquipmentCategory: AssetType,
        RawJsonResponse: "{}");
}

/// <summary>Loads dataset/T1_corpus_golden_set_synthetic.csv and dataset/sample_synthetic_assets.csv.</summary>
public static class CorpusData
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "TestData");

    public static IReadOnlyList<GoldenRow> Golden { get; } = LoadGolden();
    public static IReadOnlyList<Asset> Assets { get; } = LoadAssets();

    public static GoldenRow Row(string file) => Golden.Single(r => r.File == file);

    public static IEnumerable<object[]> FilesWithStatus(string status) =>
        Golden.Where(r => r.ExpectedStatus == status).Select(r => new object[] { r.File });

    public static IEnumerable<object[]> AllFiles() => Golden.Select(r => new object[] { r.File });

    private static IReadOnlyList<GoldenRow> LoadGolden()
    {
        var (h, rows) = ReadCsv(Path.Combine(Dir, "T1_corpus_golden_set_synthetic.csv"));
        return rows.Select(r => new GoldenRow(
            File: r[h["file"]],
            DocClass: r[h["doc_class"]],
            Manufacturer: NullIfEmpty(r[h["manufacturer"]]),
            Models: r[h["model"]].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AssetType: NullIfEmpty(r[h["asset_type"]]),
            ExpectedAssetIds: r[h["expected_asset_id"]].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ExpectedStatus: r[h["expected_status"]])).ToList();
    }

    private static IReadOnlyList<Asset> LoadAssets()
    {
        var (h, rows) = ReadCsv(Path.Combine(Dir, "sample_synthetic_assets.csv"));
        return rows.Select(r => new Asset(
            Id: r[h["EqmId"]],
            Name: r[h["EqmDescr"]],
            Manufacturer: r[h["ManufacturerName"]],
            ModelNumber: r[h["EqmModel"]],
            SerialNumber: r[h["EqmSerialNr"]],
            Category: r[h["Category"]],
            ParentAssetId: NullIfEmpty(r[h["EqmPartOfEqmId"]]))).ToList();
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static (Dictionary<string, int> Header, List<List<string>> Rows) ReadCsv(string path)
    {
        var all = Parse(File.ReadAllText(path));
        var header = all[0].Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        return (header, all.Skip(1).Where(r => r.Count > 1).ToList());
    }

    // Minimal RFC 4180 parser: quoted fields, doubled quotes, newlines inside quotes.
    private static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
