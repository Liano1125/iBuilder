// gameapp/GameAppExport.cs: a built game exported as its own .ipa, and that .ipa's "player" mode.
//
// The export is a copy of THIS app's bundle (the same executable, host and runtime) with the built game inside it:
//   Payload/<Name>.app/Frameworks/DmGameProbe.framework   the framework the chain assembled (re-signed by the installer)
//   Payload/<Name>.app/ExportedGame/extract/flat/...      the rewritten modules and their tables (read at the boot)
//   Payload/<Name>.app/ExportedGame/extract/RomFS.img     the game's files
//   Payload/<Name>.app/ExportedGame/shaders/<id>/...      its shader library
//   Payload/<Name>.app/ExportedGame/documents/...         profile.tsv, games/<id>/ (settings, saves), profile-derived/<id>/
//   Payload/<Name>.app/ExportedGame/manifest.tsv          title id, dump name, display name, app build
// and its own Info.plist: a bundle identifier of its own (installed beside GameApp), the game's name and icon.
//
// Nothing is signed here: SideStore, AltStore or FlareStore re-sign every executable of an .ipa at install, the app
// and Frameworks/ alike. Installed, the app finds ExportedGame/manifest.tsv in its bundle and is a player: it lists that
// one game, boots it from the bundle (GameAppMain.ConfigText), and offers no import, build, rebuild or delete.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

using CoreGraphics;
using Foundation;
using ImageIO;
using UIKit;

internal static class GameExport
{
    public const string Folder = "ExportedGame";

    // ---- player mode: the installed export --------------------------------------------------------------------

    private static Dictionary<string, string> manifest;
    private static bool manifestRead;

    /// The bundle's ExportedGame/manifest.tsv as KEY<TAB>value, or null in GameApp itself.
    public static Dictionary<string, string> PlayerManifest
    {
        get
        {
            if (manifestRead) return manifest;
            manifestRead = true;
            try
            {
                var path = Path.Combine(NSBundle.MainBundle.BundlePath, Folder, "manifest.tsv");
                if (File.Exists(path)) manifest = GameAppUi.ParseExportManifest(File.ReadAllText(path));
            }
            catch (Exception) { manifest = null; }
            return manifest;
        }
    }

    public static bool IsPlayer => PlayerManifest != null && PlayerManifest.ContainsKey("dump");

    public static string PlayerDump => IsPlayer ? PlayerManifest["dump"] : null;

    public static string BundleGame => Path.Combine(NSBundle.MainBundle.BundlePath, Folder);

    public static string BundleFramework =>
        Path.Combine(NSBundle.MainBundle.BundlePath, "Frameworks", "DmGameProbe.framework", "DmGameProbe");

    /// First launch of an installed export (and every launch after): the game's small, writable files go into
    /// Documents when they are not there yet (the player's own settings and saves are never overwritten), and the
    /// framework stamp says this build made the framework in the bundle, for this game.
    public static void PreparePlayerDocuments(string documents, Action<string> log)
    {
        if (!IsPlayer) return;
        var source = Path.Combine(BundleGame, "documents");
        int copied = 0;
        try
        {
            if (Directory.Exists(source))
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(source, file);
                    var dest = Path.Combine(documents, rel);
                    if (File.Exists(dest)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(file, dest);
                    copied++;
                }
            File.WriteAllText(Path.Combine(documents, "gameapp.framework"),
                              $"app_build={GameAppBuild.Id}\ngame={PlayerDump}\nbuilt={(PlayerManifest.TryGetValue("exported", out var e) ? e : "")}\n");
            if (copied > 0) log?.Invoke($"exported game: {copied} file(s) set up in Documents (settings, saves, profile) for {PlayerDump}");
        }
        catch (Exception error) { log?.Invoke($"exported game: Documents could not be set up: {error.Message}"); }
    }

    // ---- a slim export's game files (GameAppUi.RomfsFileName) --------------------------------------------------

    /// The game files' name this export asks for, or null when they are inside the bundle (a full export).
    public static string PlayerRomfsName =>
        IsPlayer && PlayerManifest.TryGetValue("romfs_file", out var f) && !File.Exists(Path.Combine(BundleGame, "extract", "RomFS.img")) ? f : null;

