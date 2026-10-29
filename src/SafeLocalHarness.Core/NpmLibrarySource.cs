using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>What the npm registry says about one version, shown to the user before anything is downloaded.</summary>
public sealed record NpmPackageInfo(string Name, string Version, string License, string TarballUrl, string Integrity, long? UnpackedBytes, int? FileCount, string? BrowserFile);

/// <summary>A verified, unpacked package: the files that can go on the shelf, its README and LICENSE text.</summary>
public sealed record NpmFetched(NpmPackageInfo Info, string Folder, long DownloadedBytes, IReadOnlyList<string> Candidates, string? Readme, string? LicenseText, string? DetectedGlobal);

/// <summary>
/// 4-5 (b): libraries from their official source, the npm registry. Reading the registry entry and downloading are
/// separate steps, so the user sees name, version, licence, address and SHA-512 and presses the button before the
/// download. The archive must match the registry's SHA-512; only .js / .css files of the package (and its README and
/// LICENSE) are unpacked, with the same caution as cartridges (entry count, sizes, paths).
/// </summary>
public sealed class NpmLibrarySource(HttpClient http, string downloadRoot)
{
    public const string Registry = "https://registry.npmjs.org/";
    public const long MaxMetadataBytes = 40L * 1024 * 1024;
    public const long MaxTarballBytes = 20L * 1024 * 1024;
    public const long MaxUnpackedBytes = 60L * 1024 * 1024;
    public const int MaxEntries = 5_000;

    /// <summary>An npm package name, scoped ("@pdf-lib/fontkit") or not ("chart.js").</summary>
    public static bool IsValidPackageName(string name) =>
        Regex.IsMatch(name, @"^(@[a-z0-9][a-z0-9._-]{0,39}/)?[a-z0-9][a-z0-9._-]{0,39}$", RegexOptions.CultureInvariant);

    /// <summary>The shelf name of a package: "@pdf-lib/fontkit" becomes "pdf-lib-fontkit" (a folder name without "@" or "/").</summary>
    public static string ShelfName(string packageName) => packageName.TrimStart('@').Replace('/', '-');

    public static string MetadataUrl(string name) => Registry + Uri.EscapeDataString(name).Replace("%40", "@", StringComparison.Ordinal);

    public async Task<NpmPackageInfo> ReadAsync(string name, string? version = null, CancellationToken cancellationToken = default)
    {
        if (!IsValidPackageName(name)) throw new InvalidDataException("パッケージ名は英小文字・数字・. - _ で指定してください（例: chart.js、@pdf-lib/fontkit）。");
        using var response = await http.GetAsync(MetadataUrl(name), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException($"npm に {name} というパッケージは見つかりません。");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxMetadataBytes) throw new InvalidDataException("登録情報が大きすぎます。");
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var chosen = version ?? root.GetProperty("dist-tags").GetProperty("latest").GetString()
                     ?? throw new InvalidDataException("最新の版が分かりません。");
        if (!root.GetProperty("versions").TryGetProperty(chosen, out var entry)) throw new InvalidOperationException($"{name} に版 {chosen} はありません。");
        var dist = entry.GetProperty("dist");
        var license = entry.TryGetProperty("license", out var licenseElement) && licenseElement.ValueKind == JsonValueKind.String ? licenseElement.GetString()! : "（記載なし）";
        var integrity = dist.TryGetProperty("integrity", out var integrityElement) ? integrityElement.GetString() ?? "" : "";
        if (!integrity.StartsWith("sha512-", StringComparison.Ordinal)) throw new InvalidDataException("この版には SHA-512 の記録がないため、取得しません。");
        var tarball = dist.GetProperty("tarball").GetString() ?? "";
        if (!tarball.StartsWith(Registry, StringComparison.Ordinal)) throw new InvalidDataException("配布ファイルの場所が npm の公式の場所ではないため、取得しません。");
        string? browser = null;
        foreach (var field in new[] { "jsdelivr", "unpkg", "browser" })
            if (entry.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String) { browser = value.GetString(); break; }
        return new NpmPackageInfo(name, chosen, license, tarball, integrity,
            dist.TryGetProperty("unpackedSize", out var unpacked) && unpacked.TryGetInt64(out var unpackedBytes) ? unpackedBytes : null,
            dist.TryGetProperty("fileCount", out var count) && count.TryGetInt32(out var files) ? files : null,
            browser is null ? null : NormalizePackagePath(browser));
    }

