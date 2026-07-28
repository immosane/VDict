namespace VDict.Presentation.Settings;

/// <summary>
/// Owns lazily-created setting sections for one settings-window lifetime.
/// </summary>
public sealed class SettingsSectionCache<TSection> : IDisposable
    where TSection : class
{
    private readonly Func<string, TSection> _factory;
    private readonly Action<TSection>? _release;
    private readonly Dictionary<string, TSection> _sections =
        new(StringComparer.Ordinal);
    private bool _disposed;

    public SettingsSectionCache(
        Func<string, TSection> factory,
        Action<TSection>? release = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _release = release;
    }

    public int Count => _sections.Count;

    public TSection GetOrCreate(string key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_sections.TryGetValue(key, out TSection? cached))
        {
            return cached;
        }

        TSection section = _factory(key) ??
            throw new InvalidOperationException($"设置 Section 工厂未能创建“{key}”。");
        _sections.Add(key, section);
        return section;
    }

    public bool IsCreated(string key) => _sections.ContainsKey(key);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (TSection section in _sections.Values)
        {
            _release?.Invoke(section);
        }

        _sections.Clear();
    }
}