    private static string RomfsBookmarkPath => Path.Combine(GameAppMain.Documents, "..", "Library", "romfs.bookmark");
    private static NSUrl romfsUrl;   // the bookmarked file, accessed for the whole process (the boot reads it by path)
    private static string romfsPath;

    /// Where the boot reads the game's files: the bundle's (full export), a matching .romfs put in this app's own
    /// Documents (Files › <the game>), or the one shown once from iBuilder (a security-scoped bookmark, read in place).
    /// Null when a slim export has not been shown its files yet.
    public static string PlayerRomfs
    {
        get
        {
            if (!IsPlayer) return null;
            var inBundle = Path.Combine(BundleGame, "extract", "RomFS.img");
            if (PlayerRomfsName == null) return inBundle;
            if (romfsPath != null && File.Exists(romfsPath)) return romfsPath;
            try
            {
                foreach (var f in Directory.EnumerateFiles(GameAppMain.Documents, "*" + GameAppUi.RomfsExtension))
                    if (Mismatch(f) == null) return romfsPath = f;
            }
            catch (Exception) { }
            try
            {
                if (!File.Exists(RomfsBookmarkPath)) return null;
                var data = NSData.FromFile(RomfsBookmarkPath);
                var url = NSUrl.FromBookmarkData(data, NSUrlBookmarkResolutionOptions.WithoutUI, null, out bool stale, out NSError error);
                if (url == null) return null;
                url.StartAccessingSecurityScopedResource();
                if (!File.Exists(url.Path) || Mismatch(url.Path) != null) { url.StopAccessingSecurityScopedResource(); return null; }
                if (stale) SaveBookmark(url);
                romfsUrl = url;
                return romfsPath = url.Path;
            }
            catch (Exception) { return null; }
        }
    }

    private static string Mismatch(string path)
    {
        long size = new FileInfo(path).Length;
        return GameAppUi.RomfsMismatch(PlayerManifest, Path.GetFileName(path), size, () =>
        {
            using var r = File.OpenRead(path);
            return GameAppUi.RomfsCheck(r);
        });
    }

    private static void SaveBookmark(NSUrl url)
    {
        var data = url.CreateBookmarkData(NSUrlBookmarkCreationOptions.MinimalBookmark, null, null, out NSError error);
        if (data == null) throw new InvalidOperationException("无法保存游戏文件的位置：" + error?.LocalizedDescription);
        Directory.CreateDirectory(Path.GetDirectoryName(RomfsBookmarkPath));
        File.WriteAllBytes(RomfsBookmarkPath, data.ToArray());
    }

    /// The file the player picked (Files, opened in place): checked against the manifest, then remembered.
    /// Returns null when it is this game's files, else the sentence to show.
    public static string UsePickedRomfs(NSUrl url)
    {
        bool accessed = url.StartAccessingSecurityScopedResource();
        try
        {
            var why = Mismatch(url.Path);
            if (why != null) { if (accessed) url.StopAccessingSecurityScopedResource(); return why; }
            SaveBookmark(url);
            romfsUrl?.StopAccessingSecurityScopedResource();
            romfsUrl = url;            // stays accessed: the boot opens it by path
            romfsPath = url.Path;
            return null;
        }
        catch (Exception e)
        {
            if (accessed) url.StopAccessingSecurityScopedResource();
            return e.Message;
        }
    }

    // ---- the export ---------------------------------------------------------------------------------------------

    private sealed record Entry(string Name, string Source, byte[] Bytes, long Size);

