using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string root = Path.GetFullPath(args.ElementAtOrDefault(0) ?? ".");
string mode = args.ElementAtOrDefault(1) ?? "check";
if (mode is not ("check" or "inventory")) throw new ArgumentException("Usage: LocalizationMaintenance <repository> [check|inventory]");
var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "PluginSdk/Localization/Strings.en.json")))!;
string Key(string source) => "S" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
var keys = catalog.Keys.ToDictionary(Key, source => source);
var missing = new List<string>();
var candidates = new List<object>();
int references = 0, markup = 0;
bool Chinese(string source) => source.Any(c => c is >= '\u4e00' and <= '\u9fff');
bool Skip(string file) => Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)
    .Any(part => part is "bin" or "obj" or ".git" or "tools" or "upstream" or "upstream2");
bool Localizer(InvocationExpressionSyntax call) => call.Expression.ToString() is "L.T" or "L.F"
    || call.Expression.ToString().EndsWith("UiText.T", StringComparison.Ordinal)
    || call.Expression.ToString().EndsWith("UiText.F", StringComparison.Ordinal);
string? Source(ExpressionSyntax expression)
{
    if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)) return literal.Token.ValueText;
    if (expression is not InterpolatedStringExpressionSyntax interpolation) return null;
    var result = new StringBuilder(); int index = 0;
    foreach (var part in interpolation.Contents)
        if (part is InterpolatedStringTextSyntax text) result.Append(text.TextToken.ValueText);
        else if (part is InterpolationSyntax argument)
        {
            result.Append('{').Append(index++);
            if (argument.AlignmentClause != null) result.Append(',').Append(argument.AlignmentClause.Value);
            if (argument.FormatClause != null) result.Append(':').Append(argument.FormatClause.FormatStringToken.ValueText);
            result.Append('}');
        }
    return result.ToString();
}
foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(file => !Skip(file)))
{
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
    foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(Localizer))
    {
        if (call.ArgumentList.Arguments.FirstOrDefault()?.Expression is not { } argument || Source(argument) is not { } source || !Chinese(source)) continue;
        references++;
        if (!catalog.ContainsKey(source)) missing.Add($"{Path.GetRelativePath(root, file)}:{tree.GetLineSpan(call.Span).StartLinePosition.Line + 1}: {source}");
    }
    if (mode == "inventory")
        foreach (var literal in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>().Where(node => node.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            string source = literal.Token.ValueText;
            if (!Chinese(source) || literal.Ancestors().OfType<InvocationExpressionSyntax>().Any(Localizer)) continue;
            candidates.Add(new { File = Path.GetRelativePath(root, file), Line = tree.GetLineSpan(literal.Span).StartLinePosition.Line + 1, Source = source,
                Catalogued = catalog.ContainsKey(source) });
        }
}
foreach (string file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories).Where(file => !Skip(file)))
    foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\{loc:Text Key=(?<key>S[A-F0-9]+)\}"))
    {
        markup++;
        if (!keys.ContainsKey(match.Groups["key"].Value)) missing.Add(Path.GetRelativePath(root, file) + ": " + match.Value);
    }
var formatting = new Regex(@"(?<!\{)\{\d+(?:,[^}:]+)?(?::[^}]+)?\}");
foreach (var pair in catalog)
{
    string[] original = formatting.Matches(pair.Key).Select(match => match.Value).Order().ToArray();
    string[] translated = formatting.Matches(pair.Value).Select(match => match.Value).Order().ToArray();
    if (pair.Value.Length == 0 || !original.SequenceEqual(translated)) missing.Add("Invalid translation: " + pair.Key);
    if (original.Length > 0) try { CompositeFormat.Parse(pair.Value); } catch (FormatException) { missing.Add("Invalid template: " + pair.Key); }
}
string audit = Path.Combine(root, "bin/LocalizationAudit"); Directory.CreateDirectory(audit);
var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
File.WriteAllText(Path.Combine(audit, "reference-check.json"), JsonSerializer.Serialize(new { Entries = catalog.Count, CodeReferences = references, XamlReferences = markup, Errors = missing }, options));
if (mode == "inventory") File.WriteAllText(Path.Combine(audit, "unmarked-candidates.json"), JsonSerializer.Serialize(candidates, options));
Console.WriteLine($"Catalog: {catalog.Count}; code references: {references}; XAML references: {markup}; errors: {missing.Count}");
foreach (string error in missing.Take(12)) Console.Error.WriteLine(error);
Environment.ExitCode = missing.Count == 0 ? 0 : 1;
