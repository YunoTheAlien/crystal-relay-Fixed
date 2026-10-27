using System.IO;

namespace VrcTwitchOscBridge.Services;

/// <summary>
/// Whitelist of Twitch chat accounts whose messages are read for tip amounts.
///
/// Backed by a plain text file, one Twitch login per line, so it needs no UI and is easy
/// to hand edit. Blank lines and lines starting with '#' are ignored. Matching is on the
/// whole login, case-insensitively: there is no wildcard or partial matching, so a
/// whitelisted "streamelements" never matches "streamelements_alt".
///
/// The file is re-read when its timestamp changes, so edits apply without a restart.
/// </summary>
public sealed class ChatTipBotWhitelist
{
    private readonly string filePath;
    private readonly object gate = new();

    private HashSet<string> logins = new(StringComparer.OrdinalIgnoreCase);
    private DateTime lastWriteUtc = DateTime.MinValue;
    private long lastLength = -1;
    private bool loadedOnce;

    public ChatTipBotWhitelist()
        : this(AppDataPaths.ChatTipBotsPath)
    {
    }

    internal ChatTipBotWhitelist(string filePath)
    {
        this.filePath = filePath;
    }

    public string FilePath => filePath;

    /// <summary>
    /// True when the login is whitelisted. Exact whole-name match, case-insensitive.
    /// </summary>
    public bool IsWhitelisted(string? userLogin)
    {
        var login = userLogin?.Trim();
        if (string.IsNullOrWhiteSpace(login))
        {
            return false;
        }

        RefreshIfChanged();

        lock (gate)
        {
            return logins.Contains(login);
        }
    }

    public int Count
    {
        get
        {
            RefreshIfChanged();
            lock (gate)
            {
                return logins.Count;
            }
        }
    }

    private void RefreshIfChanged()
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                lock (gate)
                {
                    if (logins.Count > 0 || !loadedOnce)
                    {
                        logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        lastWriteUtc = DateTime.MinValue;
                        lastLength = -1;
                        loadedOnce = true;
                    }
                }

                return;
            }

            lock (gate)
            {
                if (loadedOnce && info.LastWriteTimeUtc == lastWriteUtc && info.Length == lastLength)
                {
                    return;
                }
            }

            var parsed = ParseLines(File.ReadAllLines(filePath));

            lock (gate)
            {
                logins = parsed;
                lastWriteUtc = info.LastWriteTimeUtc;
                lastLength = info.Length;
                loadedOnce = true;
            }
        }
        catch (IOException)
        {
            // A locked or mid-write file must not break chat handling; keep the last good list.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static HashSet<string> ParseLines(IEnumerable<string> lines)
    {
        var parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines)
        {
            var line = rawLine?.Trim() ?? string.Empty;
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // Tolerate a pasted "@name" or a trailing comment.
            var commentIndex = line.IndexOf('#');
            if (commentIndex >= 0)
            {
                line = line[..commentIndex].Trim();
            }

            line = line.TrimStart('@').Trim();
            if (line.Length == 0 || line.Any(char.IsWhiteSpace))
            {
                continue;
            }

            parsed.Add(line);
        }

        return parsed;
    }
}
