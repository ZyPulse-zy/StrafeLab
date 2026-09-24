using System.Runtime.InteropServices;
using StrafeLab.Core;
using System.Windows.Interop;
using System.Windows.Threading;

namespace StrafeLab.Input;

public sealed class RawInputSource : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
    private const ushort RI_KEY_BREAK = 0x01;
    private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
    private const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;

    private IntPtr _windowHandle;
    private bool _attached;
    private double _deliveryConfidence=.92;

    private Dispatcher? _dispatcher;
    private Thread? _thread;
    public void Start()
    {
        if (_thread != null) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        
        _thread = new Thread(() => {
            try {
                _dispatcher = Dispatcher.CurrentDispatcher;
                using var source = new HwndSource(new HwndSourceParameters("StrafeLabInput") {ParentWindow=new IntPtr(-3),Width=0,Height=0});
                source.AddHook((IntPtr hwnd,int msg,IntPtr wp,IntPtr lp,ref bool handled)=>{ProcessWindowMessage(msg,lp);return IntPtr.Zero;});
                if(!Attach(source.Handle))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                ready.TrySetResult(); Dispatcher.Run(); Detach();
            } catch(Exception ex) {ready.TrySetException(ex);}
        }) {IsBackground=true,Name="StrafeLab Raw Input"};
        _thread.SetApartmentState(ApartmentState.STA);_thread.Start();ready.Task.GetAwaiter().GetResult();
    }
    public event EventHandler<InputEvent>? InputReceived;
    public event EventHandler? MouseMoved;
    public bool IsAttached => _attached;

    public bool Attach(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return false;
        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = HID_USAGE_PAGE_GENERIC,
                Usage = HID_USAGE_GENERIC_KEYBOARD,
                Flags = RIDEV_INPUTSINK,
                Target = windowHandle
            },
            new RawInputDevice
            {
                UsagePage = HID_USAGE_PAGE_GENERIC,
                Usage = HID_USAGE_GENERIC_MOUSE,
                Flags = RIDEV_INPUTSINK,
                Target = windowHandle
            }
        };

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            return false;
        }

        _windowHandle = windowHandle;
        _attached = true;
        return true;
    }

    public void Detach()
    {
        if (!_attached) return;
        var devices = new[]
        {
            new RawInputDevice { UsagePage = HID_USAGE_PAGE_GENERIC, Usage = HID_USAGE_GENERIC_KEYBOARD, Flags = RIDEV_REMOVE },
            new RawInputDevice { UsagePage = HID_USAGE_PAGE_GENERIC, Usage = HID_USAGE_GENERIC_MOUSE, Flags = RIDEV_REMOVE }
        };
        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
        _windowHandle = IntPtr.Zero;
        _attached = false;
    }

    public bool ProcessWindowMessage(int message, IntPtr lParam)
    {
        if (message != WM_INPUT || !_attached) return false;

        var timestampUs = TimeUtil.NowMicroseconds();
        uint queueAge=unchecked((uint)(Environment.TickCount-GetMessageTime()));
        _deliveryConfidence=queueAge<=8?.92:queueAge<=20?.75:.4;
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size == 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) == uint.MaxValue)
            {
                return false;
            }

            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            var data=IntPtr.Add(buffer,(int)headerSize);
            if (header.Type == 1 && size >= headerSize+Marshal.SizeOf<RawKeyboard>())
            {
                ProcessKeyboard(Marshal.PtrToStructure<RawKeyboard>(data),timestampUs);return true;
            }
            if (header.Type == 0 && size >= headerSize+Marshal.SizeOf<RawMouse>())
            {
                ProcessMouse(Marshal.PtrToStructure<RawMouse>(data),timestampUs);return true;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return false;
    }

    public void Dispose()
    {
        _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        if (_thread?.IsAlive==true && _thread != Thread.CurrentThread) _thread.Join(3000);
    }

    private void ProcessKeyboard(RawKeyboard keyboard, long timestampUs)
    {
        var control = keyboard.MakeCode switch
        {
            0x11 => InputControl.W,
            0x1E => InputControl.A,
            0x1F => InputControl.S,
            0x20 => InputControl.D,
            0x2A or 0x36 => InputControl.Walk,
            0x1D => InputControl.Crouch,
            0x39 => InputControl.Jump,
            _ => (InputControl?)null
        };
        if (control is null) return;

        InputReceived?.Invoke(this, new InputEvent
        {
            TimestampUs = timestampUs,
            Control = control.Value,
            Action = (keyboard.Flags & RI_KEY_BREAK) == 0 ? InputAction.Down : InputAction.Up,
            Source = InputSourceKind.RawInput,
            Confidence = _deliveryConfidence
        });
    }

    private void ProcessMouse(RawMouse mouse, long timestampUs)
    {
        if (mouse.LastX != 0 || mouse.LastY != 0) MouseMoved?.Invoke(this, EventArgs.Empty);
        if ((mouse.ButtonFlags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0)
        {
            InputReceived?.Invoke(this, new InputEvent
            {
                TimestampUs = timestampUs,
                Control = InputControl.Mouse1,
                Action = InputAction.Down,
                Source = InputSourceKind.RawInput,
                Confidence = _deliveryConfidence
            });
        }

        if ((mouse.ButtonFlags & RI_MOUSE_LEFT_BUTTON_UP) != 0)
        {
            InputReceived?.Invoke(this, new InputEvent
            {
                TimestampUs = timestampUs,
                Control = InputControl.Mouse1,
                Action = InputAction.Up,
                Source = InputSourceKind.RawInput,
                Confidence = _deliveryConfidence
            });
        }
    }

    [DllImport("user32.dll")] private static extern int GetMessageTime();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint numberOfDevices,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        uint headerSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit, Size=24)]
    public struct RawMouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(6)] public ushort ButtonData;
        [FieldOffset(8)] public uint RawButtons;
        [FieldOffset(12)] public int LastX;
        [FieldOffset(16)] public int LastY;
        [FieldOffset(20)] public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RawInputData
    {
        [FieldOffset(0)] public RawMouse Mouse;
        [FieldOffset(0)] public RawKeyboard Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInput
    {
        public RawInputHeader Header;
        public RawInputData Data;
    }
}
