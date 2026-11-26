using Hopkey.Core.Config;
using Hopkey.Core.Util;

namespace Hopkey.Core.History;

/// <summary>Favorites are stored in settings.json; this service wraps mutations.</summary>
public sealed class FavoritesService
{
    private readonly SettingsStore _settings;

    public FavoritesService(SettingsStore settings) => _settings = settings;

    public IReadOnlyList<FavoriteItem> Items => _settings.Current.Favorites;

    public bool Contains(string path) => _settings.Current.Favorites.Exists(f => PathUtil.Equal(f.Path, path));

    public void Add(string path, string? alias = null)
    {
        if (string.IsNullOrWhiteSpace(path) || Contains(path)) return;
        _settings.Update(s => s.Favorites.Add(new FavoriteItem(PathUtil.TrimEndSeparator(path), alias)));
    }

    public void Remove(string path) =>
        _settings.Update(s => s.Favorites.RemoveAll(f => PathUtil.Equal(f.Path, path)));

    public void Toggle(string path)
    {
        if (Contains(path)) Remove(path); else Add(path);
    }

    public void Move(int from, int to) =>
        _settings.Update(s =>
        {
            if (from < 0 || from >= s.Favorites.Count || to < 0 || to >= s.Favorites.Count) return;
            var item = s.Favorites[from];
            s.Favorites.RemoveAt(from);
            s.Favorites.Insert(to, item);
        });

    public void Replace(IEnumerable<FavoriteItem> items) =>
        _settings.Update(s => s.Favorites = items.Select(i => new FavoriteItem(i.Path, i.Alias)).ToList());
}
