using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Baka;

/// What the browser shows about one installed extension. `Folder` is where its files live on disk,
/// which is also where the popup, options page and icon are read from.
sealed record ExtensionInfo(string Id, string Name, bool Enabled, string Version, string? PopupPage,
    string? OptionsPage, string? IconFile, string? Folder, bool FromWebStore);

/// What a Web Store package asks for, read from its manifest before anything is installed.
sealed record ExtensionPackage(string Id, string Name, string Version, string Folder, IReadOnlyList<string> Permissions);

/// The browser side of extension management. Settings and the toolbar menu talk to this, so neither
/// needs to know about WebView2 profiles.
interface IExtensionHost
{
    bool ExtensionsSupported { get; }
    Task<IReadOnlyList<ExtensionInfo>> ListExtensionsAsync();
    Task SetExtensionEnabledAsync(string id, bool enabled);
    Task RemoveExtensionAsync(string id);
    Task<string> InstallExtensionFolderAsync(string folder);
    void OpenWebStore();
    void OpenExtensionOptions(string id);
}

/// Chrome Web Store support. WebView2 can run Chrome extensions but cannot install them from the store,
/// because the store's "Add to Chrome" button talks to Chrome itself. This does what Chrome does: it asks
/// Google's update service for the signed .crx package, checks the package signature belongs to the
/// extension ID on the store page, unpacks it into the browser's data folder, and hands the unpacked
/// folder to WebView2.
static class WebStore
{
    public const string Home = "https://chromewebstore.google.com/";
    const long MaxPackageBytes = 160L * 1024 * 1024;
    const long MaxUnpackedBytes = 400L * 1024 * 1024;
    const int MaxEntries = 20_000;

    static readonly Regex IdPattern = new("^[a-p]{32}$", RegexOptions.CultureInvariant);

    public static string ExtensionsRoot => App.Path_("extensions");
    static string IndexFile => App.Path_("extensions", "index.json");

