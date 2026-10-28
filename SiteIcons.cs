using System.Security.Cryptography;
using System.Text;

namespace Baka;

/// Small on-disk cache of site icons by host, so bookmarks show the site's own icon. Icons are saved
/// only when a regular (non-private) tab has already loaded the favicon; nothing is fetched for this.
static class SiteIcons
{
    static readonly Dictionary<string, Image?> _memory = new(StringComparer.OrdinalIgnoreCase);
    static string Folder => App.Path_("favicons");

    static string? Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.Host.ToLowerInvariant() : null;

    static string FileFor(string host) =>
        Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host)))[..24] + ".png");

    public static void Save(string? url, Image icon)
    {
        if (Host(url) is not { } host) return;
        try
        {
            Directory.CreateDirectory(Folder);
            var path = FileFor(host);
            var temp = path + ".tmp";
            icon.Save(temp, System.Drawing.Imaging.ImageFormat.Png);
            File.Move(temp, path, overwrite: true);
            if (_memory.TryGetValue(host, out var old)) old?.Dispose();
            _memory[host] = new Bitmap(icon);
        }
        catch { }
    }

    public static Image? For(string? url)
    {
        if (Host(url) is not { } host) return null;
        if (_memory.TryGetValue(host, out var cached)) return cached;
        Image? image = null;
        try
        {
            var path = FileFor(host);
            if (File.Exists(path))
            {
                // copied so the file is not held open
                using var fromFile = Image.FromFile(path);
                image = new Bitmap(fromFile);
            }
        }
        catch { image = null; }
        _memory[host] = image;
        return image;
    }
}
