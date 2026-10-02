using Microsoft.Win32;
using SharpDX.DirectInput;

namespace openXRTK_Graph.Input;

/// <summary>Recorder actions that can be triggered directly by a controller button (no SimHub required).</summary>
public enum ButtonAction
{
    Start,
    Stop,
    Toggle,
    Marker,
}

/// <summary>A button on a specific DirectInput device (e.g. a wheel or button box).</summary>
public sealed record ButtonBinding(Guid DeviceInstanceGuid, string? DeviceName, int ButtonIndex)
{
    public override string ToString() => $"\"{DeviceName}\", button {ButtonIndex + 1}";
}

/// <summary>
/// Lets the user assign controller buttons to recorder actions directly in the app and polls them
/// on a background thread, so it works while the app isn't focused. Binding is done via "learn mode":
/// all connected devices are polled and the first button pressed is assigned. Devices that aren't
/// attached (e.g. a wheel that is powered off) are skipped and retried lazily.
/// </summary>
public sealed class ButtonBindingManager : IDisposable
{
    private const string KeyPath = global::XrPerf.Contracts.ControlProtocol.AppRegistryKey + @"\Buttons";

    private sealed class BoundEntry
    {
        public required ButtonAction Action;
        public required ButtonBinding Binding;
        public Joystick? Joystick;
        public bool WasPressed;
    }

    private readonly DirectInput _directInput = new();
    private readonly List<BoundEntry> _entries = new();
    private readonly object _entriesLock = new();
    private Thread? _pollThread;
    private volatile bool _running;

    private Thread? _learnThread;
    private volatile bool _learning;

    /// <summary>Raised on the poll thread when a bound button transitions from released to pressed.</summary>
    public event Action<ButtonAction>? ActionTriggered;

    /// <summary>Raised on a background thread once learn mode has assigned a button.</summary>
    public event Action<ButtonAction, ButtonBinding>? Learned;

    public bool IsLearning => _learning;

    public ButtonBindingManager()
    {
        Load();
    }

    public ButtonBinding? GetBinding(ButtonAction action)
    {
        lock (_entriesLock)
            return _entries.FirstOrDefault(e => e.Action == action)?.Binding;
    }

    public void SetBinding(ButtonAction action, ButtonBinding? binding)
    {
        BoundEntry? removed;
        lock (_entriesLock)
        {
            removed = _entries.FirstOrDefault(e => e.Action == action);
            if (removed != null) _entries.Remove(removed);
            if (binding != null)
            {
                _entries.Add(new BoundEntry
                {
                    Action = action,
                    Binding = binding,
                    Joystick = TryCreateJoystick(binding.DeviceInstanceGuid),
                    // Avoid firing immediately for the button that was just pressed during learning.
                    WasPressed = true,
                });
            }
        }
        ReleaseJoystick(removed?.Joystick);
        Save(action, binding);
        if (binding != null) EnsurePolling();
    }

    /// <summary>Starts learn mode: the first button pressed on any connected device is assigned to <paramref name="action"/>.</summary>
    public void StartLearning(ButtonAction action)
    {
        CancelLearning();
        _learning = true;
        _learnThread = new Thread(() => LearnLoop(action)) { IsBackground = true, Name = "Button Learn" };
        _learnThread.Start();
    }

    public void CancelLearning()
    {
        _learning = false;
        if (_learnThread != null && _learnThread != Thread.CurrentThread)
            _learnThread.Join(TimeSpan.FromSeconds(1));
        _learnThread = null;
    }