    /// The 32-letter extension ID of a Web Store detail page, or null for any other address.
    /// Accepts the current store (chromewebstore.google.com/detail/name/id) and the old one
    /// (chrome.google.com/webstore/detail/name/id).
    public static string? IdFromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int detail;
        if (uri.Host.Equals("chromewebstore.google.com", StringComparison.OrdinalIgnoreCase))
            detail = Array.FindIndex(segments, s => s.Equals("detail", StringComparison.OrdinalIgnoreCase));
        else if (uri.Host.Equals("chrome.google.com", StringComparison.OrdinalIgnoreCase) &&
                 segments.Length > 1 && segments[0].Equals("webstore", StringComparison.OrdinalIgnoreCase))
            detail = Array.FindIndex(segments, s => s.Equals("detail", StringComparison.OrdinalIgnoreCase));
        else return null;
        if (detail < 0) return null;
        for (var i = segments.Length - 1; i > detail; i--)
        {
            var candidate = segments[i].ToLowerInvariant();
            if (IdPattern.IsMatch(candidate)) return candidate;
        }
        return null;
    }

    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    /// Chrome's ID scheme: the first 16 bytes of SHA-256 over the public key, each hex digit 0-f
    /// written as a letter a-p.
    public static string IdFromPublicKey(byte[] publicKey) => IdFromBytes(SHA256.HashData(publicKey).AsSpan(0, 16));

    static string IdFromBytes(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(32);
        foreach (var b in bytes)
        {
            sb.Append((char)('a' + (b >> 4)));
            sb.Append((char)('a' + (b & 0xF)));
        }
        return sb.ToString();
    }

    public static string DownloadUrl(string id, string browserVersion) =>
        "https://clients2.google.com/service/update2/crx?response=redirect&acceptformat=crx2,crx3" +
        $"&prodversion={Uri.EscapeDataString(browserVersion)}&x=id%3D{id}%26installsource%3Dondemand%26uc";

    /// The major.minor.build.patch part of the WebView2 runtime version, which Google's update service
    /// uses to pick a package the engine can run.
    public static string EngineVersion(string? runtimeVersion)
    {
        var token = (runtimeVersion ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return Version.TryParse(token, out var v) ? v.ToString() : "130.0.0.0";
    }

    /// Downloads, verifies and unpacks a store extension into a staging folder. Nothing is installed
    /// yet: the caller shows the permissions first and only then hands the folder to WebView2.
    public static async Task<ExtensionPackage> DownloadAsync(string id, string browserVersion, CancellationToken token = default)
    {
        if (!IsValidId(id)) throw new ArgumentException("That is not a Chrome Web Store extension ID.", nameof(id));
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 6 })
        {
            Timeout = TimeSpan.FromSeconds(90),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{browserVersion} Safari/537.36");
        using var response = await http.GetAsync(DownloadUrl(id, browserVersion), HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            throw new InvalidDataException("The Chrome Web Store has no package for this extension. It may have been removed, or it may not support this browser version.");
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The extension download was redirected away from HTTPS and was stopped.");
        if (response.Content.Headers.ContentLength is > MaxPackageBytes)
            throw new InvalidDataException("The extension package is larger than this browser accepts.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxPackageBytes) throw new InvalidDataException("The extension package is larger than this browser accepts.");
        }
        var staging = Path.Combine(ExtensionsRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Unpack(buffer.ToArray(), staging, id);
            return ReadPackage(id, staging);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// Moves a staged package into its permanent folder (replacing an older copy) and returns that folder.
    public static string Commit(ExtensionPackage package)
    {
        var final = Path.Combine(ExtensionsRoot, package.Id);
        if (Directory.Exists(final)) Directory.Delete(final, recursive: true);
        Directory.Move(package.Folder, final);
        return final;
    }

    public static void Discard(ExtensionPackage package) => TryDelete(package.Folder);

    /// Parses a .crx (version 3, or the old version 2), verifies its signature, and extracts the zip inside
    /// to `destination`. When `expectedId` is given, the package must be signed by the key that ID belongs
    /// to; anything else is refused. The key is written into manifest.json so WebView2 gives the unpacked
    /// copy the same ID the store uses, which is what extensions that check their own ID expect.
    public static void Unpack(byte[] crx, string destination, string? expectedId = null)
    {
        if (crx.Length < 16 || crx[0] != 'C' || crx[1] != 'r' || crx[2] != '2' || crx[3] != '4')
            throw new InvalidDataException("The download is not a Chrome extension package (.crx).");
        var version = BitConverter.ToUInt32(crx, 4);
        byte[] publicKey;
        int zipStart;
        if (version == 3)
        {
            var headerSize = (int)BitConverter.ToUInt32(crx, 8);
            if (headerSize <= 0 || 12L + headerSize > crx.Length) throw new InvalidDataException("The extension package header is damaged.");
            var header = crx.AsSpan(12, headerSize);
            zipStart = 12 + headerSize;
            publicKey = VerifyCrx3(header, crx.AsSpan(zipStart), expectedId);
        }
        else if (version == 2)
        {
            var keyLength = (int)BitConverter.ToUInt32(crx, 8);
            var signatureLength = (int)BitConverter.ToUInt32(crx, 12);
            if (keyLength <= 0 || signatureLength <= 0 || 16L + keyLength + signatureLength > crx.Length)
                throw new InvalidDataException("The extension package header is damaged.");
            publicKey = crx.AsSpan(16, keyLength).ToArray();
            var signature = crx.AsSpan(16 + keyLength, signatureLength);
            zipStart = 16 + keyLength + signatureLength;
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            if (!rsa.VerifyData(crx.AsSpan(zipStart), signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1))
                throw new CryptographicException("The extension package signature is not valid.");
            if (expectedId is not null && IdFromPublicKey(publicKey) != expectedId)
                throw new CryptographicException("The extension package is not signed for this extension ID.");
        }
        else throw new InvalidDataException($"Chrome extension package version {version} is not supported.");

        ExtractZip(crx.AsSpan(zipStart).ToArray(), destination);
        AddKeyToManifest(destination, publicKey);
    }

    /// CRX3: a protobuf CrxFileHeader. Field 2 holds RSA proofs (public key + signature), field 3 ECDSA
    /// proofs, field 10000 the signed header data whose field 1 is the 16-byte CRX ID. The signature covers
    /// "CRX3 SignedData\0", the signed-data length, the signed data, and the zip archive.
    static byte[] VerifyCrx3(ReadOnlySpan<byte> header, ReadOnlySpan<byte> archive, string? expectedId)
    {
        var rsaProofs = new List<(byte[] Key, byte[] Signature)>();
        var ecdsaProofs = new List<(byte[] Key, byte[] Signature)>();
        byte[]? signedData = null;
        foreach (var (field, value) in Protobuf.Fields(header))
        {
            if (field == 2 || field == 3)
            {
                byte[]? key = null, signature = null;
                foreach (var (f, v) in Protobuf.Fields(value))
                {
                    if (f == 1) key = v.ToArray();
                    else if (f == 2) signature = v.ToArray();
                }
                if (key is null || signature is null) continue;
                (field == 2 ? rsaProofs : ecdsaProofs).Add((key, signature));
            }
            else if (field == 10000) signedData = value.ToArray();
        }
        if (signedData is null) throw new InvalidDataException("The extension package has no signed ID.");
        byte[]? crxId = null;
        foreach (var (f, v) in Protobuf.Fields(signedData)) if (f == 1) crxId = v.ToArray();
        if (crxId is not { Length: 16 }) throw new InvalidDataException("The extension package ID is damaged.");
        var id = IdFromBytes(crxId);
        if (expectedId is not null && id != expectedId)
            throw new CryptographicException("The extension package is not signed for this extension ID.");

        var message = new byte[16 + 4 + signedData.Length + archive.Length];
        Encoding.ASCII.GetBytes("CRX3 SignedData\0").CopyTo(message, 0);
        BitConverter.GetBytes((uint)signedData.Length).CopyTo(message, 16);
        signedData.CopyTo(message, 20);
        archive.CopyTo(message.AsSpan(20 + signedData.Length));

        // The developer key is the one whose hash is the ID. The store adds its own proof too; both are checked.
        byte[]? developerKey = null;
        foreach (var (key, signature) in rsaProofs)
        {
            using var rsa = RSA.Create();
            try { rsa.ImportSubjectPublicKeyInfo(key, out _); } catch { continue; }
            if (!rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new CryptographicException("The extension package signature is not valid.");
            if (IdFromPublicKey(key) == id) developerKey = key;
        }
        foreach (var (key, signature) in ecdsaProofs)
        {
            using var ecdsa = ECDsa.Create();
            try { ecdsa.ImportSubjectPublicKeyInfo(key, out _); } catch { continue; }
            if (!ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new CryptographicException("The extension package signature is not valid.");
            if (IdFromPublicKey(key) == id) developerKey = key;
        }
        return developerKey ?? throw new CryptographicException("The extension package is not signed by the key its ID belongs to.");
    }

    static void ExtractZip(byte[] zip, string destination)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("The extension package has too many files.");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            // zip-slip guard: every entry has to land inside the destination folder
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('\\', '/')));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The extension package tried to write outside its folder.");
            if (entry.FullName.EndsWith('/') || entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
            total += entry.Length;
            if (total > MaxUnpackedBytes) throw new InvalidDataException("The unpacked extension is larger than this browser accepts.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
        if (!File.Exists(Path.Combine(root, "manifest.json")))
            throw new InvalidDataException("The extension package has no manifest.json.");
    }

    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    static void AddKeyToManifest(string folder, byte[] publicKey)
    {
        var path = Path.Combine(folder, "manifest.json");
        var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) as JsonObject
            ?? throw new InvalidDataException("The extension manifest is not a JSON object.");
        node["key"] = Convert.ToBase64String(publicKey);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    public static JsonObject? ReadManifest(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "manifest.json");
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
            return JsonNode.Parse(text, documentOptions: Lenient) as JsonObject;
        }
        catch { return null; }
    }

    /// Resolves "__MSG_name__" strings from the extension's default locale, the way Chrome shows them.
    public static string Localize(string folder, JsonObject manifest, string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var m = Regex.Match(value, "^__MSG_(.+)__$");
        if (!m.Success) return value;
        var key = m.Groups[1].Value;
        var locales = new List<string>();
        if (manifest["default_locale"]?.GetValue<string>() is { Length: > 0 } def) locales.Add(def);
        locales.AddRange(["en", "en_US", "en_GB"]);
        foreach (var locale in locales.Distinct())
        {
            try
            {
                var file = Path.Combine(folder, "_locales", locale, "messages.json");
                if (!File.Exists(file)) continue;
                if (JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient) is not JsonObject messages) continue;
                foreach (var (name, entry) in messages)
                    if (name.Equals(key, StringComparison.OrdinalIgnoreCase) && entry?["message"]?.GetValue<string>() is { } text)
                        return text;
            }
            catch { }
        }
        return key;
    }

    static ExtensionPackage ReadPackage(string id, string folder)
    {
        var manifest = ReadManifest(folder) ?? throw new InvalidDataException("The extension manifest could not be read.");
        var name = Localize(folder, manifest, manifest["name"]?.GetValue<string>());
        var version = manifest["version"]?.GetValue<string>() ?? "";
        return new ExtensionPackage(id, name.Length > 0 ? name : id, version, folder, DescribePermissions(manifest));
    }

    /// Plain-language list of what an extension can do, for the confirmation before installing.
    public static IReadOnlyList<string> DescribePermissions(JsonObject manifest)
    {
        var raw = new List<string>();
        foreach (var key in new[] { "permissions", "host_permissions", "optional_permissions" })
            if (manifest[key] is JsonArray list)
                foreach (var item in list) if (item?.GetValue<string>() is { } s) raw.Add(s);
        if (manifest["content_scripts"] is JsonArray scripts)
            foreach (var script in scripts)
                if (script?["matches"] is JsonArray matches)
                    foreach (var item in matches) if (item?.GetValue<string>() is { } s) raw.Add(s);

        var result = new List<string>();
        var hosts = raw.Where(p => p.Contains("://") || p == "<all_urls>").Distinct().ToList();
        if (hosts.Any(h => h is "<all_urls>" or "*://*/*" or "http://*/*" or "https://*/*"))
            result.Add("Read and change all your data on all websites");
        else if (hosts.Count > 0)
            result.Add("Read and change your data on " + string.Join(", ", hosts.Take(4).Select(Host)) + (hosts.Count > 4 ? $" and {hosts.Count - 4} more sites" : ""));
        var api = raw.Where(p => !p.Contains("://") && p != "<all_urls>").Distinct().ToHashSet(StringComparer.Ordinal);
        void Say(string permission, string text) { if (api.Contains(permission)) result.Add(text); }
        Say("tabs", "Read your browsing history");
        Say("history", "Read and change your browsing history");
        Say("bookmarks", "Read and change your bookmarks");
        Say("downloads", "Manage your downloads");
        Say("cookies", "Read and change cookies");
        Say("clipboardRead", "Read data you copy and paste");
        Say("clipboardWrite", "Change data you copy and paste");
        Say("nativeMessaging", "Talk to programs installed on this PC");
        Say("webRequest", "Watch the pages your browser loads");
        Say("declarativeNetRequest", "Block or change page content");
        Say("declarativeNetRequestWithHostAccess", "Block or change page content");
        Say("proxy", "Change your proxy settings");
        Say("privacy", "Change privacy-related settings");
        Say("management", "Manage your other extensions");
        Say("notifications", "Show notifications");
        Say("geolocation", "Know your location");
        Say("scripting", "Run scripts on pages it can access");
        if (result.Count == 0) result.Add("No special permissions");
        return result;
    }

    static string Host(string pattern)
    {
        var start = pattern.IndexOf("://", StringComparison.Ordinal);
        var rest = start >= 0 ? pattern[(start + 3)..] : pattern;
        var slash = rest.IndexOf('/');
        return slash >= 0 ? rest[..slash] : rest;
    }

    /// The page to open for the toolbar popup, the options page, and the best icon file, from the manifest.
    public static (string? Popup, string? Options, string? Icon) Pages(string folder)
    {
        if (ReadManifest(folder) is not { } manifest) return (null, null, null);
        string? Get(JsonNode? node) { try { return node?.GetValue<string>(); } catch { return null; } }
        var popup = Get(manifest["action"]?["default_popup"]) ?? Get(manifest["browser_action"]?["default_popup"])
            ?? Get(manifest["page_action"]?["default_popup"]);
        var options = Get(manifest["options_ui"]?["page"]) ?? Get(manifest["options_page"]);
        string? icon = null;
        foreach (var source in new[] { manifest["icons"], manifest["action"]?["default_icon"], manifest["browser_action"]?["default_icon"] })
        {
            if (source is JsonValue v && Get(v) is { } single) { icon = single; break; }
            if (source is JsonObject sizes)
            {
                var best = sizes.Select(p => (Size: int.TryParse(p.Key, out var n) ? n : 0, File: Get(p.Value)))
                    .Where(p => p.File is not null).OrderBy(p => Math.Abs(p.Size - 32)).FirstOrDefault();
                if (best.File is not null) { icon = best.File; break; }
            }
        }
        static string? Clean(string? page) => string.IsNullOrWhiteSpace(page) ? null : page.TrimStart('/');
        return (Clean(popup), Clean(options), Clean(icon));
    }

    // ---------- index: WebView2 extension ID -> folder on disk ----------

    public static Dictionary<string, string> LoadIndex()
    {
        try
        {
            if (File.Exists(IndexFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IndexFile)) ?? [];
        }
        catch { }
        return [];
    }

    public static void SaveIndex(Dictionary<string, string> index)
    {
        try
        {
            Directory.CreateDirectory(ExtensionsRoot);
            var temp = IndexFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, IndexFile, overwrite: true);
        }
        catch { }
    }

    /// True when the folder is one this browser unpacked (and so may delete on removal).
    public static bool IsManagedFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        var root = Path.GetFullPath(ExtensionsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(folder).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static void TryDelete(string? folder)
    {
        if (!IsManagedFolder(folder)) return;
        try { if (Directory.Exists(folder)) Directory.Delete(folder!, recursive: true); } catch { }
    }

    /// Leftover staging folders from an install that was interrupted.
    public static void CleanStaging()
    {
        try
        {
            if (!Directory.Exists(ExtensionsRoot)) return;
            foreach (var dir in Directory.GetDirectories(ExtensionsRoot, ".staging-*")) TryDelete(dir);
        }
        catch { }
    }
}

