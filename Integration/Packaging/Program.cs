using System.Security.Cryptography;
using SteamDatabase.ValvePak;

if (args.Length != 2) throw new ArgumentException("Usage: pack <compiled addon directory> <output.vpk>");
var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (!File.Exists(Path.Combine(source, "panorama/layout/ability_draft.vxml_c")))
    throw new InvalidOperationException("The compiled Ability Draft layout is missing.");
foreach (var required in new[] { "panorama/layout/hud_hideout.vxml_c", "panorama/scripts/dw_bootstrap.vjs_c", "panorama/scripts/dw_addon.vjs_c" })
    if (!File.Exists(Path.Combine(source, required)))
        throw new InvalidOperationException("The player VPK must include the Deadworks UI bridge: " + required);
using var package = new Package();
var files = Directory.GetFiles(Path.Combine(source, "panorama"), "*_c", SearchOption.AllDirectories)
    .Concat(Directory.GetFiles(Path.Combine(source, "resource"), "*", SearchOption.AllDirectories)).ToArray();
foreach (var file in files.Order(StringComparer.Ordinal))
    package.AddFile(Path.GetRelativePath(source, file).Replace('\\', '/'), File.ReadAllBytes(file));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
package.Write(output);
var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output)));
File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!, "player-package.json"),
    System.Text.Json.JsonSerializer.Serialize(new { SchemaVersion = 1, IncludesBridge = true, Sha256 = hash, Resources = files.Length }));
Console.WriteLine($"Packed {files.Length} static resources: {output}");
Console.WriteLine($"SHA256 {hash}");
