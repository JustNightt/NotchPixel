using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;

namespace OpenPixel.Services
{
    // ========================================================================
    // Video wallpaper round-trip verification.
    //
    // Writes a short uncompressed AVI by hand, then decodes it through the exact
    // Media Foundation source-reader path the wallpaper uses and asserts that a
    // coloured frame arrived. Uncompressed AVI is read by the AVI source that
    // ships with Media Foundation, so the test does not depend on an H.264
    // encoder MFT being installed - but the decode path under test is identical.
    // ========================================================================
    public static class VideoTestClip
    {
        private const int Width = 320;
        private const int Height = 240;
        private const int Fps = 30;

        public static int Run(string path, bool generateClip = true, bool verbose = true)
        {
            path ??= Path.Combine(Path.GetTempPath(), "openpixel-video-test.avi");
            if (generateClip)
            {
                try
                {
                    WriteAvi(path, 20);
                }
                catch (Exception ex)
                {
                    if (verbose) Console.WriteLine("VIDEO_TEST_FAIL encode " + ex.GetType().Name);
                    return 2;
                }
            }
            else if (!File.Exists(path))
            {
                if (verbose) Console.WriteLine("VIDEO_TEST_FAIL missing " + path);
                return 2;
            }

            using var decoder = new VideoWallpaperDecoder();
            decoder.Start(path);

            bool decoded = false;
            bool coloured = false;
            string lastError = "";
            int frameW = 0, frameH = 0;

            for (int i = 0; i < 60; ++i)
            {
                if (decoder.HasVideo)
                {
                    using var target = new Bitmap(Width, Height);
                    using (var g = Graphics.FromImage(target))
                    {
                        if (decoder.DrawFrame(g, new Rectangle(0, 0, Width, Height)))
                        {
                            decoded = true;
                            frameW = decoder.FrameWidth;
                            frameH = decoder.FrameHeight;
                            try
                            {
                                target.Save(Path.Combine(Environment.CurrentDirectory, "video_test_frame.png"),
                                    System.Drawing.Imaging.ImageFormat.Png);
                            }
                            catch { }
                            Color sample = target.GetPixel(16, 16);
                            // A correctly decoded gradient is never pure black.
                            coloured = sample.R > 8 || sample.G > 8 || sample.B > 8;
                            break;
                        }
                    }
                }
                lastError = decoder.LastError;
                Thread.Sleep(100);
            }

            decoder.Stop();

            if (verbose)
            {
                Console.WriteLine(decoded
                    ? $"VIDEO_TEST_{(coloured ? "PASS" : "FAIL")} {frameW}x{frameH} coloured={coloured}"
                    : $"VIDEO_TEST_FAIL decode {lastError}");
                if (!decoded) Console.WriteLine("VIDEO_TEST_DIAG " + decoder.Diagnostics);
            }

            // Keep a failing clip around for diagnosis; remove it on success.
            if (decoded && coloured)
            {
                if (generateClip) { try { File.Delete(path); } catch { } }
            }
            else if (verbose)
            {
                Console.WriteLine("VIDEO_TEST_CLIP " + path);
            }
            return decoded && coloured ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // Minimal RIFF/AVI writer: 24-bit uncompressed BGR, bottom-up.
        // ------------------------------------------------------------------
        private static void WriteAvi(string path, int frameCount)
        {
            int stride = Width * 4;
            int frameSize = stride * Height;
            int microSecPerFrame = 1_000_000 / Fps;

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var w = new BinaryWriter(stream, Encoding.ASCII);

            // RIFF header (size patched at the end).
            WriteFourCC(w, "RIFF");
            long riffSizePos = stream.Position;
            w.Write(0);
            WriteFourCC(w, "AVI ");

            // ---- hdrl ----
            long hdrlStart = stream.Position;
            WriteFourCC(w, "LIST");
            long hdrlSizePos = stream.Position;
            w.Write(0);
            WriteFourCC(w, "hdrl");

            // avih
            WriteFourCC(w, "avih");
            w.Write(56);
            w.Write(microSecPerFrame);
            w.Write(frameSize * Fps);
            w.Write(0);
            w.Write(0x10);              // AVIF_HASINDEX
            w.Write(frameCount);
            w.Write(0);
            w.Write(1);
            w.Write(frameSize);
            w.Write(Width);
            w.Write(Height);
            for (int i = 0; i < 4; ++i) w.Write(0);

            // ---- strl ----
            long strlStart = stream.Position;
            WriteFourCC(w, "LIST");
            long strlSizePos = stream.Position;
            w.Write(0);
            WriteFourCC(w, "strl");

            WriteFourCC(w, "strh");
            w.Write(56);
            WriteFourCC(w, "vids");
            w.Write(0);                 // fccHandler: uncompressed BI_RGB
            w.Write(0);                 // flags
            w.Write((short)0);          // priority
            w.Write((short)0);          // language
            w.Write(0);                 // initial frames
            w.Write(1);                 // scale
            w.Write(Fps);               // rate
            w.Write(0);                 // start
            w.Write(frameCount);        // length
            w.Write(frameSize);         // suggested buffer
            w.Write(-1);                // quality
            w.Write(frameSize);         // sample size
            w.Write((short)0);          // rcFrame left
            w.Write((short)0);          // top
            w.Write((short)Width);      // right
            w.Write((short)Height);     // bottom

            WriteFourCC(w, "strf");
            w.Write(40);
            w.Write(40);                // biSize
            w.Write(Width);
            w.Write(Height);            // bottom-up
            w.Write((short)1);          // planes
            w.Write((short)32);         // bit count
            w.Write(0);                 // BI_RGB
            w.Write(frameSize);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            w.Write(0);

            PatchSize(w, strlSizePos, (int)(stream.Position - strlStart - 8));
            PatchSize(w, hdrlSizePos, (int)(stream.Position - hdrlStart - 8));

            // ---- movi ----
            long moviStart = stream.Position;
            WriteFourCC(w, "LIST");
            long moviSizePos = stream.Position;
            w.Write(0);
            WriteFourCC(w, "movi");
            long moviDataStart = moviStart + 12;   // first chunk header position

            var offsets = new long[frameCount];
            var sizes = new int[frameCount];
            var pixels = new byte[frameSize];

            for (int frame = 0; frame < frameCount; ++frame)
            {
                offsets[frame] = stream.Position - moviDataStart + 4; // relative to 'movi' fourcc
                sizes[frame] = frameSize;

                for (int y = 0; y < Height; ++y)
                {
                    int row = (Height - 1 - y) * stride; // bottom-up storage
                    for (int x = 0; x < Width; ++x)
                    {
                        int i = row + x * 4;
                        pixels[i] = (byte)((x + frame * 4) % 256);                     // B
                        pixels[i + 1] = (byte)((y * 2 + frame * 2) % 256);             // G
                        pixels[i + 2] = (byte)(180 - (y * 120 / Math.Max(1, Height))); // R
                        pixels[i + 3] = 255;
                    }
                }

                WriteFourCC(w, "00db");
                w.Write(frameSize);
                w.Write(pixels);
                if ((frameSize & 1) != 0) w.Write((byte)0);
            }

            PatchSize(w, moviSizePos, (int)(stream.Position - moviStart - 8));

            // ---- idx1 ----
            WriteFourCC(w, "idx1");
            w.Write(frameCount * 16);
            for (int frame = 0; frame < frameCount; ++frame)
            {
                WriteFourCC(w, "00db");
                w.Write(0x10);                  // AVIIF_KEYFRAME
                w.Write((int)offsets[frame]);
                w.Write(sizes[frame]);
            }

            PatchSize(w, riffSizePos, (int)(stream.Position - 8));
            w.Flush();
        }

        private static void WriteFourCC(BinaryWriter w, string fourcc)
        {
            for (int i = 0; i < 4; ++i) w.Write((byte)fourcc[i]);
        }

        private static void PatchSize(BinaryWriter w, long position, int size)
        {
            long current = w.BaseStream.Position;
            w.BaseStream.Position = position;
            w.Write(size);
            w.BaseStream.Position = current;
        }
    }
}
