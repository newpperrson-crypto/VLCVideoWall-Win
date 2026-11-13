using System;
using System.Runtime.InteropServices;

namespace VLCVideoWall;

/// <summary>
/// Громкость отдельного процесса Windows (Core Audio, сеанс WASAPI этого процесса).
///
/// Зачем: в режиме «звук из потока стены» отдельного аудиопроцесса VLC нет — звук
/// выводит сам VLC стены. Его HTTP-интерфейс управляет только своим плейлистом,
/// поэтому громкость и выключение звука делаются через сеанс процесса в Windows:
/// это не трогает другие программы и не требует перезапуска стены.
/// </summary>
internal static class ProcessVolume
{
    // ---------------------------------------------------------------- COM

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }

    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        int GetDevice(string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int access, out IntPtr properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        int GetAudioSessionControl(ref Guid sessionGuid, int streamFlags, out IntPtr sessionControl);
        int GetSimpleAudioVolume(ref Guid sessionGuid, int streamFlags, out IntPtr audioVolume);
        // IAudioSessionManager2
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
        int RegisterSessionNotification(IntPtr sessionNotification);
        int UnregisterSessionNotification(IntPtr sessionNotification);
        int RegisterDuckNotification(string sessionId, IntPtr duckNotification);
        int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int sessionCount);
        int GetSession(int sessionIndex, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetProcessId(out uint processId);
        int IsSystemSoundsSession();
        int SetDurable(bool durable);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, IntPtr eventContext);
        int GetMasterVolume(out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // ---------------------------------------------------------------- работа

    /// <summary>Найти сеанс процесса и вернуть ISimpleAudioVolume (или null).</summary>
    private static ISimpleAudioVolume FindSession(uint processId)
    {
        IMMDeviceEnumerator enumerator = null;
        IMMDevice device = null;
        object managerObject = null;
        IAudioSessionEnumerator sessions = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device) != 0) return null;

            var iid = typeof(IAudioSessionManager2).GUID;
            object obj;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out obj) != 0 || obj == null) return null;
            managerObject = obj;
            var manager = (IAudioSessionManager2)obj;

            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null) return null;
            int count;
            if (sessions.GetCount(out count) != 0) return null;

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl control;
                if (sessions.GetSession(i, out control) != 0 || control == null) continue;

                // ВАЖНО: control, control2 и volume — один и тот же COM-объект (RCW).
                // ReleaseComObject на промежуточном приведении отвязывает его целиком,
                // поэтому освобождаем ровно один раз и не трогаем совпавший сеанс.
                var control2 = control as IAudioSessionControl2;
                var volume = control as ISimpleAudioVolume;
                uint pid = 0;
                bool match = false;
                if (control2 != null && volume != null &&
                    control2.GetProcessId(out pid) == 0 && pid == processId)
                {
                    match = true;
                }

                if (match) return volume;      // освободит вызывающий код
                Marshal.ReleaseComObject(control);
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (sessions != null) Marshal.ReleaseComObject(sessions);
            if (managerObject != null) Marshal.ReleaseComObject(managerObject);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Перечислить звуковые сеансы: PID → (выключен, громкость). Нужно для диагностики
    /// и чтобы отличить «сеанса нет» от «неверно сопоставили PID».
    /// </summary>
    public static System.Collections.Generic.Dictionary<uint, Tuple<bool, float>> ListSessions()
    {
        var result = new System.Collections.Generic.Dictionary<uint, Tuple<bool, float>>();
        IMMDeviceEnumerator enumerator = null;
        IMMDevice device = null;
        object managerObject = null;
        IAudioSessionEnumerator sessions = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device) != 0) return result;

            var iid = typeof(IAudioSessionManager2).GUID;
            object obj;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out obj) != 0 || obj == null) return result;
            managerObject = obj;
            var manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null) return result;

            int count;
            if (sessions.GetCount(out count) != 0) return result;

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl control;
                if (sessions.GetSession(i, out control) != 0 || control == null) continue;
                try
                {
                    var control2 = control as IAudioSessionControl2;
                    var volume = control as ISimpleAudioVolume;
                    if (control2 == null || volume == null) continue;

                    uint pid;
                    if (control2.GetProcessId(out pid) != 0) continue;
                    float level;
                    bool mute;
                    if (volume.GetMasterVolume(out level) != 0) continue;
                    if (volume.GetMute(out mute) != 0) continue;
                    result[pid] = Tuple.Create(mute, level);
                }
                finally
                {
                    Marshal.ReleaseComObject(control);
                }
            }
        }
        catch { }
        finally
        {
            if (sessions != null) Marshal.ReleaseComObject(sessions);
            if (managerObject != null) Marshal.ReleaseComObject(managerObject);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }

    /// <summary>Поставить громкость процесса (0..1). false — сеанса нет.</summary>
    public static bool SetVolume(uint processId, float level)
    {
        var volume = FindSession(processId);
        if (volume == null) return false;
        try
        {
            float clamped = Math.Max(0f, Math.Min(1f, level));
            if (volume.SetMasterVolume(clamped, IntPtr.Zero) != 0) return false;
            // нулевая громкость = «выключено» и по флагу mute, иначе Windows при
            // следующем изменении общей громкости вернёт звук сам
            volume.SetMute(clamped <= 0.0001f, IntPtr.Zero);
            return true;
        }
        catch { return false; }
        finally { Marshal.ReleaseComObject(volume); }
    }

    /// <summary>Текущая громкость процесса (0..1) или -1, если сеанса нет.</summary>
    public static float GetVolume(uint processId)
    {
        var volume = FindSession(processId);
        if (volume == null) return -1f;
        try
        {
            float level;
            bool mute;
            if (volume.GetMasterVolume(out level) != 0) return -1f;
            if (volume.GetMute(out mute) != 0) return -1f;
            return mute ? 0f : level;
        }
        catch { return -1f; }
        finally { Marshal.ReleaseComObject(volume); }
    }
}
