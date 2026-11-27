using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NotchPixel.E2E.Tests.Infrastructure;

public static class MediaAndFixtureBuilder
{
    public static string CreateValidMp4(
        string filePath,
        int durationSeconds = 2,
        int width = 320,
        int height = 180)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        byte[] bytes = BuildMinimalIsoBmffMp4(durationSeconds, width, height);
        File.WriteAllBytes(filePath, bytes);
        return filePath;
    }

    public static string CreateUnicodeMp4Fixture(
        string directory,
        string fileName = "测试_видео_α.mp4",
        int durationSeconds = 2)
    {
        Directory.CreateDirectory(directory);
        return CreateValidMp4(Path.Combine(directory, fileName), durationSeconds);
    }

    public static string CreateValidMkv(
        string filePath,
        int durationMs = 2000,
        int width = 320,
        int height = 180)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        byte[] bytes = BuildMinimalEbmlMatroska(durationMs, width, height);
        File.WriteAllBytes(filePath, bytes);
        return filePath;
    }

    public static string CreateUnicodeMkvFixture(
        string directory,
        string fileName = "日本語_тест.mkv",
        int durationMs = 2000)
    {
        Directory.CreateDirectory(directory);
        return CreateValidMkv(Path.Combine(directory, fileName), durationMs);
    }

    public static string CreateCorruptVideoFixture(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        byte[] garbage = Encoding.ASCII.GetBytes("NOT_A_VALID_MEDIA_CONTAINER_0000_CORRUPT_HEADER");
        File.WriteAllBytes(filePath, garbage);
        return filePath;
    }

    public static string CreateEmptyFixture(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(filePath, Array.Empty<byte>());
        return filePath;
    }

    // -------------------------------------------------------------------------
    // Deterministic ISO BMFF (.mp4) Builder (ftyp + moov + mdat)
    // -------------------------------------------------------------------------

    private static byte[] BuildMinimalIsoBmffMp4(int durationSec, int width, int height)
    {
        uint timescale = 1000;
        uint duration = (uint)Math.Max(1, durationSec) * timescale;

        // Minimal Annex-B / AVCC IDR NAL sample payload
        byte[] samplePayload =
        {
            0x00, 0x00, 0x00, 0x05, // NAL length = 5
            0x65, 0x88, 0x84, 0x21, 0xA0 // IDR slice stub
        };

        byte[] ftyp = Mp4Box("ftyp", ms =>
        {
            WriteAscii(ms, "isom");
            WriteU32(ms, 0x00000200);
            WriteAscii(ms, "isom");
            WriteAscii(ms, "iso2");
            WriteAscii(ms, "avc1");
            WriteAscii(ms, "mp41");
        });

        byte[] mdat = Mp4Box("mdat", ms => ms.Write(samplePayload, 0, samplePayload.Length));

        // Build moov with placeholder stco offset, then patch stco offset cleanly
        byte[] BuildMoov(uint chunkOffset)
        {
            byte[] mvhd = Mp4FullBox("mvhd", 0, 0, ms =>
            {
                WriteU32(ms, 0); // creation_time
                WriteU32(ms, 0); // modification_time
                WriteU32(ms, timescale);
                WriteU32(ms, duration);
                WriteU32(ms, 0x00010000); // rate 1.0
                WriteU16(ms, 0x0100);     // volume 1.0
                ms.Write(new byte[10], 0, 10); // reserved
                WriteIdentityMatrix(ms);
                ms.Write(new byte[24], 0, 24); // pre_defined
                WriteU32(ms, 2); // next_track_ID
            });

            byte[] tkhd = Mp4FullBox("tkhd", 0, 0x000007, ms =>
            {
                WriteU32(ms, 0);
                WriteU32(ms, 0);
                WriteU32(ms, 1); // track_ID
                WriteU32(ms, 0);
                WriteU32(ms, duration);
                ms.Write(new byte[8], 0, 8);
                WriteU16(ms, 0); // layer
                WriteU16(ms, 0); // alternate_group
                WriteU16(ms, 0); // volume
                WriteU16(ms, 0);
                WriteIdentityMatrix(ms);
                WriteU32(ms, (uint)(width << 16));
                WriteU32(ms, (uint)(height << 16));
            });

            byte[] mdhd = Mp4FullBox("mdhd", 0, 0, ms =>
            {
                WriteU32(ms, 0);
                WriteU32(ms, 0);
                WriteU32(ms, timescale);
                WriteU32(ms, duration);
                WriteU16(ms, 0x55C4); // language "und"
                WriteU16(ms, 0);
            });

            byte[] hdlr = Mp4FullBox("hdlr", 0, 0, ms =>
            {
                WriteU32(ms, 0);
                WriteAscii(ms, "vide");
                ms.Write(new byte[12], 0, 12);
                byte[] name = Encoding.ASCII.GetBytes("VideoHandler\0");
                ms.Write(name, 0, name.Length);
            });

            byte[] vmhd = Mp4FullBox("vmhd", 0, 1, ms =>
            {
                WriteU16(ms, 0);
                ms.Write(new byte[6], 0, 6);
            });

            byte[] urlBox = Mp4FullBox("url ", 0, 1, _ => { });
            byte[] dref = Mp4FullBox("dref", 0, 0, ms =>
            {
                WriteU32(ms, 1);
                ms.Write(urlBox, 0, urlBox.Length);
            });
            byte[] dinf = Mp4Box("dinf", ms => ms.Write(dref, 0, dref.Length));

            // Minimal avcC (Baseline Profile level 3.0)
            byte[] avcC = Mp4Box("avcC", ms =>
            {
                ms.WriteByte(1);    // configurationVersion
                ms.WriteByte(0x42); // AVCProfileIndication (Baseline)
                ms.WriteByte(0xC0); // profile_compatibility
                ms.WriteByte(0x1E); // AVCLevelIndication (3.0)
                ms.WriteByte(0xFF); // lengthSizeMinusOne = 3 (4 bytes)
                ms.WriteByte(0xE1); // numOfSequenceParameterSets = 1
                byte[] sps = { 0x67, 0x42, 0xC0, 0x1E, 0xD9, 0x01, 0x41, 0xFB, 0x01, 0x10, 0x00, 0x00, 0x03, 0x00, 0x10, 0x00, 0x00, 0x03, 0x03, 0xC0, 0xF1, 0x62, 0xE4, 0x80 };
                WriteU16(ms, (ushort)sps.Length);
                ms.Write(sps, 0, sps.Length);
                ms.WriteByte(1); // numOfPictureParameterSets = 1
                byte[] pps = { 0x68, 0xCE, 0x3C, 0x80 };
                WriteU16(ms, (ushort)pps.Length);
                ms.Write(pps, 0, pps.Length);
            });

            byte[] avc1 = Mp4Box("avc1", ms =>
            {
                ms.Write(new byte[6], 0, 6);
                WriteU16(ms, 1); // data_reference_index
                ms.Write(new byte[16], 0, 16);
                WriteU16(ms, (ushort)width);
                WriteU16(ms, (ushort)height);
                WriteU32(ms, 0x00480000); // horizresolution 72 dpi
                WriteU32(ms, 0x00480000); // vertresolution 72 dpi
                WriteU32(ms, 0);
                WriteU16(ms, 1); // frame_count
                ms.Write(new byte[32], 0, 32); // compressorname
                WriteU16(ms, 0x0018); // depth = 24
                WriteU16(ms, 0xFFFF); // pre_defined = -1
                ms.Write(avcC, 0, avcC.Length);
            });

            byte[] stsd = Mp4FullBox("stsd", 0, 0, ms =>
            {
                WriteU32(ms, 1);
                ms.Write(avc1, 0, avc1.Length);
            });
            byte[] stts = Mp4FullBox("stts", 0, 0, ms =>
            {
                WriteU32(ms, 1);
                WriteU32(ms, 1);
                WriteU32(ms, duration);
            });
            byte[] stsc = Mp4FullBox("stsc", 0, 0, ms =>
            {
                WriteU32(ms, 1);
                WriteU32(ms, 1);
                WriteU32(ms, 1);
                WriteU32(ms, 1);
            });
            byte[] stsz = Mp4FullBox("stsz", 0, 0, ms =>
            {
                WriteU32(ms, (uint)samplePayload.Length);
                WriteU32(ms, 1);
            });
            byte[] stco = Mp4FullBox("stco", 0, 0, ms =>
            {
                WriteU32(ms, 1);
                WriteU32(ms, chunkOffset);
            });

            byte[] stbl = Mp4Box("stbl", ms =>
            {
                ms.Write(stsd); ms.Write(stts); ms.Write(stsc); ms.Write(stsz); ms.Write(stco);
            });
            byte[] minf = Mp4Box("minf", ms =>
            {
                ms.Write(vmhd); ms.Write(dinf); ms.Write(stbl);
            });
            byte[] mdia = Mp4Box("mdia", ms =>
            {
                ms.Write(mdhd); ms.Write(hdlr); ms.Write(minf);
            });
            byte[] trak = Mp4Box("trak", ms =>
            {
                ms.Write(tkhd); ms.Write(mdia);
            });
            return Mp4Box("moov", ms =>
            {
                ms.Write(mvhd); ms.Write(trak);
            });
        }

        byte[] moovSizeProbe = BuildMoov(0);
        uint mdatPayloadOffset = (uint)(ftyp.Length + moovSizeProbe.Length + 8);
        byte[] moov = BuildMoov(mdatPayloadOffset);

        using var outMs = new MemoryStream();
        outMs.Write(ftyp);
        outMs.Write(moov);
        outMs.Write(mdat);
        return outMs.ToArray();
    }

    private static byte[] Mp4Box(string type, Action<MemoryStream> writePayload)
    {
        using var body = new MemoryStream();
        writePayload(body);
        byte[] payload = body.ToArray();
        using var box = new MemoryStream(8 + payload.Length);
        WriteU32(box, (uint)(8 + payload.Length));
        WriteAscii(box, type);
        box.Write(payload, 0, payload.Length);
        return box.ToArray();
    }

    private static byte[] Mp4FullBox(string type, byte version, uint flags, Action<MemoryStream> writePayload)
    {
        return Mp4Box(type, ms =>
        {
            ms.WriteByte(version);
            ms.WriteByte((byte)((flags >> 16) & 0xFF));
            ms.WriteByte((byte)((flags >> 8) & 0xFF));
            ms.WriteByte((byte)(flags & 0xFF));
            writePayload(ms);
        });
    }

    private static void WriteIdentityMatrix(MemoryStream ms)
    {
        WriteU32(ms, 0x00010000); WriteU32(ms, 0); WriteU32(ms, 0);
        WriteU32(ms, 0); WriteU32(ms, 0x00010000); WriteU32(ms, 0);
        WriteU32(ms, 0); WriteU32(ms, 0); WriteU32(ms, 0x40000000);
    }

    private static void WriteAscii(MemoryStream ms, string fourCc)
    {
        byte[] b = Encoding.ASCII.GetBytes(fourCc);
        ms.Write(b, 0, b.Length);
    }

    private static void WriteU32(MemoryStream ms, uint v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, v);
        ms.Write(buf);
    }

    private static void WriteU16(MemoryStream ms, ushort v)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, v);
        ms.Write(buf);
    }

    // -------------------------------------------------------------------------
    // Deterministic EBML Matroska (.mkv) Builder
    // -------------------------------------------------------------------------

    private static byte[] BuildMinimalEbmlMatroska(int durationMs, int width, int height)
    {
        byte[] ebmlHeader = EbmlElement(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, ms =>
        {
            WriteEbmlUint(ms, new byte[] { 0x42, 0x86 }, 1); // EBMLVersion
            WriteEbmlUint(ms, new byte[] { 0x42, 0xF7 }, 1); // EBMLReadVersion
            WriteEbmlUint(ms, new byte[] { 0x42, 0xF2 }, 4); // EBMLMaxIDLength
            WriteEbmlUint(ms, new byte[] { 0x42, 0xF3 }, 8); // EBMLMaxSizeLength
            WriteEbmlString(ms, new byte[] { 0x42, 0x82 }, "matroska"); // DocType
            WriteEbmlUint(ms, new byte[] { 0x42, 0x87 }, 4); // DocTypeVersion
            WriteEbmlUint(ms, new byte[] { 0x42, 0x85 }, 2); // DocTypeReadVersion
        });

        byte[] segInfo = EbmlElement(new byte[] { 0x15, 0x49, 0xA9, 0x66 }, ms =>
        {
            WriteEbmlUint(ms, new byte[] { 0x2A, 0xD7, 0xB1 }, 1000000); // TimecodeScale = 1ms
            WriteEbmlFloat64(ms, new byte[] { 0x44, 0x89 }, Math.Max(100, durationMs));
            WriteEbmlString(ms, new byte[] { 0x4D, 0x80 }, "NotchPixelE2E");
            WriteEbmlString(ms, new byte[] { 0x57, 0x41 }, "NotchPixelE2E");
        });

        byte[] videoElem = EbmlElement(new byte[] { 0xE0 }, ms =>
        {
            WriteEbmlUint(ms, new byte[] { 0xB0 }, (ulong)width);
            WriteEbmlUint(ms, new byte[] { 0xBA }, (ulong)height);
        });

        byte[] trackEntry = EbmlElement(new byte[] { 0xAE }, ms =>
        {
            WriteEbmlUint(ms, new byte[] { 0xD7 }, 1); // TrackNumber
            WriteEbmlUint(ms, new byte[] { 0x73, 0xC5 }, 1); // TrackUID
            WriteEbmlUint(ms, new byte[] { 0x83 }, 1); // TrackType (1 = video)
            WriteEbmlString(ms, new byte[] { 0x86 }, "V_MPEG4/ISO/AVC"); // CodecID
            ms.Write(videoElem, 0, videoElem.Length);
        });

        byte[] tracks = EbmlElement(new byte[] { 0x16, 0x54, 0xAE, 0x6B }, ms =>
        {
            ms.Write(trackEntry, 0, trackEntry.Length);
        });

        byte[] cluster = EbmlElement(new byte[] { 0x1F, 0x43, 0xB6, 0x75 }, ms =>
        {
            WriteEbmlUint(ms, new byte[] { 0xE7 }, 0); // Timecode = 0
            // SimpleBlock: track 1 (0x81), timecode 0 (0x00 0x00), keyframe flag (0x80), 4-byte dummy frame
            byte[] blockPayload = { 0x81, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x01 };
            byte[] simpleBlock = EbmlElement(new byte[] { 0xA3 }, b => b.Write(blockPayload, 0, blockPayload.Length));
            ms.Write(simpleBlock, 0, simpleBlock.Length);
        });

        byte[] segment = EbmlElement(new byte[] { 0x18, 0x53, 0x80, 0x67 }, ms =>
        {
            ms.Write(segInfo);
            ms.Write(tracks);
            ms.Write(cluster);
        });

        byte[] total = new byte[ebmlHeader.Length + segment.Length];
        Buffer.BlockCopy(ebmlHeader, 0, total, 0, ebmlHeader.Length);
        Buffer.BlockCopy(segment, 0, total, ebmlHeader.Length, segment.Length);
        return total;
    }

    private static byte[] EbmlElement(byte[] id, Action<MemoryStream> writeBody)
    {
        using var body = new MemoryStream();
        writeBody(body);
        byte[] payload = body.ToArray();
        byte[] sizeVint = EncodeEbmlSize((ulong)payload.Length);
        using var outMs = new MemoryStream(id.Length + sizeVint.Length + payload.Length);
        outMs.Write(id, 0, id.Length);
        outMs.Write(sizeVint, 0, sizeVint.Length);
        outMs.Write(payload, 0, payload.Length);
        return outMs.ToArray();
    }

    private static void WriteEbmlUint(MemoryStream ms, byte[] id, ulong value)
    {
        byte[] raw = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(raw, value);
        int start = 0;
        while (start < 7 && raw[start] == 0) start++;
        int len = 8 - start;
        ms.Write(id, 0, id.Length);
        byte[] size = EncodeEbmlSize((ulong)len);
        ms.Write(size, 0, size.Length);
        ms.Write(raw, start, len);
    }

    private static void WriteEbmlString(MemoryStream ms, byte[] id, string text)
    {
        byte[] raw = Encoding.UTF8.GetBytes(text);
        ms.Write(id, 0, id.Length);
        byte[] size = EncodeEbmlSize((ulong)raw.Length);
        ms.Write(size, 0, size.Length);
        ms.Write(raw, 0, raw.Length);
    }

    private static void WriteEbmlFloat64(MemoryStream ms, byte[] id, double value)
    {
        byte[] raw = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(raw, value);
        ms.Write(id, 0, id.Length);
        byte[] size = EncodeEbmlSize(8);
        ms.Write(size, 0, size.Length);
        ms.Write(raw, 0, 8);
    }

    private static byte[] EncodeEbmlSize(ulong size)
    {
        if (size < 0x7F) return new[] { (byte)(0x80 | size) };
        if (size < 0x3FFF) return new[] { (byte)(0x40 | (size >> 8)), (byte)(size & 0xFF) };
        return new[]
        {
            (byte)(0x10 | ((size >> 24) & 0x0F)),
            (byte)((size >> 16) & 0xFF),
            (byte)((size >> 8) & 0xFF),
            (byte)(size & 0xFF)
        };
    }

    // -------------------------------------------------------------------------
    // Ephemeral Loopback Test-Double Servers (Test-only, 127.0.0.1:0)
    // -------------------------------------------------------------------------

    public sealed class MockObsWebSocketServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private bool _isRecording;
        private int _recordSeconds;

        public int Port { get; }
        public Uri WebSocketUri => new($"ws://127.0.0.1:{Port}");
        public bool IsRecording => _isRecording;
        public int RecordSeconds => _recordSeconds;

        public MockObsWebSocketServer(bool initialRecording = false, int initialSeconds = 0)
        {
            _isRecording = initialRecording;
            _recordSeconds = initialSeconds;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(() => AcceptClientsAsync(_cts.Token));
        }

        public void SetRecording(bool active, int seconds = 0)
        {
            _isRecording = active;
            _recordSeconds = seconds;
        }

        private async Task AcceptClientsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                }
                catch { break; }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                byte[] buf = new byte[4096];
                int read = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
                if (read <= 0) return;

                string req = Encoding.UTF8.GetString(buf, 0, read);
                string? key = null;
                foreach (string line in req.Split("\r\n"))
                {
                    if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                        key = line["Sec-WebSocket-Key:".Length..].Trim();
                }
                if (string.IsNullOrEmpty(key)) return;

                string accept = Convert.ToBase64String(
                    SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                string handshake =
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), ct).ConfigureAwait(false);

                // OBS WebSocket v5 Hello (op = 0)
                string hello = "{\"op\":0,\"d\":{\"obsWebSocketVersion\":\"5.3.0\",\"rpcVersion\":1}}";
                await SendWsTextFrameAsync(stream, hello, ct).ConfigureAwait(false);

                while (!ct.IsCancellationRequested)
                {
                    string? msg = await ReadWsTextFrameAsync(stream, ct).ConfigureAwait(false);
                    if (msg == null) break;

                    using var doc = JsonDocument.Parse(msg);
                    int op = doc.RootElement.TryGetProperty("op", out var opEl) ? opEl.GetInt32() : -1;
                    if (op == 1) // Identify
                    {
                        await SendWsTextFrameAsync(stream, "{\"op\":2,\"d\":{\"negotiatedRpcVersion\":1}}", ct).ConfigureAwait(false);
                    }
                    else if (op == 6) // Request
                    {
                        string reqType = doc.RootElement.GetProperty("d").GetProperty("requestType").GetString() ?? "";
                        string reqId = doc.RootElement.GetProperty("d").GetProperty("requestId").GetString() ?? "1";
                        if (reqType == "GetRecordStatus")
                        {
                            var ts = TimeSpan.FromSeconds(_recordSeconds);
                            string tc = $"{ts:hh\\:mm\\:ss\\.fff}";
                            long ms = _recordSeconds * 1000L;
                            string resp = JsonSerializer.Serialize(new
                            {
                                op = 7,
                                d = new
                                {
                                    requestType = "GetRecordStatus",
                                    requestId = reqId,
                                    requestStatus = new { result = true, code = 100 },
                                    responseData = new
                                    {
                                        outputActive = _isRecording,
                                        outputPaused = false,
                                        outputTimecode = tc,
                                        outputDuration = ms,
                                        outputBytes = _isRecording ? 102400L : 0L
                                    }
                                }
                            });
                            await SendWsTextFrameAsync(stream, resp, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        private static async Task SendWsTextFrameAsync(NetworkStream stream, string text, CancellationToken ct)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            using var ms = new MemoryStream();
            ms.WriteByte(0x81);
            if (payload.Length < 126)
            {
                ms.WriteByte((byte)payload.Length);
            }
            else
            {
                ms.WriteByte(126);
                ms.WriteByte((byte)(payload.Length >> 8));
                ms.WriteByte((byte)(payload.Length & 0xFF));
            }
            ms.Write(payload, 0, payload.Length);
            byte[] frame = ms.ToArray();
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        }

        private static async Task<string?> ReadWsTextFrameAsync(NetworkStream stream, CancellationToken ct)
        {
            byte[] hdr = new byte[2];
            int r = await stream.ReadAsync(hdr, 0, 2, ct).ConfigureAwait(false);
            if (r < 2) return null;
            int opcode = hdr[0] & 0x0F;
            if (opcode == 0x8) return null; // Close
            bool masked = (hdr[1] & 0x80) != 0;
            int len = hdr[1] & 0x7F;
            if (len == 126)
            {
                byte[] ext = new byte[2];
                await stream.ReadExactlyAsync(ext, ct).ConfigureAwait(false);
                len = (ext[0] << 8) | ext[1];
            }
            byte[] mask = new byte[4];
            if (masked) await stream.ReadExactlyAsync(mask, ct).ConfigureAwait(false);
            byte[] payload = new byte[len];
            if (len > 0) await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            if (masked)
            {
                for (int i = 0; i < len; i++) payload[i] ^= mask[i % 4];
            }
            return Encoding.UTF8.GetString(payload);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _acceptLoop.Wait(1000); } catch { }
            _cts.Dispose();
        }
    }

    public sealed class MockChatStreamServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly ConcurrentQueue<(string User, string Text, string Badge)> _messages = new();

        public int Port { get; }
        public string BaseHttpUrl => $"http://127.0.0.1:{Port}";

        public MockChatStreamServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void PushMessage(string user, string text, string badge = "") =>
            _messages.Enqueue((user, text, badge));

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    _ = Task.Run(() => HandleHttpAsync(client, ct), ct);
                }
                catch { break; }
            }
        }

        private async Task HandleHttpAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                byte[] buf = new byte[2048];
                int read = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
                if (read <= 0) return;

                var list = new List<object>();
                foreach (var m in _messages)
                {
                    list.Add(new { user = m.User, text = m.Text, badge = m.Badge });
                }
                string json = JsonSerializer.Serialize(new { live = true, messages = list });
                byte[] body = Encoding.UTF8.GetBytes(json);
                string header =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
                await stream.WriteAsync(body, ct).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _acceptLoop.Wait(1000); } catch { }
            _cts.Dispose();
        }
    }

    public sealed class MockDeckTlsServer : IDisposable
    {
        private readonly TcpListener _tlsListener;
        private readonly UdpClient? _udpServer;
        private readonly X509Certificate2 _cert;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _tlsLoop;
        private readonly Task? _udpLoop;
        private bool _allowPairing = true;

        public int TlsPort { get; }
        public int UdpPort { get; }
        public string CertFingerprintSha256 { get; }
        public string PairingCode { get; set; } = "482913";
        public string IssuedToken { get; set; } = "e2e-test-deck-token-v1";
        public string PcName { get; set; } = "TEST-PC-DECK";
        public string MacAddress { get; set; } = "AA:BB:CC:DD:EE:FF";
        public ConcurrentQueue<string> ReceivedRequests { get; } = new();

        public MockDeckTlsServer(bool enableUdpDiscovery = true)
        {
            _cert = CreateEphemeralSelfSignedCert("CN=NotchPixelTestDeck");
            CertFingerprintSha256 = Convert.ToHexString(SHA256.HashData(_cert.RawData)).ToLowerInvariant();

            _tlsListener = new TcpListener(IPAddress.Loopback, 0);
            _tlsListener.Start();
            TlsPort = ((IPEndPoint)_tlsListener.LocalEndpoint).Port;
            _tlsLoop = Task.Run(() => AcceptTlsLoopAsync(_cts.Token));

            if (enableUdpDiscovery)
            {
                _udpServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                UdpPort = ((IPEndPoint)_udpServer.Client.LocalEndPoint!).Port;
                _udpLoop = Task.Run(() => UdpLoopAsync(_cts.Token));
            }
        }

        public void SetAllowPairing(bool allow) => _allowPairing = allow;

        private static X509Certificate2 CreateEphemeralSelfSignedCert(string subject)
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 ephemeral = req.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(30));
            byte[] pfx = ephemeral.Export(X509ContentType.Pfx, "testpass");
            return X509CertificateLoader.LoadPkcs12(pfx, "testpass", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }

        private async Task UdpLoopAsync(CancellationToken ct)
        {
            if (_udpServer == null) return;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult rx = await _udpServer.ReceiveAsync(ct).ConfigureAwait(false);
                    string msg = Encoding.UTF8.GetString(rx.Buffer);
                    if (msg.StartsWith("OPENPIXEL_DECK_DISCOVER", StringComparison.Ordinal))
                    {
                        string respJson = JsonSerializer.Serialize(new
                        {
                            ip = "127.0.0.1",
                            tlsPort = TlsPort,
                            certFingerprint = CertFingerprintSha256,
                            mac = MacAddress,
                            pcName = PcName
                        });
                        byte[] resp = Encoding.UTF8.GetBytes(respJson);
                        await _udpServer.SendAsync(resp, resp.Length, rx.RemoteEndPoint).ConfigureAwait(false);
                    }
                }
                catch { break; }
            }
        }

        private async Task AcceptTlsLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await _tlsListener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    _ = Task.Run(() => HandleTlsClientAsync(client, ct), ct);
                }
                catch { break; }
            }
        }

        private async Task HandleTlsClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
            {
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _cert,
                        ClientCertificateRequired = false,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, ct).ConfigureAwait(false);

                byte[] buf = new byte[4096];
                int read = await ssl.ReadAsync(buf, ct).ConfigureAwait(false);
                if (read <= 0) return;

                string rawReq = Encoding.UTF8.GetString(buf, 0, read);
                string firstLine = rawReq.Split("\r\n", 2)[0];
                ReceivedRequests.Enqueue(firstLine);
                string[] parts = firstLine.Split(' ');
                string target = parts.Length > 1 ? parts[1] : "/";

                (int status, string json) = DispatchPath(target);
                string statusText = status switch
                {
                    200 => "OK",
                    401 => "Unauthorized",
                    403 => "Forbidden",
                    _ => "Not Found"
                };
                byte[] body = Encoding.UTF8.GetBytes(json);
                string header =
                    $"HTTP/1.1 {status} {statusText}\r\n" +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
                await ssl.WriteAsync(body, ct).ConfigureAwait(false);
                await ssl.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        private (int Status, string Json) DispatchPath(string target)
        {
            int qIdx = target.IndexOf('?');
            string path = qIdx >= 0 ? target[..qIdx] : target;
            string query = qIdx >= 0 ? target[(qIdx + 1)..] : string.Empty;

            string GetParam(string key)
            {
                foreach (string kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) continue;
                    if (Uri.UnescapeDataString(kv[..eq]).Equals(key, StringComparison.OrdinalIgnoreCase))
                        return Uri.UnescapeDataString(kv[(eq + 1)..].Replace('+', ' '));
                }
                return string.Empty;
            }

            if (path == "/deck/probe")
            {
                return (200, JsonSerializer.Serialize(new
                {
                    pcName = PcName,
                    ip = "127.0.0.1",
                    tlsPort = TlsPort,
                    certFingerprint = CertFingerprintSha256,
                    mac = MacAddress
                }));
            }

            if (path == "/deck/pair")
            {
                if (GetParam("code") != PairingCode)
                    return (401, "{\"paired\":false,\"error\":\"Invalid pairing code\"}");
                if (!_allowPairing)
                    return (403, "{\"paired\":false,\"error\":\"PC user denied pairing\"}");

                return (200, JsonSerializer.Serialize(new
                {
                    paired = true,
                    token = IssuedToken,
                    keyFingerprint = "e2e-key-fp",
                    certFingerprint = CertFingerprintSha256,
                    mac = MacAddress,
                    pcName = PcName
                }));
            }

            string token = GetParam("token");
            if (string.IsNullOrEmpty(token) || token != IssuedToken)
                return (401, "{\"error\":\"Device not paired or revoked\"}");

            if (path == "/deck/unpair")
            {
                IssuedToken = string.Empty;
                return (200, "{\"ok\":true,\"unpaired\":true}");
            }

            if (path == "/deck/state")
            {
                return (200, JsonSerializer.Serialize(new
                {
                    pcName = PcName,
                    cpu = 22,
                    gpu = 14,
                    ram = 48,
                    cpuTempAvailable = true,
                    cpuTempC = 54,
                    gpuTempAvailable = true,
                    gpuTempC = 49,
                    recording = false,
                    recordingSec = 0,
                    micMuted = false,
                    volume = 64,
                    muted = false,
                    media = new
                    {
                        hasSession = false,
                        isPlaying = false,
                        title = "",
                        artist = "",
                        app = "",
                        posSec = 0,
                        durSec = 0,
                        accentHex = "#2BE4EA"
                    },
                    apps = Array.Empty<object>()
                }));
            }

            return (200, "{\"ok\":true}");
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _tlsListener.Stop(); } catch { }
            try { _udpServer?.Close(); _udpServer?.Dispose(); } catch { }
            try { _tlsLoop.Wait(1000); } catch { }
            try { _udpLoop?.Wait(1000); } catch { }
            _cert.Dispose();
            _cts.Dispose();
        }
    }
}
