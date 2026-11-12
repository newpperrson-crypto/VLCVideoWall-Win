using System.Windows.Forms;

namespace VLCVideoWall;

internal static class Program
{
    private static Mutex _mutex;

    /// <summary>Именованное событие: второй экземпляр просит первый показать окно.</summary>
    private const string ShowEventName = @"Local\VLCVideoWall_ShowWindow";

    [STAThread]
    private static void Main()
    {
        _mutex = new Mutex(true, @"Local\VLCVideoWall_SingleInstance", out bool isNew);
        if (!isNew)
        {
            // Приложение уже работает (возможно, свёрнуто в трей) — разворачиваем его окно
            // и выходим молча, без предупреждений.
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(ShowEventName))
                    ev.Set();
            }
            catch { }
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }

        try
        {
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
            {
                var form = new MainForm();

                var listener = new Thread(() =>
                {
                    while (true)
                    {
                        try
                        {
                            if (!showEvent.WaitOne()) continue;
                            form.BeginInvoke(new Action(form.ShowFromTray));
                        }
                        catch (ObjectDisposedException) { return; }
                        catch { }
                    }
                });
                listener.IsBackground = true;
                listener.Start();

                Application.Run(form);
            }
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }
}
