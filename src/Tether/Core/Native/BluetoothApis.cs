using System.Runtime.InteropServices;

namespace Tether.Core.Native;

/// <summary>Classic Win32 Bluetooth APIs from bthprops.cpl / BluetoothApis.dll.</summary>
internal static class BluetoothApis
{
    public const uint ServiceDisable = 0x00;
    public const uint ServiceEnable = 0x01;
    public const int ErrorSuccess = 0;
    public const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct BluetoothDeviceInfo
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public int fConnected;
        public int fRemembered;
        public int fAuthenticated;
        public SystemTime stLastSeen;
        public SystemTime stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;

        public static BluetoothDeviceInfo Create(ulong address) => new()
        {
            dwSize = (uint)Marshal.SizeOf<BluetoothDeviceInfo>(),
            Address = address,
            szName = string.Empty,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BluetoothFindRadioParams
    {
        public uint dwSize;
    }

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    public static extern IntPtr BluetoothFindFirstRadio(ref BluetoothFindRadioParams p, out IntPtr radio);

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BluetoothFindNextRadio(IntPtr find, out IntPtr radio);

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    public static extern int BluetoothGetDeviceInfo(IntPtr radio, ref BluetoothDeviceInfo info);

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    public static extern int BluetoothEnumerateInstalledServices(IntPtr radio, ref BluetoothDeviceInfo info, ref uint serviceCount, [In, Out] Guid[]? services);

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    public static extern int BluetoothSetServiceState(IntPtr radio, ref BluetoothDeviceInfo info, ref Guid service, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    /// <summary>Returns handles to every local radio. Caller closes them.</summary>
    public static List<IntPtr> GetRadios()
    {
        var radios = new List<IntPtr>();
        var p = new BluetoothFindRadioParams { dwSize = (uint)Marshal.SizeOf<BluetoothFindRadioParams>() };
        var find = BluetoothFindFirstRadio(ref p, out var radio);
        if (find == IntPtr.Zero) return radios;
        try
        {
            do { radios.Add(radio); } while (BluetoothFindNextRadio(find, out radio));
        }
        finally
        {
            BluetoothFindRadioClose(find);
        }
        return radios;
    }
}

internal static class DwmApi
{
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
    }

    public static void SetRoundedCorners(IntPtr hwnd)
    {
        int v = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int));
    }

    public static void SetBorderColor(IntPtr hwnd, System.Windows.Media.Color c)
    {
        int v = c.R | (c.G << 8) | (c.B << 16); // COLORREF is 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref v, sizeof(int));
    }
}

internal static class User32
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
