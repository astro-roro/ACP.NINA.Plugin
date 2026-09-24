using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Services {

    /// Stream content that reports how much of the stream has been sent, as
    /// a fraction from 0 to 1. The TS upload uses it to show a percentage in
    /// the dock while a large file goes out over a slow link.
    public class ProgressStreamContent : HttpContent {

        private const int ChunkSize = 64 * 1024;

        private readonly Stream source;
        private readonly IProgress<double> progress;

        public ProgressStreamContent(Stream source, IProgress<double> progress) {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.progress = progress;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context) {
            var total = source.CanSeek ? source.Length : -1L;
            var sent = 0L;
            var buffer = new byte[ChunkSize];
            progress?.Report(0.0);
            int read;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0) {
                await stream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                sent += read;
                if (total > 0) progress?.Report(Math.Min(1.0, (double)sent / total));
            }
            progress?.Report(1.0);
        }

        protected override bool TryComputeLength(out long length) {
            if (source.CanSeek) {
                length = source.Length;
                return true;
            }
            length = -1;
            return false;
        }
    }
}
