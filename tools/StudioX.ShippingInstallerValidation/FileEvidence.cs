namespace StudioX.ShippingInstallerValidation;

using System.Security.Cryptography;
using System.Text.Json;

internal static class FileEvidence
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    internal sealed record Entry(string Path, long Bytes, string Sha256);
    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    internal static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path))
        {
            File.Copy(path, path + ".previous-" + Guid.NewGuid().ToString("N") + ".json");
        }
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    }
    internal static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException("Empty evidence: " + path);
    internal static string Resolve(string root, string relative)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("Unsafe evidence path: " + relative);
        }
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Evidence path escaped its root.");
        }
        for (var cursor = path; cursor.Length >= fullRoot.Length; cursor = Path.GetDirectoryName(cursor) ?? "")
        {
            if (Path.Exists(cursor) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Evidence refuses links: " + cursor);
            }
        }
        return path;
    }
    internal static List<Entry> Capture(string root)
    {
        var entries = new List<Entry>();
        if (!Directory.Exists(root))
        {
            return entries;
        }
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Linked directory: " + directory);
            }
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Linked entry: " + item.FullName);
                }
                if (item is DirectoryInfo child)
                {
                    pending.Push(child.FullName);
                }
                else if (item is FileInfo file)
                {
                    entries.Add(new(Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), file.Length, Hash(file.FullName)));
                    if (entries.Count % 2000 == 0)
                    {
                        Console.WriteLine("Hashed " + entries.Count + " files.");
                    }
                }
            }
        }
        return entries.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }
    internal static void Compare(string root, string snapshot, string output)
    {
        var before = Read<List<Entry>>(snapshot).ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var after = Capture(root).ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var changed = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Where(path =>
            !before.TryGetValue(path, out var old) || !after.TryGetValue(path, out var current) || old.Bytes != current.Bytes || old.Sha256 != current.Sha256).ToArray();
        Write(output, new
        {
            passed = changed.Length == 0,
            files = before.Count,
            changed
        });
        if (changed.Length != 0)
        {
            throw new IOException("Preserved tree changed: " + string.Join(", ", changed.Take(10)));
        }
    }
    internal static void VerifyPayload(string installed, string output, string[] preserved)
    {
        foreach (var prefix in preserved)
        {
            if (!prefix.StartsWith("runtime/toolsets/", StringComparison.Ordinal) && prefix is not ("runtime/hdl/" or "runtime/stc-isp/"))
            {
                throw new ArgumentException("Only explicitly preserved development directories may be excluded.");
            }
        }
        var manifestPath = Path.Combine(installed, "release-files.sha256.json");
        var hashes = Read<Dictionary<string, string>>(manifestPath);
        var verified = 0;
        var skipped = 0;
        foreach (var (relative, expected) in hashes)
        {
            var path = Resolve(installed, relative);
            if (preserved.Any(prefix => relative.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }
            if (!File.Exists(path) || !Hash(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Installed payload differs: " + relative);
            }
            if (++verified % 2000 == 0)
            {
                Console.WriteLine("Verified " + verified + " installed payload files.");
            }
        }
        Write(output, new
        {
            passed = true,
            verifiedFiles = verified,
            preservedFiles = skipped,
            preservedDirectories = preserved,
            manifestSha256 = Hash(manifestPath)
        });
    }
    internal static void Export(string root, string archive, string output)
    {
        if (File.Exists(archive))
        {
            throw new IOException("Preserve the existing evidence export.");
        }
        var selected = Capture(root).Where(entry => !entry.Path.Split('/').Any(part => part is "user-data" or "first_firmware")
            && Path.GetExtension(entry.Path).ToLowerInvariant() is ".json" or ".log" or ".txt" or ".png").ToArray();
        if (selected.Any(entry => entry.Bytes > 256L * 1024 * 1024) || selected.Sum(entry => entry.Bytes) > 4L * 1024 * 1024 * 1024)
        {
            throw new IOException("Evidence export exceeds its declared bounds.");
        }
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var entry in selected)
            {
                System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, Resolve(root, entry.Path), entry.Path, System.IO.Compression.CompressionLevel.Optimal);
            }
            using var writer = new StreamWriter(zip.CreateEntry("evidence-manifest.json").Open());
            writer.Write(JsonSerializer.Serialize(new
            {
                formatVersion = 1,
                createdUtc = DateTimeOffset.UtcNow,
                files = selected
            }, Json));
        }
        Write(output, new
        {
            passed = true,
            fileCount = selected.Length,
            bytes = new FileInfo(archive).Length,
            sha256 = Hash(archive)
        });
    }
}
