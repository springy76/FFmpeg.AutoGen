using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace FFmpeg.AutoGen.Example
{
    using System.Linq;

    public sealed unsafe class VideoStreamDecoder : IDisposable
    {
        private readonly AVCodecContext* _pCodecContext;
        private readonly AVFormatContext* _pFormatContext;
        private readonly int _streamIndex;
        private readonly AVFrame* _pFrame;
        private readonly AVPacket* _pPacket;

        private readonly AVStream* pStream = null;

        private VideoStreamDecoder(AVFormatContext* pFormatContext)
        {
            _pFormatContext = pFormatContext;
            try
            {
                ffmpeg.avformat_find_stream_info(_pFormatContext, null).ThrowExceptionIfError();

                // find the first video stream
                for (var i = 0; i < _pFormatContext->nb_streams; i++)
                {
                    var avStream = _pFormatContext->streams[i];
                    if (pStream == null && avStream->codec->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        pStream = avStream;
                        Console.Write("using ");
                    }

                    OutputDictionary(avStream->metadata, $"stream-{i}: {avStream->codec->codec_type} {(double)avStream->time_base.den / avStream->time_base.num:n3}");
                }

                if (pStream == null) throw new InvalidOperationException("Could not found video stream.");

                _streamIndex = pStream->index;
                _pCodecContext = pStream->codec;

                var codecId = _pCodecContext->codec_id;
                var pCodec = ffmpeg.avcodec_find_decoder(codecId);
                if (pCodec == null) throw new InvalidOperationException("Unsupported codec.");

                ffmpeg.avcodec_open2(_pCodecContext, pCodec, null).ThrowExceptionIfError();

                CodecName = ffmpeg.avcodec_get_name(codecId);
                FrameSize = new Size(_pCodecContext->width, _pCodecContext->height);
                PixelFormat = _pCodecContext->pix_fmt;

                _pPacket = ffmpeg.av_packet_alloc();
                _pFrame = ffmpeg.av_frame_alloc();
            }
            catch
            {
                this.Dispose();
                throw;
            }
        }

        public static VideoStreamDecoder TryCreate(string url, out string errorMessage)
        {
            var pFormatContext = ffmpeg.avformat_alloc_context();
            var error = ffmpeg.avformat_open_input(&pFormatContext, url, null, null);
            if (error < 0)
            {
                ffmpeg.avformat_free_context(pFormatContext);
                errorMessage = FFmpegHelper.av_strerror(error);
                return null;
            }

            errorMessage = null;
            return new VideoStreamDecoder(pFormatContext);
        }

        public string CodecName { get; }
        public Size FrameSize { get; }
        public AVPixelFormat PixelFormat { get; }

        public void Dispose()
        {
            if (_pFrame != null)
            {
                ffmpeg.av_frame_unref(_pFrame);
                ffmpeg.av_free(_pFrame);
            }

            if (_pPacket != null)
            {
                ffmpeg.av_packet_unref(_pPacket);
                ffmpeg.av_free(_pPacket);
            }

            if (_pCodecContext != null)
                ffmpeg.avcodec_close(_pCodecContext);

            var pFormatContext = _pFormatContext;
            if (pFormatContext != null)
                ffmpeg.avformat_close_input(&pFormatContext);
        }

        public TimeSpan? GetTime(long value)
        {
            if (value == long.MinValue)
                return null;
            var sec = (double)value * this.pStream->time_base.num / this.pStream->time_base.den;
            return TimeSpan.FromSeconds(sec);
        }

        public bool TryDecodeNextFrame(out AVFrame frame)
        {
            ffmpeg.av_frame_unref(_pFrame);
            int error;

            error = ffmpeg.avcodec_receive_frame(_pCodecContext, _pFrame);
            if (error != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                frame = *_pFrame;
                if (error == ffmpeg.AVERROR_EOF)
                    return false;

                error.ThrowExceptionIfError();
                return true;
            }

            do
            {
                try
                {
                    while (true)
                    {
                        error = ffmpeg.av_read_frame(_pFormatContext, _pPacket);
                        if (error == ffmpeg.AVERROR_EOF)
                        {
                            // trigger draining/flushing
                            ffmpeg.avcodec_send_packet(_pCodecContext, null).ThrowExceptionIfError();
                            break;
                            //frame = *_pFrame;
                            //return false;
                        }

                        error.ThrowExceptionIfError();

                        if (_pPacket->stream_index == _streamIndex)
                        {
                            ffmpeg.avcodec_send_packet(_pCodecContext, _pPacket).ThrowExceptionIfError();
                            break;
                        }

                        ffmpeg.av_packet_unref(_pPacket);
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(_pPacket);
                }

                error = ffmpeg.avcodec_receive_frame(_pCodecContext, _pFrame);
            } while (error == ffmpeg.AVERROR(ffmpeg.EAGAIN));

            frame = *_pFrame;
            if (error == ffmpeg.AVERROR_EOF)
                return false;

            error.ThrowExceptionIfError();
            return true;
        }

        public void OutputDictionary()
            => OutputDictionary(_pFormatContext->metadata, "CONTEXT (file)");

        public static void OutputDictionary(AVDictionary* dict, string title)
        {
            var info = GetDictionary(dict);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(title);
            Console.ResetColor();
            info.ToList().ForEach(x => Console.WriteLine($"{x.Key} = {x.Value}"));
        }

        private static IReadOnlyDictionary<string, string> GetDictionary(AVDictionary* dict)
        {
            AVDictionaryEntry* tag = null;
            var result = new Dictionary<string, string>();
            while ((tag = ffmpeg.av_dict_get(dict, "", tag, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
            {
                var key = Marshal.PtrToStringAnsi((IntPtr)tag->key);
                var value = Marshal.PtrToStringAnsi((IntPtr)tag->value);
                result.Add(key, value);
            }

            return result;
        }
    }
}
