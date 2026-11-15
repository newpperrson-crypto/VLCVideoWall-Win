namespace VLCVideoWall;

/// <summary>
/// Таблица камер. Отличие от обычного DataGridView одно: Ctrl+C кладёт в буфер только
/// адрес текущей камеры.
///
/// Штатный DataGridView обрабатывает Ctrl+C сам — в ProcessCmdKey, до события KeyDown,
/// и копирует выделение (вся строка: «Вкл», канал, адрес, проверка через табуляцию).
/// Поэтому перехватывать нужно именно ProcessCmdKey, иначе перехват «не срабатывает»:
/// обработчик KeyDown просто не вызывается.
/// </summary>
public sealed class CameraGrid : DataGridView
{
    /// <summary>Адрес камеры в текущей строке; null/пусто — копировать нечего.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string> UrlForCurrentRow { get; set; }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.C) && UrlForCurrentRow != null)
        {
            var url = UrlForCurrentRow();
            if (!string.IsNullOrWhiteSpace(url))
            {
                try { Clipboard.SetText(url.Trim()); } catch { }
                return true;   // штатное «копировать строку» не выполняем
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
