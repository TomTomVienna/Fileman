using Fileman.Core.Scanning;

namespace Fileman.Core.Automation;

/// <summary>
/// Überwacht die Quellordner mit FileSystemWatcher und sammelt die betroffenen Pfade.
/// Die eigentliche Arbeit (sichern) passiert erst nach einer Ruhezeit im <see cref="BackupService"/>.
/// Der Wächter kann Ereignisse verlieren (Pufferüberlauf); das wird gemeldet und durch einen vollständigen Abgleich aufgefangen.
/// </summary>
public sealed class ChangeWatcher : IDisposable
{
    private readonly object _sync = new();
    private readonly ExclusionFilter _filter;
    private readonly string? _archiveDirectory;
    private readonly List<(string Root, FileSystemWatcher Watcher)> _watchers = [];
    private HashSet<string> _paths = new(PathUtil.Comparer);
    private bool _overflow;

    public ChangeWatcher(ExclusionFilter filter, string? archiveDirectory = null)
    {
        _filter = filter;
        _archiveDirectory = archiveDirectory;
    }

    /// <summary>Wird bei jedem relevanten Ereignis ausgelöst (auf einem Hintergrund-Thread).</summary>
    public event Action? Changed;

    /// <summary>Die aktuell tatsächlich überwachten Quellordner.</summary>
    public IReadOnlyList<string> WatchedRoots
    {
        get
        {
            lock (_sync)
                return _watchers.Select(w => w.Root).ToList();
        }
    }

    /// <summary>
    /// Überwacht genau die angegebenen, vorhandenen Ordner. Kann wiederholt aufgerufen werden, etwa wenn ein
    /// Netzlaufwerk wieder erreichbar ist.
    /// </summary>
    /// <returns>True, wenn sich die Menge der überwachten Ordner geändert hat.</returns>
    public bool Watch(IEnumerable<string> roots)
    {
        var wanted = roots.Select(PathUtil.Normalize).Where(Directory.Exists).ToHashSet(PathUtil.Comparer);
        lock (_sync)
        {
            var changed = false;
            foreach (var (root, watcher) in _watchers.Where(w => !wanted.Contains(w.Root)).ToList())
            {
                watcher.Dispose();
                _watchers.RemoveAll(w => w.Watcher == watcher);
                changed = true;
            }
            foreach (var root in wanted.Where(r => _watchers.All(w => !PathUtil.Comparer.Equals(w.Root, r))))
            {
                _watchers.Add((root, CreateWatcher(root)));
                changed = true;
            }
            return changed;
        }
    }

    private FileSystemWatcher CreateWatcher(string root)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Created += (_, e) => Add(e.FullPath, root);
        watcher.Deleted += (_, e) => Add(e.FullPath, root);
        watcher.Changed += (_, e) =>
        {
            // Bei Ordnern meldet Windows "Changed", sobald sich darin etwas ändert – die Datei selbst kommt ohnehin.
            if (!Directory.Exists(e.FullPath))
                Add(e.FullPath, root);
        };
        watcher.Renamed += (_, e) =>
        {
            Add(e.OldFullPath, root);
            Add(e.FullPath, root);
        };
        watcher.Error += (_, _) =>
        {
            lock (_sync)
                _overflow = true;
            Changed?.Invoke();
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>Nimmt einen geänderten Pfad auf (auch für Tests nutzbar).</summary>
    public void Add(string path, string root)
    {
        if (_filter.IsExcludedPath(path, root) || (_archiveDirectory is not null && PathUtil.IsUnder(path, _archiveDirectory)))
            return;
        lock (_sync)
            _paths.Add(path);
        Changed?.Invoke();
    }

    /// <summary>Liefert alle seit dem letzten Aufruf gesammelten Pfade und ob Ereignisse verloren gingen.</summary>
    public (IReadOnlyCollection<string> Paths, bool Overflow) Drain()
    {
        lock (_sync)
        {
            var paths = _paths;
            var overflow = _overflow;
            _paths = new HashSet<string>(PathUtil.Comparer);
            _overflow = false;
            return (paths, overflow);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var (_, watcher) in _watchers)
                watcher.Dispose();
            _watchers.Clear();
        }
    }
}
