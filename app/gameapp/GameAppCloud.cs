// gameapp/GameAppCloud.cs: games and exported apps kept in the cloud, shown in the library before they are downloaded.
//
// A source is the NAS (SFTP, gameapp/GameAppNas.cs, the folder chosen in its settings) or a folder picked in the Files
// app: iCloud Drive, Google Drive, OneDrive, Dropbox... any app that shows its files there (its File Provider). The app
// lists each source's folder (one level), keeps the list (Library/Application Support/gameapp.cloud.tsv, so the library
// shows it at once at the next launch), and shows every game or .ipa it holds that is not in Documents greyed, with a
// cloud mark. A tap downloads it (with the game's update and DLC from the same source), and a game downloaded from a
// source can drop its download again ("Remove download"): it stays in the cloud.
//
// No UIKit here: gameapp/tests/GameAppUi.Tests runs it on the Mac.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

public enum CloudKind { Nas, Folder }

/// One source: Id is stable (it names the source in the cached list); Location is the NAS folder or the folder's path as
/// the Files app gave it (shown, and used to name the provider); the folder itself is reached through its bookmark.
public sealed record CloudSource(string Id, CloudKind Kind, string Name, string Location);

/// A file a source holds. Name is its real name (an iCloud placeholder ".X.nsp.icloud" is "X.nsp"); Size is 0 when the
/// provider does not say; Remote is how the source reaches it (the NAS path, or the name inside the folder).
public sealed record CloudFile(string SourceId, string Name, long Size, string Remote);

public static class Cloud
{
    public static readonly string[] GameExtensions = { ".nsp", ".xci", ".nsz", ".nup" };

    public static bool IsGameFile(string name) => GameExtensions.Contains(Path.GetExtension(name ?? "").ToLowerInvariant());

    public static bool IsIpa(string name) => string.Equals(Path.GetExtension(name ?? ""), ".ipa", StringComparison.OrdinalIgnoreCase);

    /// What the library cares about in a source: games (with their updates and DLC) and exported apps.
    public static bool Wanted(string name) => IsGameFile(name) || IsIpa(name);

    /// An iCloud Drive file not downloaded on this phone is listed as ".<name>.icloud": its real name.
    public static string RealName(string listed)
    {
        if (string.IsNullOrEmpty(listed)) return listed;
        if (listed.StartsWith(".", StringComparison.Ordinal) && listed.EndsWith(".icloud", StringComparison.OrdinalIgnoreCase) && listed.Length > 8)
            return listed.Substring(1, listed.Length - 8);
        return listed;
    }

