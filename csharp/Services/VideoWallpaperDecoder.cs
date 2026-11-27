using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenPixel.Services
{
    // ========================================================================
    // Live video wallpaper decoder (Media Foundation Source Reader).
    //
    // Decodes the first video stream of an .mp4/.mkv file on a worker thread,
    // paces it against the container frame rate, loops at end of stream and
    // exposes the latest frame as a bitmap for the wallpaper window.
    //
    // Not every source can hand back RGB32 directly (uncompressed AVI and some
    // MKV/HEVC streams cannot), so the decoder negotiates RGB32 first and falls
    // back to NV12 with a YUV conversion. Audio streams are never selected, and
    // any failure leaves HasVideo false so the app draws its fallback backdrop.
    // ========================================================================
    public sealed class VideoWallpaperDecoder : IDisposable
    {
        private const int MF_SOURCE_READER_FIRST_VIDEO_STREAM = unchecked((int)0xFFFFFFFC);
        private const int MF_SOURCE_READER_ALL_STREAMS = unchecked((int)0xFFFFFFFE);
        private const int MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;
        private const int MF_SOURCE_READERF_ERROR = 0x00000004;
        private const uint MF_MT_INTERLACE_MODE_PROGRESSIVE = 2;

        private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-feef521167cf");
        private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");

        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        private static extern int MFCreateSourceReaderFromURL(string pwszURL, MediaFoundationInterop.IMFAttributes pAttributes, out IMFSourceReader ppSourceReader);

        [ComImport, Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection(int dwStreamIndex, out bool pfSelected);
            [PreserveSig] int SetStreamSelection(int dwStreamIndex, bool fSelected);
            [PreserveSig] int GetNativeMediaType(int dwStreamIndex, int dwMediaTypeIndex, out MediaFoundationInterop.IMFMediaType ppMediaType);
            [PreserveSig] int GetCurrentMediaType(int dwStreamIndex, out MediaFoundationInterop.IMFMediaType ppMediaType);
            [PreserveSig] int SetCurrentMediaType(int dwStreamIndex, IntPtr pdwReserved, MediaFoundationInterop.IMFMediaType pMediaType);
            [PreserveSig] int SetCurrentPosition(ref Guid guidTimeFormat, ref PROPVARIANT varPosition);
            [PreserveSig] int ReadSample(int dwStreamIndex, int dwControlFlags, out int pdwActualStreamIndex, out int pdwStreamFlags, out long pllTimestamp, out MediaFoundationInterop.IMFSample ppSample);
            [PreserveSig] int Flush(int dwStreamIndex);
            [PreserveSig] int GetServiceForStream(int dwStreamIndex, ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
            [PreserveSig] int GetPresentationAttribute(int dwStreamIndex, ref Guid guidAttribute, IntPtr pvarAttribute);
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public long hVal;
        }

        private enum SourceFormat { Native, Rgb32, Nv12 }

        private readonly object _frameLock = new();
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly ManualResetEventSlim _resume = new(true);
        private Thread _thread;
        private Bitmap _front;
        private Bitmap _back;
        private string _path = "";
        private SourceFormat _format = SourceFormat.Rgb32;
        private volatile bool _paused;
        private volatile bool _hasVideo;
        private volatile string _lastError = "";
        private int _streamIndex = MF_SOURCE_READER_FIRST_VIDEO_STREAM;
        private int _width;
        private int _height;

        public bool HasVideo => _hasVideo;
        public string Diagnostics { get; private set; } = "";
        public string LastError => _lastError;
        public int FrameWidth => _width;
        public int FrameHeight => _height;
        public bool IsPaused => _paused;
        public string CurrentPath => _path;

        public void Start(string path)
        {
            Stop();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _lastError = "missing-file";
                return;
            }

            _path = path;
            _stop.Reset();
            _resume.Set();
            _thread = new Thread(DecodeLoop)
            {
                IsBackground = true,
                Name = "OpenPixel.VideoWallpaper"
            };
            _thread.Start();
        }

        public void Pause()
        {
            _paused = true;
            _resume.Reset();
        }

        public void Resume()
        {
            _paused = false;
            _resume.Set();
        }

        public void Stop()
        {
            _stop.Set();
            _resume.Set();
            Thread thread = _thread;
            _thread = null;
            if (thread != null && thread.IsAlive)
            {
                try { thread.Join(1500); } catch { }
            }
            lock (_frameLock)
            {
                _front?.Dispose();
                _back?.Dispose();
                _front = null;
                _back = null;
                _width = 0;
                _height = 0;
                _hasVideo = false;
            }
        }

        // Paints the latest decoded frame into the destination rectangle.
        public bool DrawFrame(Graphics g, Rectangle destination, ColorMatrix matrix = null)
        {
            if (g == null) return false;
            lock (_frameLock)
            {
                if (_front == null || _width <= 0 || _height <= 0) return false;
                try
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    if (matrix != null)
                    {
                        using var attributes = new ImageAttributes();
                        attributes.SetColorMatrix(matrix);
                        g.DrawImage(_front, destination, 0, 0, _front.Width, _front.Height, GraphicsUnit.Pixel, attributes);
                    }
                    else
                    {
                        g.DrawImage(_front, destination);
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        // Average colour of the current frame: feeds glass tinting from the
        // moving wallpaper instead of a frozen colour.
        public bool TryGetAverageColor(out Color color)
        {
            color = Color.Black;
            lock (_frameLock)
            {
                if (_front == null) return false;
                try
                {
                    long r = 0, g2 = 0, b = 0, n = 0;
                    int stepX = Math.Max(1, _width / 32);
                    int stepY = Math.Max(1, _height / 18);
                    for (int y = 0; y < _height; y += stepY)
                    {
                        for (int x = 0; x < _width; x += stepX)
                        {
                            Color c = _front.GetPixel(x, y);
                            r += c.R; g2 += c.G; b += c.B; ++n;
                        }
                    }
                    if (n == 0) return false;
                    color = Color.FromArgb(255, (int)(r / n), (int)(g2 / n), (int)(b / n));
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private void DecodeLoop()
        {
            bool mfStarted = false;
            IMFSourceReader reader = null;
            try
            {
                int hr = MediaFoundationInterop.MFStartup(MediaFoundationInterop.MF_VERSION, 0);
                if (hr < 0) { _lastError = "mf-startup-0x" + hr.ToString("X8"); return; }
                mfStarted = true;

                if (!Open(ref reader, out int width, out int height, out double fps)) return;

                _width = width;
                _height = height;
                _hasVideo = true;
                _lastError = "";
                _resume.Wait();

                long frameTicks = (long)(1000.0 / Math.Clamp(fps, 1.0, 120.0));
                long nextDue = Environment.TickCount64;

                while (!_stop.IsSet)
                {
                    if (_paused)
                    {
                        _resume.Wait(100);
                        continue;
                    }

                    hr = reader.ReadSample(_streamIndex, 0, out _, out int flags, out _, out MediaFoundationInterop.IMFSample sample);
                    if (hr < 0)
                    {
                        _lastError = "read-0x" + hr.ToString("X8");
                        break;
                    }

                    if ((flags & MF_SOURCE_READERF_ERROR) != 0)
                    {
                        _lastError = "stream-error";
                        break;
                    }

                    if (sample != null)
                    {
                        try { CopySample(sample); }
                        finally { Marshal.ReleaseComObject(sample); }
                    }

                    if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                    {
                        if (!SeekHome(reader))
                        {
                            Marshal.ReleaseComObject(reader);
                            reader = null;
                            if (!Open(ref reader, out _, out _, out _)) break;
                        }
                        nextDue = Environment.TickCount64;
                        continue;
                    }

                    nextDue += frameTicks;
                    long wait = nextDue - Environment.TickCount64;
                    if (wait > 0) _stop.Wait((int)Math.Min(wait, 100));
                    else if (wait < -250) nextDue = Environment.TickCount64;
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.GetType().Name;
            }
            finally
            {
                if (reader != null)
                {
                    try { Marshal.ReleaseComObject(reader); } catch { }
                }
                _hasVideo = false;
                if (mfStarted)
                {
                    try { MediaFoundationInterop.MFShutdown(); } catch { }
                }
            }
        }

        // Tries the fast RGB32 path first, then NV12. Each attempt opens a fresh
        // reader because a rejected media type leaves the previous one unusable.
        private bool Open(ref IMFSourceReader reader, out int width, out int height, out double fps)
        {
            width = 0;
            height = 0;
            fps = 30.0;

            string errors = "";
            foreach (SourceFormat candidate in new[] { SourceFormat.Rgb32, SourceFormat.Nv12 })
            {
                IMFSourceReader attempt = null;
                int attemptW = 0, attemptH = 0;
                double attemptFps = 30.0;

                if (TryOpen(candidate, out attempt, out attemptW, out attemptH, out attemptFps))
                {
                    reader = attempt;
                    _format = candidate;
                    width = attemptW;
                    height = attemptH;
                    fps = attemptFps;
                    return true;
                }

                errors += candidate + "=" + _lastError + "; ";
                if (attempt != null)
                {
                    try { Marshal.ReleaseComObject(attempt); } catch { }
                }
            }
            _lastError = errors.TrimEnd();
            return false;
        }

        private bool TryOpen(SourceFormat format, out IMFSourceReader reader, out int width, out int height, out double fps)
        {
            reader = null;
            width = 0;
            height = 0;
            fps = 30.0;

            MediaFoundationInterop.IMFAttributes attributes = null;
            try
            {
                if (MediaFoundationInterop.MFCreateAttributes(out attributes, 2) >= 0 && attributes != null)
                {
                    Guid videoProcessing = MfInteropGuid.SourceReaderEnableVideoProcessing;
                    attributes.SetUINT32(ref videoProcessing, 1);
                }

                int hr = MFCreateSourceReaderFromURL(_path, attributes, out reader);
                if (hr < 0 || reader == null)
                {
                    _lastError = "open-0x" + hr.ToString("X8");
                    return false;
                }

                int selectHr = reader.SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, false);
                int streamIndex = FindVideoStream(reader);
                Diagnostics += $"videoStream={streamIndex};";
                if (streamIndex < 0) streamIndex = MF_SOURCE_READER_FIRST_VIDEO_STREAM;
                if (selectHr >= 0) selectHr = reader.SetStreamSelection(streamIndex, true);
                Diagnostics += $"select=0x{selectHr:X8};";
                if (selectHr < 0)
                {
                    _lastError = "select-0x" + selectHr.ToString("X8");
                    return false;
                }

                int nativeHr = reader.GetNativeMediaType(streamIndex, 0, out MediaFoundationInterop.IMFMediaType nativeType);
                Diagnostics += $"native=0x{nativeHr:X8};";
                if (nativeType != null)
                {
                    try
                    {
                        Guid nativeSize = MF_MT_FRAME_SIZE;
                        if (nativeType.GetUINT64(ref nativeSize, out ulong nativePacked) >= 0)
                        {
                            Diagnostics += $"nativeSize={nativePacked >> 32}x{nativePacked & 0xFFFFFFFF};";
                        }
                    }
                    finally { Marshal.ReleaseComObject(nativeType); }
                }

                if (format != SourceFormat.Native)
                {
                    if (MediaFoundationInterop.MFCreateMediaType(out MediaFoundationInterop.IMFMediaType mediaType) < 0)
                    {
                        _lastError = "media-type";
                        return false;
                    }

                    try
                    {
                        Guid majorKey = MfInteropGuid.MtMajorType;
                        Guid majorValue = MfInteropGuid.MediaTypeVideo;
                        Guid subtypeKey = MfInteropGuid.MtSubtype;
                        Guid subtypeValue = format == SourceFormat.Rgb32 ? MFVideoFormat_RGB32 : MFVideoFormat_NV12;
                        Guid interlaceKey = MF_MT_INTERLACE_MODE;

                        mediaType.SetGUID(ref majorKey, ref majorValue);
                        mediaType.SetGUID(ref subtypeKey, ref subtypeValue);
                        mediaType.SetUINT32(ref interlaceKey, MF_MT_INTERLACE_MODE_PROGRESSIVE);

                        hr = reader.SetCurrentMediaType(streamIndex, IntPtr.Zero, mediaType);
                        if (hr < 0)
                        {
                            _lastError = "set-" + format + "-0x" + hr.ToString("X8");
                            return false;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(mediaType);
                    }
                }

                if (reader.GetCurrentMediaType(streamIndex, out MediaFoundationInterop.IMFMediaType current) >= 0 && current != null)
                {
                    try
                    {
                        Guid sizeKey = MF_MT_FRAME_SIZE;
                        Guid rateKey = MF_MT_FRAME_RATE;
                        if (current.GetUINT64(ref sizeKey, out ulong packed) >= 0)
                        {
                            width = (int)(packed >> 32);
                            height = (int)(packed & 0xFFFFFFFF);
                        }
                        if (current.GetUINT64(ref rateKey, out ulong rate) >= 0)
                        {
                            uint num = (uint)(rate >> 32);
                            uint den = (uint)(rate & 0xFFFFFFFF);
                            if (den > 0 && num > 0) fps = (double)num / den;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(current);
                    }
                }

                if (width <= 0 || height <= 0)
                {
                    _lastError = "no-dimensions";
                    return false;
                }

                // Probe one sample: some readers accept the type but fail on read.
                Diagnostics += $"dims={width}x{height};fps={fps:0.##};";
                hr = reader.ReadSample(streamIndex, 0, out _, out int probeFlags, out _, out MediaFoundationInterop.IMFSample probe);
                Diagnostics += $"probe=0x{hr:X8}/flags={probeFlags};sample={(probe != null)};";
                if (probe != null) { try { Marshal.ReleaseComObject(probe); } catch { } }
                if (hr < 0 || (probeFlags & MF_SOURCE_READERF_ERROR) != 0)
                {
                    _lastError = "probe-" + format + "-0x" + hr.ToString("X8");
                    return false;
                }

                if (format == SourceFormat.Native)
                {
                    _lastError = "native-probe-ok";
                    return false;
                }

                lock (_frameLock)
                {
                    _front?.Dispose();
                    _back?.Dispose();
                    _front = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                    _back = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                }
                _streamIndex = streamIndex;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.GetType().Name;
                return false;
            }
            finally
            {
                if (attributes != null)
                {
                    try { Marshal.ReleaseComObject(attributes); } catch { }
                }
            }
        }

        // Locates the real stream number of the first video stream. Using the
        // MF_SOURCE_READER_FIRST_VIDEO_STREAM alias works on paper but some
        // readers only accept a concrete index for SetCurrentMediaType/ReadSample.
        private static int FindVideoStream(IMFSourceReader reader)
        {
            Guid majorKey = MfInteropGuid.MtMajorType;
            for (int i = 0; i < 32; ++i)
            {
                if (reader.GetNativeMediaType(i, 0, out MediaFoundationInterop.IMFMediaType type) < 0 || type == null) continue;
                try
                {
                    if (type.GetGUID(ref majorKey, out Guid major) >= 0 && major == MfInteropGuid.MediaTypeVideo) return i;
                }
                finally
                {
                    Marshal.ReleaseComObject(type);
                }
            }
            return -1;
        }

        private static bool SeekHome(IMFSourceReader reader)
        {
            try
            {
                Guid timeFormat = Guid.Empty;
                var position = new PROPVARIANT { vt = 20 /* VT_I8 */, hVal = 0 };
                return reader.SetCurrentPosition(ref timeFormat, ref position) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private unsafe void CopySample(MediaFoundationInterop.IMFSample sample)
        {
            if (sample.ConvertToContiguousBuffer(out MediaFoundationInterop.IMFMediaBuffer buffer) < 0 || buffer == null) return;
            IntPtr source = IntPtr.Zero;
            bool locked = false;
            try
            {
                if (buffer.Lock(out source, out _, out uint currentLength) < 0 || source == IntPtr.Zero) return;
                locked = true;

                lock (_frameLock)
                {
                    if (_back == null || _front == null) return;
                    int width = _width;
                    int height = _height;
                    if (width <= 0 || height <= 0) return;

                    BitmapData data = _back.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        if (_format == SourceFormat.Rgb32) CopyRgb32(source, currentLength, data, width, height);
                        else CopyNv12(source, currentLength, data, width, height);
                    }
                    finally
                    {
                        _back.UnlockBits(data);
                    }

                    (_front, _back) = (_back, _front);
                }
            }
            finally
            {
                if (locked) { try { buffer.Unlock(); } catch { } }
                try { Marshal.ReleaseComObject(buffer); } catch { }
            }
        }

        private static unsafe void CopyRgb32(IntPtr source, uint length, BitmapData data, int width, int height)
        {
            int rowBytes = width * 4;
            int srcStride = (int)length / Math.Max(1, height);
            if (srcStride < rowBytes) srcStride = rowBytes;
            byte* srcBase = (byte*)source;
            byte* dstBase = (byte*)data.Scan0;
            for (int y = 0; y < height; ++y)
            {
                // Media Foundation RGB32 is bottom-up.
                byte* srcRow = srcBase + (long)(height - 1 - y) * srcStride;
                byte* dstRow = dstBase + (long)y * data.Stride;
                Buffer.MemoryCopy(srcRow, dstRow, rowBytes, Math.Min(rowBytes, srcStride));
            }
        }

        private static unsafe void CopyNv12(IntPtr source, uint length, BitmapData data, int width, int height)
        {
            // Total NV12 payload is stride * height * 1.5, so the luma stride is
            // the payload divided by 1.5 * height (width when unpadded).
            int srcStride = (int)(length / Math.Max(1.0, height * 1.5));
            if (srcStride < width) srcStride = width;

            byte* yBase = (byte*)source;
            byte* uvBase = yBase + (long)srcStride * height;
            byte* dstBase = (byte*)data.Scan0;

            for (int y = 0; y < height; ++y)
            {
                byte* yRow = yBase + (long)y * srcStride;
                byte* uvRow = uvBase + (long)(y / 2) * srcStride;
                byte* dstRow = dstBase + (long)y * data.Stride;
                for (int x = 0; x < width; ++x)
                {
                    int luma = yRow[x];
                    int u = uvRow[(x / 2) * 2] - 128;
                    int v = uvRow[(x / 2) * 2 + 1] - 128;
                    int r = (int)(luma + 1.402 * v);
                    int g = (int)(luma - 0.344136 * u - 0.714136 * v);
                    int b = (int)(luma + 1.772 * u);
                    int i = x * 4;
                    dstRow[i] = (byte)Math.Clamp(b, 0, 255);
                    dstRow[i + 1] = (byte)Math.Clamp(g, 0, 255);
                    dstRow[i + 2] = (byte)Math.Clamp(r, 0, 255);
                    dstRow[i + 3] = 255;
                }
            }
        }

        public void Dispose() => Stop();

        private static class MfInteropGuid
        {
            public static readonly Guid MtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
            public static readonly Guid MtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
            public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
            public static readonly Guid SourceReaderEnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        }
    }
}
