using System.Runtime.InteropServices;

namespace ScrcpyX;

public sealed class H264Decoder : IDisposable
{
    private const int H264 = 27;
    private const int Yuv420P = 0;
    private const int YuvJ420P = 12;
    private const int Nv12 = 23;

    private IntPtr _context;
    private IntPtr _packet;
    private IntPtr _frame;
    private byte[] _pixels = [];
    private int _destW;
    private int _destH;

    public H264Decoder(string dllDirectory)
    {
        SetDllDirectory(dllDirectory);
        NativeLibrary.Load(Path.Combine(dllDirectory, "avutil-60.dll"));
        NativeLibrary.Load(Path.Combine(dllDirectory, "swresample-6.dll"));
        NativeLibrary.Load(Path.Combine(dllDirectory, "avcodec-62.dll"));
        var codec = avcodec_find_decoder(H264);
        if (codec == IntPtr.Zero)
            throw new InvalidOperationException("找不到 H.264 解码器。");
        _context = avcodec_alloc_context3(codec);
        av_opt_set(_context, "threads", "4", 0);
        av_opt_set(_context, "flags", "low_delay", 0);
        if (avcodec_open2(_context, codec, IntPtr.Zero) < 0)
            throw new InvalidOperationException("H.264 解码器没有打开。");
        _packet = av_packet_alloc();
        _frame = av_frame_alloc();
    }

    public bool Decode(byte[] annexB, bool present, int viewW, int viewH, out byte[] pixels, out int width, out int height)
    {
        pixels = _pixels;
        width = _destW;
        height = _destH;
        if (annexB.Length == 0 || av_new_packet(_packet, annexB.Length) < 0)
            return false;
        Marshal.Copy(annexB, 0, Marshal.ReadIntPtr(_packet, 24), annexB.Length);
        var sent = avcodec_send_packet(_context, _packet);
        av_packet_unref(_packet);
        if (sent < 0)
        {
            avcodec_flush_buffers(_context);
            return false;
        }
        var produced = false;
        while (avcodec_receive_frame(_context, _frame) == 0)
        {
            if (!present)
            {
                av_frame_unref(_frame);
                continue;
            }
            var srcW = Marshal.ReadInt32(_frame, 104);
            var srcH = Marshal.ReadInt32(_frame, 108);
            var format = Marshal.ReadInt32(_frame, 116);
            if (srcW is < 16 or > 8000 || srcH is < 16 or > 8000)
            {
                av_frame_unref(_frame);
                continue;
            }
            var destW = viewW >= 16 ? viewW : Math.Min(srcW, 480);
            var destH = viewH >= 16 ? viewH : Math.Max(1, srcH * destW / srcW);
            if (_pixels.Length != destW * destH * 4)
                _pixels = new byte[destW * destH * 4];
            Convert(srcW, srcH, format, destW, destH);
            _destW = destW;
            _destH = destH;
            pixels = _pixels;
            width = destW;
            height = destH;
            produced = true;
            av_frame_unref(_frame);
        }
        return produced;
    }

    private unsafe void Convert(int srcW, int srcH, int format, int destW, int destH)
    {
        var y = (byte*)Marshal.ReadIntPtr(_frame, 0);
        var u = (byte*)Marshal.ReadIntPtr(_frame, 8);
        var v = (byte*)Marshal.ReadIntPtr(_frame, 16);
        var yStride = Marshal.ReadInt32(_frame, 64);
        var uStride = Marshal.ReadInt32(_frame, 68);
        var vStride = Marshal.ReadInt32(_frame, 72);
        var nv12 = format == Nv12;
        if (format is not (Yuv420P or YuvJ420P or Nv12))
            return;
        fixed (byte* dest = _pixels)
        {
            for (var dy = 0; dy < destH; dy++)
            {
                var sy = Math.Min(srcH - 1, dy * srcH / destH);
                var sy2 = Math.Min(srcH - 1, sy + 1);
                var y0 = y + sy * yStride;
                var y1 = y + sy2 * yStride;
                for (var dx = 0; dx < destW; dx++)
                {
                    var sx = Math.Min(srcW - 1, dx * srcW / destW);
                    var sx2 = Math.Min(srcW - 1, sx + 1);
                    var yy = (y0[sx] + y0[sx2] + y1[sx] + y1[sx2]) >> 2;
                    int uu;
                    int vv;
                    if (nv12)
                    {
                        var pair = u + (sy / 2) * uStride + (sx & ~1);
                        uu = pair[0] - 128;
                        vv = pair[1] - 128;
                    }
                    else
                    {
                        uu = u[(sy / 2) * uStride + sx / 2] - 128;
                        vv = v[(sy / 2) * vStride + sx / 2] - 128;
                    }
                    var c = yy - 16;
                    if (c < 0)
                        c = 0;
                    var r = (298 * c + 409 * vv + 128) >> 8;
                    var g = (298 * c - 100 * uu - 208 * vv + 128) >> 8;
                    var b = (298 * c + 516 * uu + 128) >> 8;
                    var pixel = dest + ((dy * destW + dx) * 4);
                    pixel[0] = (byte)Math.Clamp(b, 0, 255);
                    pixel[1] = (byte)Math.Clamp(g, 0, 255);
                    pixel[2] = (byte)Math.Clamp(r, 0, 255);
                    pixel[3] = 255;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_frame != IntPtr.Zero)
            av_frame_free(ref _frame);
        if (_packet != IntPtr.Zero)
            av_packet_free(ref _packet);
        if (_context != IntPtr.Zero)
            avcodec_free_context(ref _context);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);

    [DllImport("avutil-60.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr av_frame_alloc();

    [DllImport("avutil-60.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void av_frame_free(ref IntPtr frame);

    [DllImport("avutil-60.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void av_frame_unref(IntPtr frame);

    [DllImport("avutil-60.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int av_opt_set(IntPtr obj, string name, string value, int searchFlags);

    [DllImport("avcodec-62.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr avcodec_find_decoder(int id);

    [DllImport("avcodec-62.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr avcodec_alloc_context3(IntPtr codec);

    [DllImport("avcodec-62.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int avcodec_open2(IntPtr context, IntPtr codec, IntPtr options);

    [DllImport("avcodec-62.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void avcodec_free_context(ref IntPtr context);

    [DllImport("avcodec-62.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr av_packet_alloc();

    [DllImport("avcodec-62.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void av_packet_free(ref IntPtr packet);

    [DllImport("avcodec-62.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int av_new_packet(IntPtr packet, int size);

    [DllImport("avcodec-62.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void av_packet_unref(IntPtr packet);

    [DllImport("avcodec-62.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int avcodec_send_packet(IntPtr context, IntPtr packet);

    [DllImport("avcodec-62.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int avcodec_receive_frame(IntPtr context, IntPtr frame);

    [DllImport("avcodec-62.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void avcodec_flush_buffers(IntPtr context);
}
