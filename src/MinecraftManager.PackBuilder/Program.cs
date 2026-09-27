using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length != 3 || !Uri.TryCreate(args[0], UriKind.Absolute, out var server) || !Guid.TryParse(args[1], out var versionId))
{
    Console.Error.WriteLine("Usage: MinecraftManager.PackBuilder <server-url> <draft-version-id> <local-root>");
    return 2;
}
var token = Environment.GetEnvironmentVariable("MINECRAFT_MANAGER_ADMIN_TOKEN");
if (string.IsNullOrWhiteSpace(token)) { Console.Error.WriteLine("Set MINECRAFT_MANAGER_ADMIN_TOKEN in the process environment."); return 2; }
var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[2]));
if (!Directory.Exists(root) || IsLink(root)) { Console.Error.WriteLine("The local root is missing or is a symbolic link/reparse point."); return 2; }
var excluded = new[] { "saves/", "screenshots/", "logs/", "crash-reports/", ".git/", "bin/", "obj/" };
using var http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromMinutes(10) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
var paths = new List<string>();
foreach (var fullPath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
{
    if (IsLink(fullPath) || HasLinkedAncestor(root, fullPath)) continue;
    var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/').Normalize(NormalizationForm.FormC);
    if (relative.StartsWith("../", StringComparison.Ordinal) || excluded.Any(x => relative.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
    paths.Add(relative);
}
var collisions = paths.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToArray();
if (collisions.Length > 0) { Console.Error.WriteLine($"Case-colliding paths: {string.Join(", ", collisions)}"); return 1; }
foreach (var relative in paths.Order(StringComparer.Ordinal))
{
    var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    var before = new FileInfo(fullPath); var beforeLength = before.Length; var beforeWrite = before.LastWriteTimeUtc;
    await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
    var digest = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant(); input.Position = 0;
    using var upload = new HttpRequestMessage(HttpMethod.Post, "api/v1/admin/files") { Content = new StreamContent(input) };
    upload.Headers.Add("X-Content-SHA256", digest); upload.Content.Headers.ContentLength = beforeLength;
    using var uploadResponse = await http.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead); uploadResponse.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync()); var blobId = document.RootElement.GetProperty("blobId").GetGuid();
    var after = new FileInfo(fullPath); if (after.Length != beforeLength || after.LastWriteTimeUtc != beforeWrite) throw new IOException($"File changed during import: {relative}");
    using var attach = await http.PostAsync($"api/v1/admin/pack-versions/{versionId}/files", JsonContent.Create(new { path = relative, blobId, contentType = "application/octet-stream" })); attach.EnsureSuccessStatusCode();
    Console.WriteLine($"Added {relative} ({beforeLength} bytes, {digest})");
}
Console.WriteLine($"Draft updated with {paths.Count} files. Review the server manifest preview before publishing.");
return 0;

static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
static bool HasLinkedAncestor(string root, string path)
{
    var directory = Directory.GetParent(path);
    while (directory is not null && !directory.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)) { if (IsLink(directory.FullName)) return true; directory = directory.Parent; }
    return false;
}