    /// <summary>Downloads (after the user's approval), checks the SHA-512 and unpacks the usable files into a new folder.</summary>
    public async Task<NpmFetched> FetchAsync(NpmPackageInfo info, CancellationToken cancellationToken = default)
    {
        UserSpaceGuard.EnsureAllowed(downloadRoot);
        using var response = await http.GetAsync(info.TarballUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxTarballBytes) throw new InvalidDataException($"配布ファイルが {MaxTarballBytes / 1024 / 1024} MB を超えるため、取得しません。");
        using var buffer = new MemoryStream();
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxTarballBytes) throw new InvalidDataException($"配布ファイルが {MaxTarballBytes / 1024 / 1024} MB を超えたため、取得をやめました。");
            }
        }
        var bytes = buffer.ToArray();
        var actual = "sha512-" + Convert.ToBase64String(SHA512.HashData(bytes));
        if (actual != info.Integrity) throw new InvalidDataException("取得したファイルの SHA-512 が npm の記録と違うため、使いません。");
        var folder = Path.Combine(downloadRoot, $"{ShelfName(info.Name)}@{info.Version}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(folder);
        var candidates = new List<string>();
        string? readme = null, licenseText = null;
        long unpacked = 0;
        var entries = 0;
        using (var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
            {
                if (++entries > MaxEntries) throw new InvalidDataException("中身のファイルが多すぎます。");
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null) continue;
                unpacked += entry.Length;
                if (unpacked > MaxUnpackedBytes) throw new InvalidDataException("展開すると大きくなりすぎます。");
                var path = NormalizePackagePath(entry.Name);
                if (path is null) continue;
                var fileName = Path.GetFileName(path);
                var isReadme = path.Equals("README.md", StringComparison.OrdinalIgnoreCase);
                var isLicense = Regex.IsMatch(path, @"^LICEN[SC]E(\.md|\.txt)?$", RegexOptions.IgnoreCase);
                var usable = Path.GetExtension(path).ToLowerInvariant() is ".js" or ".css" && entry.Length is > 0 and <= LibraryShelf.MaxFileBytes;
                if (!isReadme && !isLicense && !usable) continue;
                using var content = new MemoryStream();
                await entry.DataStream.CopyToAsync(content, cancellationToken);
                if (isReadme) { readme = Encoding.UTF8.GetString(content.ToArray()); continue; }
                if (isLicense) { licenseText = Encoding.UTF8.GetString(content.ToArray()); continue; }
                // A file keeps its own name; when two folders hold the same name, the later one carries its path (dist_x.js).
                var target = Path.Combine(folder, fileName);
                if (File.Exists(target)) target = Path.Combine(folder, path.Replace('/', '_'));
                if (!ProjectFilePolicy.HasSafeWindowsNames(Path.GetFileName(target)) || File.Exists(target)) continue;
                await File.WriteAllBytesAsync(target, content.ToArray(), cancellationToken);
                candidates.Add(target);
            }
        }
        // The file the package names for browsers comes first.
        var preferred = info.BrowserFile is null ? null : candidates.FirstOrDefault(file => Path.GetFileName(file) == Path.GetFileName(info.BrowserFile) || Path.GetFileName(file) == info.BrowserFile.Replace('/', '_'));
        var ordered = preferred is null ? candidates.Order(StringComparer.Ordinal).ToList() : candidates.Where(file => file != preferred).Order(StringComparer.Ordinal).Prepend(preferred).ToList();
        var detected = preferred is null ? null : DetectGlobal(await File.ReadAllTextAsync(preferred, cancellationToken));
        return new NpmFetched(info, folder, bytes.Length, ordered, readme, licenseText, detected);
    }

    /// <summary>"package/dist/x.js" or "./dist/x.js" → "dist/x.js"; null for paths that leave the package.</summary>
    public static string? NormalizePackagePath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0] == "package") parts.RemoveAt(0);
        if (parts.Count > 0 && parts[0] == ".") parts.RemoveAt(0);
        return parts.Count == 0 || parts.Any(part => part is "." or "..") ? null : string.Join('/', parts);
    }

    /// <summary>The browser global a UMD bundle defines, e.g. "…self).Chart=e()" → Chart; null when it cannot be seen.</summary>
    public static string? DetectGlobal(string script)
    {
        var head = script.Length > 4_000 ? script[..4_000] : script;
        var match = Regex.Match(head, @"(?:globalThis|self|window|this|\))\s*\.\s*([A-Z][A-Za-z0-9_$]{1,39})\s*=\s*[A-Za-z_$][\w$]*\s*\(", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }
}