    /// Everything the export needs, before anything is written: the entries and their total size. Throws with a
    /// sentence the player can act on when something is missing.
    private static List<Entry> Plan(string documents, string bundle, string dump, string titleId, bool slim, out string appName, out string romfsPath)
    {
        var name = GameAppUi.DisplayName(dump);
        appName = GameAppUi.ExportFileName(name);
        var app = "Payload/" + appName + ".app/";
        var entries = new List<Entry>();

        void AddTree(string dir, string prefix, Func<string, bool> skip = null)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file).Replace(Path.DirectorySeparatorChar, '/');
                if (skip != null && skip(rel)) continue;
                entries.Add(new Entry(prefix + rel, file, null, new FileInfo(file).Length));
            }
        }
        void AddFile(string path, string entryName) => entries.Add(new Entry(entryName, path, null, new FileInfo(path).Length));
        void AddBytes(string entryName, byte[] bytes) => entries.Add(new Entry(entryName, null, bytes, bytes.Length));

        string Need(string path, string what)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) throw new InvalidOperationException($"{what} 缺失（{path}）：请重新构建游戏，再导出");
            return path;
        }
        var framework = Need(Path.Combine(documents, "DmGameProbe.framework"), "已构建的框架");
        var flat = Need(Path.Combine(documents, "extract", "flat"), "重写后的模块");
        var romfs = Need(Path.Combine(documents, "extract", "RomFS.img"), "游戏文件（RomFS.img）");
        romfsPath = romfs;
        var pack = Need(Path.Combine(documents, "shaders", titleId), "游戏的着色器库");
        var profile = Need(Path.Combine(documents, "profile.tsv"), "游戏的配置");

        // The app itself, minus its signature (the installer writes a new one) and its Info.plist (replaced below).
        AddTree(bundle, app, rel => rel.StartsWith("_CodeSignature/") || rel == "Info.plist" || rel.StartsWith(Folder + "/"));
        AddTree(framework, app + "Frameworks/DmGameProbe.framework/", rel => rel.StartsWith("_CodeSignature/") || rel == "Info.plist");
        AddBytes(app + "Frameworks/DmGameProbe.framework/Info.plist",
                 FrameworkPlist(Path.Combine(framework, "Info.plist"), Path.Combine(bundle, "Info.plist"), titleId));
        AddTree(flat, app + Folder + "/extract/flat/");
        if (!slim) AddFile(romfs, app + Folder + "/extract/RomFS.img");   // slim: beside the .ipa (RomfsFileName)
        AddTree(pack, app + Folder + "/shaders/" + titleId + "/");
        AddFile(profile, app + Folder + "/documents/profile.tsv");
        var games = Path.Combine(documents, "games", titleId);
        GameWarmLogic.IngestUsedFile(documents, titleId);   // the shaders played so far travel in games/<id>/used_shaders.tsv, so the player warms them first
        if (Directory.Exists(games))
            AddTree(games, app + Folder + "/documents/games/" + titleId + "/",
                    rel => rel.StartsWith("rewrite-cache/") || rel.StartsWith("temp/") || rel == "env.txt.part");
        var derived = Path.Combine(documents, "profile-derived", titleId);
        if (Directory.Exists(derived)) AddTree(derived, app + Folder + "/documents/profile-derived/" + titleId + "/");

        var manifestText = GameAppUi.ExportManifestText(titleId, dump, name, GameAppBuild.Id, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        if (slim)
        {
            string check;
            using (var r = File.OpenRead(romfs)) check = GameAppUi.RomfsCheck(r);
            manifestText += GameAppUi.RomfsManifestLines(GameAppUi.RomfsFileName(name, titleId), new FileInfo(romfs).Length, check);
        }
        AddBytes(app + Folder + "/manifest.tsv", Encoding.UTF8.GetBytes(manifestText));

        // Its own identity: a bundle identifier of its own so it installs beside GameApp, the game's name and icon.
        var icon = Path.Combine(games, "icon.jpg");
        bool hasIcon = false;
        if (File.Exists(icon))
            foreach (var (size, suffix) in new[] { (120, "@2x"), (180, "@3x") })
            {
                var png = IconPng(icon, size);
                if (png == null) continue;
                AddBytes(app + "GameIcon60x60" + suffix + ".png", png);
                hasIcon = true;
            }
        AddBytes(app + "Info.plist", InfoPlist(Path.Combine(bundle, "Info.plist"), titleId, name, hasIcon));
        return entries;
    }

    /// The game's icon as a size x size PNG, drawn with CoreGraphics and ImageIO (safe off the main thread, where the
    /// export runs: a UIGraphicsImageRenderer there gave no image and the export had no icon).
    private static byte[] IconPng(string jpeg, int size)
    {
        try
        {
            using var source = CGImageSource.FromUrl(NSUrl.FromFilename(jpeg));
            using var image = source?.CreateImage(0, (CGImageOptions) null);
            if (image == null) return null;
            using var space = CGColorSpace.CreateDeviceRGB();
            using var context = new CGBitmapContext(null, size, size, 8, size * 4, space, CGImageAlphaInfo.NoneSkipLast);
            context.InterpolationQuality = CGInterpolationQuality.High;
            context.DrawImage(new CGRect(0, 0, size, size), image);
            using var scaled = context.ToImage();
            var data = new NSMutableData();
            using (var dest = CGImageDestination.Create(data, "public.png", 1))
            {
                if (dest == null) return null;
                dest.AddImage(scaled, (NSDictionary) null);
                if (!dest.Close()) return null;
            }
            return data.ToArray();
        }
        catch (Exception) { return null; }
    }

    /// The framework's Info.plist, complete: in Documents it only needed what zsign reads, inside an app bundle the
    /// installer also wants an identifier of its own, a version and the platform (the simulator's installd refused it
    /// without CFBundleIdentifier when the framework had none).
    private static byte[] FrameworkPlist(string original, string appPlist, string titleId)
    {
        var dict = (File.Exists(original) ? NSMutableDictionary.FromFile(original) : null) ?? new NSMutableDictionary();
        var app = NSDictionary.FromFile(appPlist);
        var baseId = (app?["CFBundleIdentifier"] as NSString)?.ToString() ?? "dev.nxpocket.gameapp";
        void Set(string key, NSObject value) { if (value != null) dict[key] = value; }
        Set("CFBundleExecutable", new NSString("DmGameProbe"));
        Set("CFBundleIdentifier", new NSString(GameAppUi.ExportBundleId(baseId, titleId) + ".DmGameProbe"));
        Set("CFBundleName", new NSString("DmGameProbe"));
        Set("CFBundlePackageType", new NSString("FMWK"));
        Set("CFBundleInfoDictionaryVersion", new NSString("6.0"));
        Set("CFBundleShortVersionString", new NSString("1.0"));
        Set("CFBundleVersion", new NSString("1"));
        Set("MinimumOSVersion", app?["MinimumOSVersion"]);
        Set("CFBundleSupportedPlatforms", app?["CFBundleSupportedPlatforms"]);
        Set("UIDeviceFamily", app?["UIDeviceFamily"]);
        var data = NSPropertyListSerialization.DataWithPropertyList(dict, NSPropertyListFormat.Xml, NSPropertyListWriteOptions.Immutable, out NSError error);
        if (data == null) throw new InvalidOperationException("无法写入框架的 Info.plist：" + error?.LocalizedDescription);
        return data.ToArray();
    }

    private static byte[] InfoPlist(string original, string titleId, string name, bool hasIcon)
    {
        var dict = NSMutableDictionary.FromFile(original) ?? new NSMutableDictionary();
        var baseId = (dict["CFBundleIdentifier"] as NSString)?.ToString() ?? "dev.nxpocket.gameapp";
        dict["CFBundleIdentifier"] = new NSString(GameAppUi.ExportBundleId(baseId, titleId));
        dict["CFBundleDisplayName"] = new NSString(name);
        dict["CFBundleName"] = new NSString(name.Length > 15 ? name.Substring(0, 15) : name);
        if (hasIcon)
        {
            var files = NSArray.FromStrings("GameIcon60x60");
            var primary = new NSMutableDictionary { ["CFBundleIconFiles"] = files };
            dict["CFBundleIcons"] = new NSMutableDictionary { ["CFBundlePrimaryIcon"] = primary };
            dict.Remove(new NSString("CFBundleIcons~ipad"));
        }
        var data = NSPropertyListSerialization.DataWithPropertyList(dict, NSPropertyListFormat.Xml, NSPropertyListWriteOptions.Immutable, out NSError error);
        if (data == null) throw new InvalidOperationException("无法写入导出的 Info.plist：" + error?.LocalizedDescription);
        return data.ToArray();
    }

    /// Writes Documents/Exports/<Name>.ipa (and, `slim`, the game files beside it: Exports/<Name>/<Name> [<id>].romfs).
    /// `log` gets a line per tenth of the bytes. Returns the .ipa's path.
    public static string Export(string documents, string bundle, string dump, string titleId, bool slim, Action<string> log)
    {
        var entries = Plan(documents, bundle, dump, titleId, slim, out var appName, out var romfsSource);
        long total = entries.Sum(e => e.Size);
        var outDir = Path.Combine(documents, "Exports");
        Directory.CreateDirectory(outDir);
        long free = new DriveInfo(documents).AvailableFreeSpace;
        long need = total + (256L << 20);
        if (free > 0 && free < need)
            throw new InvalidOperationException($"空间不足：.ipa 需要约 {GameAppUi.SizeText(need)}，可用 {GameAppUi.SizeText(free)}");
        log($"export: {entries.Count} files, {GameAppUi.SizeText(total)} -> Exports/{appName}.ipa");

        var path = Path.Combine(outDir, appName + ".ipa");
        var part = path + ".part";
        long done = 0; int tenth = 0;
        try
        {
            using (var stream = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var buffer = new byte[4 << 20];
                foreach (var e in entries)
                {
                    // Large binaries are stored as they are: compressing 2 GB of game files on a phone takes minutes
                    // and gains little; the app's own files are compressed quickly.
                    var level = e.Size > (16L << 20) ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                    var entry = zip.CreateEntry(e.Name, level);
                    entry.ExternalAttributes = UnixAttributes(e.Source);
                    using (var output = entry.Open())
                    {
                        if (e.Bytes != null) output.Write(e.Bytes, 0, e.Bytes.Length);
                        else
                        {
                            using var input = new FileStream(e.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                            int n;
                            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                output.Write(buffer, 0, n);
                                done += n;
                                int t = total > 0 ? (int) (done * 10 / total) : 10;
                                if (t > tenth) { tenth = t; log($"export: {t * 10} % ({GameAppUi.SizeText(done)})"); }
                            }
                            continue;
                        }
                    }
                    done += e.Size;
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(part, path);
            if (slim) PlaceRomfs(romfsSource, Path.Combine(outDir, GameAppUi.RomfsRelativePath(GameAppUi.DisplayName(dump), titleId)), log);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
        log($"export: done, {GameAppUi.SizeText(new FileInfo(path).Length)} at Exports/{appName}.ipa");
        return path;
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int clonefile(string src, string dst, uint flags);

    /// The game files beside a slim .ipa: a clone of extract/RomFS.img (APFS: the same blocks, no space taken, and a
    /// later build replacing RomFS.img leaves the clone whole); a copy where a clone is refused.
    private static void PlaceRomfs(string source, string dest, Action<string> log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        var part = dest + ".part";
        try { File.Delete(part); } catch { }
        var started = DateTime.Now;
        bool cloned = clonefile(source, part, 0) == 0;
        if (!cloned) File.Copy(source, part, true);
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(part, dest);
        log($"export: the game files beside it, Exports/{Path.GetFileName(Path.GetDirectoryName(dest))}/{Path.GetFileName(dest)} ({GameAppUi.SizeText(new FileInfo(dest).Length)}, "
            + (cloned ? "克隆：不占额外空间" : $"copied in {(DateTime.Now - started).TotalSeconds:0}s") + ")");
    }

    /// The Unix mode an installer needs to see in the .ipa: executables keep their x bit (a main binary without it
    /// does not launch). Generated entries are plain files.
    private static int UnixAttributes(string source)
    {
        int mode = 0x1A4;   // 0644
        try
        {
            if (source != null && !OperatingSystem.IsWindows())
                mode = (int) File.GetUnixFileMode(source) & 0xFFF;
        }
        catch (Exception) { }
        return (0x8000 | mode) << 16;   // S_IFREG | permissions, in the high half
    }
}
