using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace VLCVideoWall;

/// <summary>
/// Значки звука поверх всех плиток стены. Каждый значок — всплывающее окно с владельцем
/// «окно стены» и экранными координатами; при свёрнутой или невидимой стене значки
/// скрываются, чтобы не оставаться кликабельными поверх чужих программ.
/// </summary>
public sealed class WallAudioBadges : IDisposable
{
    private const int Size = 38;
    private const int Margin = 8;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private readonly Dictionary<int, AudioBadgeWindow> _badges = new Dictionary<int, AudioBadgeWindow>();
    private IntPtr _parent = IntPtr.Zero;

    /// <summary>Клик по значку: аргумент — номер плитки.</summary>
    public event Action<int> Toggled;

    /// <summary>Сколько значков сейчас показано.</summary>
    public int Count { get { return _badges.Count; } }

    /// <summary>
    /// Привести значки в соответствие с плитками.
    /// <paramref name="tiles"/> — прямоугольники плиток в экранных координатах.
    /// </summary>
    /// <summary>Угол значка: "top-left", "top-right", "bottom-left", "bottom-right".</summary>
    public string Corner { get; set; } = "bottom-right";

    public void Sync(IntPtr parent, IDictionary<int, Rectangle> tiles,
                     Func<int, AudioBadgeState> stateFor, Func<int, string> nameFor)
    {
        if (parent == IntPtr.Zero || tiles == null || tiles.Count == 0 || !IsWindow(parent))
        {
            HideAll();
            return;
        }

        // стена свёрнута или скрыта — значки не показываем
        bool ownerVisible = IsWindowVisible(parent) && !IsIconic(parent);

        if (parent != _parent) { HideAll(); _parent = parent; }

        foreach (var pos in _badges.Keys.Where(p => !tiles.ContainsKey(p)).ToList())
        {
            var gone = _badges[pos];
            _badges.Remove(pos);
            gone.Dispose();
        }

        foreach (var pair in tiles)
        {
            AudioBadgeWindow badge;
            if (!_badges.TryGetValue(pair.Key, out badge))
            {
                int pos = pair.Key;
                badge = new AudioBadgeWindow(parent, 0, 0, Size);
                if (badge.Handle == IntPtr.Zero) continue;
                badge.Clicked += () =>
                {
                    var handler = Toggled;
                    if (handler != null) handler(pos);
                };
                _badges[pos] = badge;
            }

            badge.CameraName = nameFor(pair.Key);
            badge.SetState(stateFor(pair.Key));

            var tile = pair.Value;
            bool right = Corner == null || !Corner.EndsWith("-left", StringComparison.OrdinalIgnoreCase);
            bool bottom = Corner != null && Corner.StartsWith("bottom", StringComparison.OrdinalIgnoreCase);
            int x = right ? tile.Right - Size - Margin : tile.Left + Margin;
            int y = bottom ? tile.Bottom - Size - Margin : tile.Top + Margin;
            if (x < tile.Left) x = tile.Left;
            if (y < tile.Top) y = tile.Top;
            if (x + Size > tile.Right) x = Math.Max(tile.Left, tile.Right - Size);
            if (y + Size > tile.Bottom) y = Math.Max(tile.Top, tile.Bottom - Size);
            badge.Move(x, y);
            badge.SetVisible(ownerVisible);
        }
    }

    private void HideAll()
    {
        foreach (var b in _badges.Values.ToList()) b.Dispose();
        _badges.Clear();
        _parent = IntPtr.Zero;
    }

    public void Dispose()
    {
        HideAll();
    }
}
