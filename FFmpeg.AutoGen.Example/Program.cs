namespace FFmpeg.AutoGen.Example
{
    using System;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;


    internal class Program
    {
        private static int counter;

        private static void Main(string[] args)
        {
            Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
            Console.WriteLine("Runnung in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");

            FFmpegBinariesHelper.RegisterFFmpegBinaries();

            Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");

            SetupLogging();

            var urls = Directory.GetFiles(@"R:\xxx", "*", SearchOption.AllDirectories);

            Console.WriteLine("Decoding...");
            Parallel.ForEach(urls, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                url =>
                {
                    //while (true)
                    try
                    {
                        DecodeAllFramesToImages(url);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(url + " failed: " + ex.Message);
                    }
                });

            return;
            Console.WriteLine("Encoding...");
            EncodeImagesToH264();
        }

        [Flags]
        /*
        /// <summary>AV_LOG_PANIC = 0</summary>
        /// <summary>AV_LOG_FATAL = 8</summary>
        /// <summary>AV_LOG_ERROR = 16</summary>
        /// <summary>AV_LOG_WARNING = 24</summary>
        /// <summary>AV_LOG_INFO = 32</summary>
        /// <summary>AV_LOG_VERBOSE = 40</summary>
        /// <summary>AV_LOG_DEBUG = 48</summary>
        /// <summary>AV_LOG_TRACE = 56</summary>
*/
        private enum LogLevel
        {
            Panic = 0,
            Fatal = 8,
            Error = 16,
            Warning = 24,
            Info = 32,
            Verbose = 40,
            Debug = 48,
            Trace = 56,
        }

        private static unsafe void SetupLogging()
        {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_INFO);

            // do not convert to local function
            av_log_set_callback_callback logCallback = (p0, level, format, vl) =>
            {
                if (level > ffmpeg.av_log_get_level()) return;

                var lineSize = 1024;
                var lineBuffer = stackalloc byte[lineSize];
                var printPrefix = 1;
                ffmpeg.av_log_format_line(p0, level, format, vl, lineBuffer, lineSize, &printPrefix);
                var line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("0x{0:x2} ({1}) ", level, (LogLevel)level);
                Console.Write(line);
                Console.ResetColor();
            };

            ffmpeg.av_log_set_callback(logCallback);
        }

        private static VideoStreamDecoder TryGetDecoder(string url)
        {
            Console.WriteLine("probing: " + url);
            var result = VideoStreamDecoder.TryCreate(url, out var errorMessage);
            if (result == null)
                Console.WriteLine(url + ": " + errorMessage);
            return result;
        }

        private static unsafe void DecodeAllFramesToImages(string url)
        {
            using (var vsd = TryGetDecoder(url))
            {
                if (vsd == null || vsd.CodecName == "ansi")
                    return;
                Console.WriteLine($"codec name: {vsd.CodecName}");

                vsd.OutputDictionary();

                var sourceSize = vsd.FrameSize;
                var sourcePixelFormat = vsd.PixelFormat;
                var destinationSize = sourceSize;
                var destinationPixelFormat = AVPixelFormat.AV_PIX_FMT_BGRA;
                using (var vfc = new VideoFrameConverter(sourceSize, sourcePixelFormat, destinationSize, destinationPixelFormat))
                {
                    var frameNumber = 1;
                    while (vsd.TryDecodeNextFrame(out var frame) /*&& frameNumber < 10*/)
                    {
                        if (frame.metadata != null)
                            VideoStreamDecoder.OutputDictionary(frame.metadata, "frame " + frameNumber);

                        var total = Interlocked.Increment(ref counter);
                        var filename = $"fr.{total}.{frameNumber:D8}.jpg";
                        if (total % 1000 == 0)
                        {
                            Console.WriteLine("{0:n1}k frames; {1:n0} bytes", total / 1000.0, GC.GetTotalMemory(true));

                            vfc.Convert(in frame, out var convertedFrame);
                            var buffer = (IntPtr)convertedFrame.data[0];
                            var stride = convertedFrame.linesize[0];
                            using (var bitmap = new Bitmap(width: convertedFrame.width, height: convertedFrame.height, stride: stride, format: PixelFormat.Format32bppArgb, scan0: buffer))
                                bitmap.Save(filename, ImageFormat.Jpeg);
                            Console.WriteLine($"{url}{filename} R{frame.repeat_pict}x  {frame.pict_type}  bet:{frame.best_effort_timestamp}  {vsd.GetTime(frame.best_effort_timestamp)}  dts:{frame.pkt_dts}  pts:{frame.pts}  pktpts:{frame.pkt_pts}");
                        }

                        //vfc.Convert(in frame, out var convertedFrame);
                        //var buffer = (IntPtr)convertedFrame.data[0];
                        //var stride = convertedFrame.linesize[0];

                        //using (var bitmap = new Bitmap(width: convertedFrame.width, height: convertedFrame.height, stride: stride, format: PixelFormat.Format32bppArgb, scan0: buffer))
                        //    bitmap.Save(filename, ImageFormat.Jpeg);

                        //var source = BitmapSource.Create(convertedFrame.width, convertedFrame.height, 96, 96, PixelFormats.Bgra32, null, buffer, stride * convertedFrame.height, stride);
                        //var bf = BitmapFrame.Create(source);
                        //var encoder = new JpegBitmapEncoder() { QualityLevel = 50 };
                        //encoder.Frames.Add(bf);
                        //using (var stream = File.Create(filename))
                        //{
                        //    encoder.Save(stream);
                        //}

                        //Console.WriteLine($"{url}{filename} R{frame.repeat_pict}x  {frame.pict_type}  bet:{frame.best_effort_timestamp}  {vsd.GetTime(frame.best_effort_timestamp)}  dts:{frame.pkt_dts}  pts:{frame.pts}  pktpts:{frame.pkt_pts}");
                        frameNumber++;
                    }
                }
            }
        }

        private static unsafe void EncodeImagesToH264()
        {
            var frameFiles = Directory.GetFiles(".", "frame.*.jpg").OrderBy(x => x).ToArray();
            var fistFrameImage = Image.FromFile(frameFiles.First());

            var outputFileName = "out.h264";
            var fps = 25;
            var sourceSize = fistFrameImage.Size;
            var sourcePixelFormat = AVPixelFormat.AV_PIX_FMT_BGR24;
            var destinationSize = sourceSize;
            var destinationPixelFormat = AVPixelFormat.AV_PIX_FMT_YUV420P;
            using (var vfc = new VideoFrameConverter(sourceSize, sourcePixelFormat, destinationSize, destinationPixelFormat))
            {
                using (var fs = File.Open(outputFileName, FileMode.Create)) // be advise only ffmpeg based player (like ffplay or vlc) can play this file, for the others you need to go through muxing
                {
                    using (var vse = new H264VideoStreamEncoder(fs, fps, destinationSize))
                    {
                        var frameNumber = 0;
                        foreach (var frameFile in frameFiles)
                        {
                            byte[] bitmapData;

                            using (var frameImage = Image.FromFile(frameFile))
                            using (var frameBitmap = frameImage is Bitmap bitmap ? bitmap : new Bitmap(frameImage))
                            {
                                bitmapData = GetBitmapData(frameBitmap);
                            }

                            fixed (byte* pBitmapData = bitmapData)
                            {
                                var data = new byte_ptrArray8 { [0] = pBitmapData };
                                var linesize = new int_array8 { [0] = bitmapData.Length / sourceSize.Height };
                                var frame = new AVFrame
                                {
                                    data = data,
                                    linesize = linesize,
                                    height = sourceSize.Height
                                };
                                vfc.Convert(in frame, out var convertedFrame);
                                convertedFrame.pts = frameNumber * fps;
                                vse.Encode(convertedFrame);
                            }

                            Console.WriteLine($"frame: {frameNumber}");
                            frameNumber++;
                        }
                    }
                }
            }
        }

        private static byte[] GetBitmapData(Bitmap frameBitmap)
        {
            var bitmapData = frameBitmap.LockBits(new Rectangle(Point.Empty, frameBitmap.Size), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                var length = bitmapData.Stride * bitmapData.Height;
                var data = new byte[length];
                Marshal.Copy(bitmapData.Scan0, data, 0, length);
                return data;
            }
            finally
            {
                frameBitmap.UnlockBits(bitmapData);
            }
        }
    }
}
