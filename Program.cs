using System.Text;
using System.Xml;
using System.Globalization;
using CodeWalker.GameFiles; // from CodeWalker.Core

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: ytyp2xml <file-or-folder> [--out <output-folder>] [--overwrite] [--nametable <allnames.nametable>] [--lua]");
            return 2;
        }

        string inPath = args[0];
        string? outRoot = GetOptionValue(args, "--out");
        bool overwrite = args.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        bool writeLua = args.Contains("--lua", StringComparer.OrdinalIgnoreCase);
        string? nameTablePath = GetOptionValue(args, "--nametable");

        try
        {
            // preload nametables so hashes are written as strings in XML
            if (!string.IsNullOrWhiteSpace(nameTablePath) && File.Exists(nameTablePath))
            {
                foreach (var line in File.ReadLines(nameTablePath))
                {
                    var s = line.Trim();
                    if (s.Length > 0) JenkIndex.Ensure(s);
                }
                Console.WriteLine($"Loaded name table: {nameTablePath}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warn] Failed to load nametable: {ex.Message}");
        }

        var inputs = new List<string>();
        if (File.Exists(inPath))
        {
            if (inPath.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase)) inputs.Add(inPath);
            else
            {
                Console.Error.WriteLine($"Input file is not .ytyp: {inPath}");
                return 3;
            }
        }
        else if (Directory.Exists(inPath))
        {
            inputs.AddRange(Directory.GetFiles(inPath, "*.ytyp", SearchOption.AllDirectories));
        }
        else
        {
            Console.Error.WriteLine($"Input not found: {inPath}");
            return 3;
        }

        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("No .ytyp files found.");
            return 4;
        }

        int ok = 0, fail = 0;
        foreach (var ytypPath in inputs)
        {
            try
            {
                var data = File.ReadAllBytes(ytypPath);
                var ytyp = new YtypFile();
                ytyp.Load(data); // CodeWalker.Core API

                // Export to CodeWalker-format XML
                string xml = MetaXml.GetXml(ytyp, out _);

                string outDir = outRoot ?? Path.GetDirectoryName(ytypPath)!;
                Directory.CreateDirectory(outDir);

                // Always write XML unless explicitly skipped? We'll keep XML for parity and as a source for Lua conversion.
                string xmlFile = Path.Combine(outDir, Path.GetFileName(ytypPath) + ".xml"); // foo.ytyp.xml
                if (overwrite || !File.Exists(xmlFile))
                {
                    File.WriteAllText(xmlFile, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    Console.WriteLine($"[ok] {ytypPath} -> {xmlFile}");
                }
                else
                {
                    Console.Error.WriteLine($"[skip] {xmlFile} exists (use --overwrite to replace)");
                }

                if (writeLua)
                {
                    string lua = LuaExporter.FromYtypXml(xml);
                    string luaFile = Path.Combine(outDir, Path.GetFileName(ytypPath) + ".lua"); // foo.ytyp.lua
                    if (!overwrite && File.Exists(luaFile))
                    {
                        Console.Error.WriteLine($"[skip] {luaFile} exists (use --overwrite to replace)");
                    }
                    else
                    {
                        File.WriteAllText(luaFile, lua, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                        Console.WriteLine($"[ok] {ytypPath} -> {luaFile}");
                    }
                }

                ok++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[fail] {ytypPath}: {ex.Message}");
                fail++;
            }
        }

        Console.WriteLine($"Done. Success: {ok}, Failed: {fail}");
        return (fail == 0) ? 0 : 1;
    }

    private static string? GetOptionValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}

