// Minimal overlapped HID I/O with hard timeouts (FileStream's async reads can hang on cancel with HID handles).
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public sealed class HidIo : IDisposable {
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr ov);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, IntPtr written, IntPtr ov);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out int n, bool wait);
  [DllImport("kernel32.dll")] static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
  [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
  [DllImport("kernel32.dll")] static extern IntPtr CreateEvent(IntPtr sa, bool manual, bool initial, IntPtr name);
  [DllImport("kernel32.dll")] static extern bool ResetEvent(IntPtr h);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

  const int BufLen = 128;
  readonly SafeFileHandle h;
  readonly IntPtr ov, ev, buf;
  readonly int ovSize, evOffset;

  public SafeFileHandle Handle { get { return h; } }

  HidIo(SafeFileHandle handle) {
    h = handle;
    ovSize = IntPtr.Size == 8 ? 32 : 20; evOffset = IntPtr.Size == 8 ? 24 : 16;
    ov = Marshal.AllocHGlobal(ovSize);
    buf = Marshal.AllocHGlobal(BufLen);
    ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
  }

  public static HidIo Open(string path, bool write) {
    var handle = CreateFile(path, 0x80000000 | (write ? 0x40000000u : 0u), 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
    if (handle.IsInvalid) return null;
    return new HidIo(handle);
  }

  void ResetOverlapped() {
    for (int i = 0; i < ovSize; i++) Marshal.WriteByte(ov, i, 0);
    Marshal.WriteIntPtr(ov, evOffset, ev);
    ResetEvent(ev);
  }

  // true = finished within the time; false = timed out or failed (any pending I/O is cancelled and drained)
  bool Finish(int timeoutMs, out int n) {
    n = 0;
    if (WaitForSingleObject(ev, (uint)timeoutMs) != 0) {
      CancelIoEx(h, ov);
      GetOverlappedResult(h, ov, out n, true);
      return false;
    }
    return GetOverlappedResult(h, ov, out n, false);
  }

  public byte[] Read(int len, int timeoutMs) {
    ResetOverlapped();
    if (!ReadFile(h, buf, Math.Min(len, BufLen), IntPtr.Zero, ov) && Marshal.GetLastWin32Error() != 997) return null;
    int n;
    if (!Finish(timeoutMs, out n) || n <= 0) return null;
    var r = new byte[n];
    Marshal.Copy(buf, r, 0, n);
    return r;
  }

  public bool Write(byte[] data, int len, int timeoutMs) {
    ResetOverlapped();
    var tmp = new byte[len];
    Array.Copy(data, tmp, Math.Min(len, data.Length));
    Marshal.Copy(tmp, 0, buf, len);
    if (!WriteFile(h, buf, len, IntPtr.Zero, ov) && Marshal.GetLastWin32Error() != 997) return false;
    int n;
    return Finish(timeoutMs, out n);
  }

  public void Dispose() {
    try { CancelIoEx(h, IntPtr.Zero); } catch { }
    h.Dispose();
    CloseHandle(ev);
    Marshal.FreeHGlobal(ov);
    Marshal.FreeHGlobal(buf);
  }
}
