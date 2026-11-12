using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace VLCVideoWall;

/// <summary>
/// Значки звука поверх всех плиток стены. Каждый значок — дочернее окно окна VLC:
/// он движется и обрезается вместе со стеной и не перекрывает чужие окна.
/// </summary>
public sealed class WallAudioBadges : IDisposable
{
    private const int Size = 38;
    private const int Margin = 8;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

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
    public void Sync(IntPtr parent, IDictionary<int, Rectangle> tiles,
                     Func<int, AudioBadgeState> stateFor, Func<int, string> nameFor, bool visible = true)
    {
        if (parent == IntPtr.Zero || tiles == null || tiles.Count == 0 || !IsWindow(parent))
        {
            HideAll();
            return;
        }

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

            int x = pair.Value.Right - Size - Margin;
            int y = pair.Value.Top + Margin;
            if (x < pair.Value.Left) x = pair.Value.Left;
            if (y < pair.Value.Top) y = pair.Value.Top;
            badge.Move(x, y);
            badge.SetVisible(visible);
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