    private void LearnLoop(ButtonAction action)
    {
        var sticks = new List<(DeviceInstance Info, Joystick Stick, bool[] Initial)>();
        try
        {
            foreach (var device in _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
            {
                try
                {
                    var stick = new Joystick(_directInput, device.InstanceGuid);
                    stick.Acquire();
                    stick.Poll();
                    // Ignore buttons that are already held when learning starts.
                    sticks.Add((device, stick, (bool[])stick.GetCurrentState().Buttons.Clone()));
                }
                catch
                {
                    // Not acquirable or not attached; skip it.
                }
            }

            while (_learning)
            {
                foreach (var (info, stick, initial) in sticks)
                {
                    try
                    {
                        stick.Poll();
                        var buttons = stick.GetCurrentState().Buttons;
                        for (int i = 0; i < buttons.Length; i++)
                        {
                            if (buttons[i] && !initial[i])
                            {
                                _learning = false;
                                var binding = new ButtonBinding(info.InstanceGuid, info.InstanceName.Trim(), i);
                                SetBinding(action, binding);
                                Learned?.Invoke(action, binding);
                                return;
                            }
                            if (!buttons[i]) initial[i] = false;
                        }
                    }
                    catch
                    {
                        // Device may have been unplugged; keep scanning others.
                    }
                }
                Thread.Sleep(16);
            }
        }
        finally
        {
            foreach (var (_, stick, _) in sticks) ReleaseJoystick(stick);
        }
    }

    private Joystick? TryCreateJoystick(Guid deviceInstanceGuid)
    {
        try
        {
            var joystick = new Joystick(_directInput, deviceInstanceGuid);
            joystick.Acquire();
            return joystick;
        }
        catch
        {
            return null;
        }
    }

    private static void ReleaseJoystick(Joystick? joystick)
    {
        if (joystick is null) return;
        try
        {
            joystick.Unacquire();
            joystick.Dispose();
        }
        catch
        {
            // Ignore errors tearing down a possibly-unplugged device.
        }
    }

    private void EnsurePolling()
    {
        if (_running) return;
        _running = true;
        _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "Button Poll" };
        _pollThread.Start();
    }

    private void PollLoop()
    {
        var retryAt = new Dictionary<BoundEntry, DateTime>();
        while (_running)
        {
            List<BoundEntry> snapshot;
            lock (_entriesLock) snapshot = new List<BoundEntry>(_entries);

            foreach (var entry in snapshot)
            {
                try
                {
                    if (entry.Joystick is null)
                    {
                        // Lazily (re)acquire a device that shows up later, but don't hammer DirectInput.
                        if (retryAt.TryGetValue(entry, out var next) && DateTime.UtcNow < next) continue;
                        entry.Joystick = TryCreateJoystick(entry.Binding.DeviceInstanceGuid);
                        if (entry.Joystick is null)
                        {
                            retryAt[entry] = DateTime.UtcNow.AddSeconds(2);
                            continue;
                        }
                    }

                    entry.Joystick.Poll();
                    var buttons = entry.Joystick.GetCurrentState().Buttons;
                    int idx = entry.Binding.ButtonIndex;
                    bool pressed = idx >= 0 && idx < buttons.Length && buttons[idx];
                    if (pressed && !entry.WasPressed && !_learning)
                        ActionTriggered?.Invoke(entry.Action);
                    entry.WasPressed = pressed;
                }
                catch
                {
                    ReleaseJoystick(entry.Joystick);
                    entry.Joystick = null;
                }
            }
            Thread.Sleep(16);
        }
    }

    // ---- Persistence (HKCU\Software\XrPerf\Buttons, value per action: "guid|button|device name") ----

    private void Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null) return;
            foreach (ButtonAction action in Enum.GetValues<ButtonAction>())
            {
                if (key.GetValue(action.ToString()) is not string raw) continue;
                var parts = raw.Split('|', 3);
                if (parts.Length < 2 || !Guid.TryParse(parts[0], out var guid) || !int.TryParse(parts[1], out var button)) continue;
                lock (_entriesLock)
                {
                    _entries.Add(new BoundEntry
                    {
                        Action = action,
                        Binding = new ButtonBinding(guid, parts.Length > 2 ? parts[2] : null, button),
                    });
                }
            }
        }
        catch (Exception) { }

        bool any;
        lock (_entriesLock) any = _entries.Count > 0;
        if (any) EnsurePolling();
    }

    private static void Save(ButtonAction action, ButtonBinding? binding)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (binding is null)
                key.DeleteValue(action.ToString(), throwOnMissingValue: false);
            else
                key.SetValue(action.ToString(), $"{binding.DeviceInstanceGuid}|{binding.ButtonIndex}|{binding.DeviceName}");
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        CancelLearning();
        _running = false;
        _pollThread?.Join(TimeSpan.FromSeconds(1));
        lock (_entriesLock)
        {
            foreach (var e in _entries) ReleaseJoystick(e.Joystick);
            _entries.Clear();
        }
        _directInput.Dispose();
    }
}