internal static class LuaExporter
{
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    public static string FromYtypXml(string xml)
    {
        // Parse the CodeWalker MetaXml for CBaseArchetypeDef entries and serialize to RegisterArchetypes Lua code.
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        // Select all archetype items (CBaseArchetypeDef / CMloArchetypeDef / etc.)
        var archetypeNodes = doc.SelectNodes("//archetypes/*");
        var sb = new StringBuilder();
        sb.AppendLine("RegisterArchetypes(function()");
        sb.AppendLine("\treturn {");

        if (archetypeNodes != null)
        {
            for (int i = 0; i < archetypeNodes.Count; i++)
            {
                var at = archetypeNodes[i] as XmlElement;
                if (at == null) continue;

                // helper local function to read text from child by name (case-insensitive)
                string G(string name)
                {
                    var node = at.SelectSingleNode($"*[translate(local-name(), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')='{name.ToLower()}']") as XmlElement;
                    return node?.InnerText?.Trim() ?? string.Empty;
                }

                // vectors can be represented differently depending on MetaXml; handle common patterns
                (double x, double y, double z) GV(string name)
                {
                    var v = at.SelectSingleNode($"*[translate(local-name(), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')='{name.ToLower()}']") as XmlElement;
                    if (v == null) return (0, 0, 0);

                    double read(XmlElement e, string axis)
                    {
                        // prefer attribute
                        var attr = e.GetAttribute(axis);
                        if (!string.IsNullOrWhiteSpace(attr) && TryParseInvariant(attr, out var val)) return val;
                        // else try child element with attribute 'value' or inner text
                        var c = e.SelectSingleNode(axis) as XmlElement;
                        if (c != null)
                        {
                            var cav = c.GetAttribute("value");
                            if (!string.IsNullOrWhiteSpace(cav) && TryParseInvariant(cav, out var vv)) return vv;
                            if (TryParseInvariant(c.InnerText, out var vvv)) return vvv;
                        }
                        return 0;
                    }

                    return (read(v, "x"), read(v, "y"), read(v, "z"));
                }

                // Numbers
                string flags = G("flags");
                string lodDist = G("lodDist");
                string bsRadius = G("bsRadius");
                string specialAttribute = G("specialAttribute");

                // Strings
                string name = G("name");
                string textureDictionary = G("textureDictionary");
                string physicsDictionary = G("physicsDictionary");
                string assetName = G("assetName");
                string assetType = G("assetType");

                // Vectors
                var bbMin = GV("bbMin");
                var bbMax = GV("bbMax");
                var bsCentre = GV("bsCentre"); // Keep it UK English to please Rockstar North! :D

                // Emit Lua table
                sb.AppendLine("\t\t{");

                void Line(string k, string v, bool isString = false)
                {
                    if (string.IsNullOrEmpty(v)) return;
                    if (isString) sb.AppendLine($"\t\t\t{k} = '{EscapeLua(v)}',");
                    else sb.AppendLine($"\t\t\t{k} = {v},");
                }

                string V3((double x, double y, double z) v) => $"vector3({Fmt(v.x)}, {Fmt(v.y)}, {Fmt(v.z)})";

                Line("flags", SafeNum(flags));
                sb.AppendLine($"\t\t\tbbMin = {V3(bbMin)},");
                sb.AppendLine($"\t\t\tbbMax = {V3(bbMax)},");
                sb.AppendLine($"\t\t\tbsCentre = {V3(bsCentre)},");
                Line("bsRadius", SafeNum(bsRadius));
                Line("name", name, isString: true);
                Line("textureDictionary", textureDictionary, isString: true);
                Line("physicsDictionary", physicsDictionary, isString: true);
                Line("assetName", assetName, isString: true);
                Line("assetType", assetType, isString: true);
                Line("lodDist", SafeNum(lodDist));
                Line("specialAttribute", SafeNum(specialAttribute));

                sb.AppendLine("\t\t},");
            }
        }

        sb.AppendLine("\t}\nend)");
        return sb.ToString();
    }

    private static bool TryParseInvariant(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CI, out v);

    private static string EscapeLua(string s)
    {
        return s.Replace("\\", "\\\\").Replace("'", "\\'");
    }
    private static string Fmt(double d) => d.ToString("0.########", CI);
    private static string SafeNum(string s) => TryParseInvariant(s, out var v) ? v.ToString("0.########", CI) : "0";
}