/// Just enough protobuf to read a CRX3 header: length-delimited fields, skipping varints and fixed widths.
static class Protobuf
{
    // Explicit ReadOnlyMemory: a byte[] argument would otherwise bind back to this span overload and recurse.
    public static List<(int Field, ReadOnlyMemory<byte> Value)> Fields(ReadOnlySpan<byte> data) => Fields(new ReadOnlyMemory<byte>(data.ToArray()));

    public static List<(int Field, ReadOnlyMemory<byte> Value)> Fields(ReadOnlyMemory<byte> memory)
    {
        var result = new List<(int, ReadOnlyMemory<byte>)>();
        var data = memory.Span;
        var pos = 0;
        while (pos < data.Length)
        {
            var tag = ReadVarint(data, ref pos);
            var field = (int)(tag >> 3);
            switch ((int)(tag & 7))
            {
                case 0: ReadVarint(data, ref pos); break;
                case 1: pos += 8; break;
                case 2:
                    var length = (int)ReadVarint(data, ref pos);
                    if (length < 0 || pos + length > data.Length) throw new InvalidDataException("The extension package header is damaged.");
                    result.Add((field, memory.Slice(pos, length)));
                    pos += length;
                    break;
                case 5: pos += 4; break;
                default: throw new InvalidDataException("The extension package header is damaged.");
            }
            if (pos > data.Length) throw new InvalidDataException("The extension package header is damaged.");
        }
        return result;
    }

    static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (pos >= data.Length) throw new InvalidDataException("The extension package header is damaged.");
            var b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("The extension package header is damaged.");
    }

    // Writers, used by the self-check to build a signed test package.
    public static void WriteBytes(Stream s, int field, byte[] value)
    {
        WriteVarint(s, ((ulong)field << 3) | 2);
        WriteVarint(s, (ulong)value.Length);
        s.Write(value);
    }

    static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80) { s.WriteByte((byte)(value | 0x80)); value >>= 7; }
        s.WriteByte((byte)value);
    }
}