    /// The provider a folder belongs to, from the path the Files app gave (the provider's own container names it).
    public static string ProviderName(string path)
    {
        var p = (path ?? "").Replace('\\', '/');
        if (p.Contains("com~apple~CloudDocs") || p.Contains("/Mobile Documents/")) return "iCloud Drive";
        if (p.IndexOf("com.google.Drive", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("Google Drive", StringComparison.OrdinalIgnoreCase) >= 0) return "Google Drive";
        if (p.IndexOf("OneDrive", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("com.microsoft.skydrive", StringComparison.OrdinalIgnoreCase) >= 0) return "OneDrive";
        if (p.IndexOf("Dropbox", StringComparison.OrdinalIgnoreCase) >= 0) return "Dropbox";
        if (p.IndexOf("FileProvider", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("File Provider Storage", StringComparison.OrdinalIgnoreCase) >= 0) return "Files";
        return "在这台 " + GameAppUi.DeviceName;
    }

    /// The source's name in the library: "<provider> · <folder>".
    public static string SourceName(string path)
    {
        var folder = Path.GetFileName((path ?? "").TrimEnd('/', '\\'));
        var provider = ProviderName(path);
        return string.IsNullOrEmpty(folder) ? provider : provider + " · " + folder;
    }

    public static string NewId(CloudKind kind, IEnumerable<CloudSource> existing)
    {
        var used = new HashSet<string>((existing ?? Enumerable.Empty<CloudSource>()).Select(s => s.Id));
        var prefix = kind == CloudKind.Nas ? "nas" : "folder";
        for (int i = 1; ; i++)
            if (!used.Contains(prefix + i.ToString(CultureInfo.InvariantCulture))) return prefix + i.ToString(CultureInfo.InvariantCulture);
    }

    // ---- the saved state: sources, then the files each one held when it was last listed ---------------------------

    private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    public static string Serialize(IEnumerable<CloudSource> sources, IEnumerable<CloudFile> files)
    {
        var sb = new StringBuilder("# GameApp cloud sources and what they held (gameapp/GameAppCloud.cs)\n");
        foreach (var s in sources ?? Enumerable.Empty<CloudSource>())
            sb.Append("source\t").Append(Clean(s.Id)).Append('\t').Append(s.Kind == CloudKind.Nas ? "nas" : "folder").Append('\t')
              .Append(Clean(s.Name)).Append('\t').Append(Clean(s.Location)).Append('\n');
        foreach (var f in files ?? Enumerable.Empty<CloudFile>())
            sb.Append("file\t").Append(Clean(f.SourceId)).Append('\t').Append(Clean(f.Name)).Append('\t')
              .Append(f.Size.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Clean(f.Remote)).Append('\n');
        return sb.ToString();
    }

    public static (List<CloudSource> Sources, List<CloudFile> Files) Parse(string text)
    {
        var sources = new List<CloudSource>();
        var files = new List<CloudFile>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var c = raw.TrimEnd('\r').Split('\t');
            if (c.Length == 5 && c[0] == "source" && c[1].Length > 0)
                sources.Add(new CloudSource(c[1], c[2] == "nas" ? CloudKind.Nas : CloudKind.Folder, c[3], c[4]));
            else if (c.Length == 5 && c[0] == "file" && c[2].Length > 0 && long.TryParse(c[3], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                files.Add(new CloudFile(c[1], c[2], size, c[4]));
        }
        var ids = new HashSet<string>(sources.Select(s => s.Id));
        return (sources, files.Where(f => ids.Contains(f.SourceId)).ToList());
    }

    /// A source listed again: its old files go, the new ones come; the other sources' stay.
    public static List<CloudFile> Replace(IEnumerable<CloudFile> files, string sourceId, IEnumerable<CloudFile> listed) =>
        (files ?? Enumerable.Empty<CloudFile>()).Where(f => f.SourceId != sourceId)
            .Concat((listed ?? Enumerable.Empty<CloudFile>()).Select(f => f with { SourceId = sourceId, Name = RealName(f.Name) }).Where(f => Wanted(f.Name)))
            .ToList();

    // ---- the library: local games, then the ones only in the cloud ---------------------------------------------

    /// The base games that are only in the cloud (not in Documents), each once: the first source that holds it wins.
    public static List<CloudFile> CloudOnlyGames(IEnumerable<string> localNames, IEnumerable<CloudFile> files)
    {
        var local = new HashSet<string>(localNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var games = (files ?? Enumerable.Empty<CloudFile>()).Where(f => IsGameFile(f.Name)).ToList();
        var playable = new HashSet<string>(GameAppUi.PlayableGames(games.Select(f => f.Name).Concat(local)), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return games.Where(f => playable.Contains(f.Name) && !local.Contains(f.Name) && seen.Add(f.Name)).ToList();
    }

    /// What a tap on a cloud game downloads: the game, and its newest update and DLC from the same source, minus what
    /// Documents already has.
    public static List<CloudFile> DownloadSet(string game, string sourceId, IEnumerable<CloudFile> files, IEnumerable<string> localNames)
    {
        var local = new HashSet<string>(localNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var here = (files ?? Enumerable.Empty<CloudFile>()).Where(f => f.SourceId == sourceId && IsGameFile(f.Name)).ToList();
        var names = here.Select(f => f.Name).Concat(local).ToList();
        var want = new List<string> { game };
        var update = GameAppUi.UpdateFor(game, names);
        if (update != null) want.Add(update);
        want.AddRange(GameAppUi.DlcFor(game, names));
        return want.Where(n => !local.Contains(n)).Select(n => here.FirstOrDefault(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)))
                   .Where(f => f != null).ToList();
    }

    /// The source a local game (or .ipa) also lives in, so its download can be removed without losing it; null if none.
    public static CloudFile CloudCopyOf(string name, IEnumerable<CloudFile> files) =>
        (files ?? Enumerable.Empty<CloudFile>()).FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// Exported apps: the local ones (Documents/Exports) first, then the ones only in the cloud.
    public static List<(string Name, bool Local, CloudFile Cloud)> Apps(IEnumerable<string> localIpas, IEnumerable<CloudFile> files)
    {
        var local = (localIpas ?? Enumerable.Empty<string>()).ToList();
        var have = new HashSet<string>(local, StringComparer.OrdinalIgnoreCase);
        var list = local.Select(n => (n, true, CloudCopyOf(n, files))).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in (files ?? Enumerable.Empty<CloudFile>()).Where(f => IsIpa(f.Name)))
            if (!have.Contains(f.Name) && seen.Add(f.Name)) list.Add((f.Name, false, f));
        return list;
    }

    /// "On NAS · 4.2 GB" -- the line under a cloud item.
    public static string Where(CloudFile f, IEnumerable<CloudSource> sources)
    {
        var s = (sources ?? Enumerable.Empty<CloudSource>()).FirstOrDefault(x => x.Id == f.SourceId);
        var name = s == null ? "云端" : s.Kind == CloudKind.Nas ? "NAS" : ProviderName(s.Location);
        return "在 " + name + (f.Size > 0 ? " · " + GameAppUi.SizeText(f.Size) : "");
    }
}
