using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// Narrow ownership boundary used by the unified capture coordinator. Keeping the
    /// decoder behind this interface makes it possible to prove that an active shared
    /// preview is not accompanied by a second RTSP connection.
    /// </summary>
    internal interface IRtspFrameCaptureService : IDisposable
    {
        bool IsInitializedFor(string rtspUrl);

        Task<bool> InitializeAsync(
            string rtspUrl,
            CancellationToken cancellationToken = default);

        Task<Bitmap?> CaptureFrameAsync(
            CancellationToken cancellationToken = default);

        Task<bool> SaveFrameAsync(
            Bitmap frame,
            string filePath,
            CancellationToken cancellationToken = default);

        void Reset();
    }
}
