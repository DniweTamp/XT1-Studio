using Xt1.Core.Containers;
using Xt1.Core.Imaging;

namespace Xt1.App.Services;

/// <summary>Small LRU cache of decoded subresources, keyed by texture identity and subresource.</summary>
public sealed class DecodeCache
{
    private sealed record Entry(RgbaImage Image);

    private readonly Dictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly object _gate = new();

    public int Capacity { get; set; } = 64;

    /// <summary>
    /// Identity of one decodable subresource. Format, dimensions and mip count are included because a
    /// header edit changes the decode; payload *content* of the same length is not visible here, which
    /// is why <c>RefreshAll</c> clears the cache after any edit.
    /// </summary>
    public static string Key(EditableTexture texture, int mip, int face, int slice) =>
        $"{texture.Index}|{mip}|{face}|{slice}|{texture.Header.FormatValue:X}|" +
        $"{texture.Header.Width}x{texture.Header.Height}x{texture.Header.Depth}|{texture.Header.MipCount}|" +
        $"{texture.Payload.Length}";

    public bool TryGet(string key, out RgbaImage? image)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var entry))
            {
                _order.Remove(key);
                _order.AddFirst(key);
                image = entry.Image;
                return true;
            }
            image = null;
            return false;
        }
    }

    public void Put(string key, RgbaImage image)
    {
        lock (_gate)
        {
            if (_map.ContainsKey(key)) _order.Remove(key);
            _map[key] = new Entry(image);
            _order.AddFirst(key);
            while (_map.Count > Capacity && _order.Last is not null)
            {
                var evict = _order.Last.Value;
                _order.RemoveLast();
                _map.Remove(evict);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
