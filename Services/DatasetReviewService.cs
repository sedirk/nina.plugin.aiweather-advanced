using AIWeather.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// Indexes the machine-oriented sharded dataset into a flat collection for humans and
    /// persists review sidecars without rewriting the immutable teacher labels.
    /// </summary>
    public sealed class DatasetReviewService
    {
        private static readonly SemaphoreSlim ReviewWriteGate = new SemaphoreSlim(1, 1);
        private static readonly JsonSerializerOptions PrettyJson = CreateJsonOptions(indented: true);
        private static readonly JsonSerializerOptions CompactJson = CreateJsonOptions(indented: false);
        private static readonly string[] AllowedLabelRoots = { "labels", Path.Combine("quarantine", "labels") };
        private static readonly string[] AllowedImageRoots = { "images", Path.Combine("quarantine", "images") };

        private readonly string _rootDirectory;
        private readonly string _rootPrefix;

        public DatasetReviewService(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("Dataset root is required", nameof(rootDirectory));
            }

            _rootDirectory = Path.GetFullPath(rootDirectory);
            _rootPrefix = _rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        }

        public string RootDirectory => _rootDirectory;

        public async Task<IReadOnlyList<DatasetReviewEntry>> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            var entries = new List<DatasetReviewEntry>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var relativeRoot in AllowedLabelRoots)
            {
                var labelRoot = Path.Combine(_rootDirectory, relativeRoot);
                if (!Directory.Exists(labelRoot))
                {
                    continue;
                }

                foreach (var labelPath in Directory.EnumerateFiles(
                             labelRoot,
                             "*.json",
                             SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entries.Add(await LoadEntryAsync(labelPath, seenIds, cancellationToken));
                }
            }

            return entries
                .OrderByDescending(entry => entry.Record?.CapturedUtc ?? DateTime.MinValue)
                .ThenBy(entry => entry.SampleId, StringComparer.Ordinal)
                .ToArray();
        }

        public async Task<DatasetReviewOverlay> SaveReviewAsync(
            DatasetReviewEntry entry,
            string status,
            DatasetHumanLabel? humanLabel,
            string? notes,
            CancellationToken cancellationToken = default)
        {
            if (entry.Record == null || !string.IsNullOrWhiteSpace(entry.LoadError))
            {
                throw new InvalidOperationException("A damaged label cannot be reviewed until it is repaired");
            }

            if (!DatasetReviewStatuses.IsValid(status))
            {
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown review status");
            }

            if (status == DatasetReviewStatuses.Corrected)
            {
                ValidateHumanLabel(humanLabel);
            }
            else
            {
                humanLabel = null;
            }

            ValidateSampleId(entry.SampleId);
            var labelPath = ValidatePathInsideRoot(entry.LabelFilePath);
            if (!File.Exists(labelPath))
            {
                throw new FileNotFoundException("The original label no longer exists", labelPath);
            }

            var originalHash = Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(labelPath, cancellationToken)))
                .ToLowerInvariant();
            var reviewedUtc = DateTime.UtcNow;
            var overlay = new DatasetReviewOverlay
            {
                SampleId = entry.SampleId,
                Revision = Math.Max(0, entry.Review?.Revision ?? 0) + 1,
                Status = status,
                ReviewedUtc = reviewedUtc,
                OriginalLabelSha256 = originalHash,
                HumanLabel = humanLabel,
                Notes = SanitizeNotes(notes)
            };

            var reviewPath = GetReviewPath(entry.SampleId);
            var reviewBytes = JsonSerializer.SerializeToUtf8Bytes(overlay, PrettyJson);

            await ReviewWriteGate.WaitAsync(cancellationToken);
            try
            {
                await AtomicWriteBytesAsync(reviewPath, reviewBytes, cancellationToken);

                var audit = new DatasetReviewAuditEvent
                {
                    RecordedUtc = reviewedUtc,
                    Review = overlay
                };
                var auditPath = Path.Combine(
                    _rootDirectory,
                    "review",
                    $"reviews-{reviewedUtc:yyyy-MM}.jsonl");
                await AppendAuditLineAsync(auditPath, JsonSerializer.Serialize(audit, CompactJson), cancellationToken);
            }
            finally
            {
                ReviewWriteGate.Release();
            }

            entry.Review = overlay;
            entry.ReviewFilePath = reviewPath;
            return overlay;
        }

        public async Task<DatasetSampleDeletionResult> DeleteSampleAsync(
            DatasetReviewEntry entry,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ValidateSampleId(entry.SampleId);

            var labelPath = ValidatePathInsideAllowedRoots(
                entry.LabelFilePath,
                AllowedLabelRoots,
                "label");
            if (!File.Exists(labelPath))
            {
                throw new FileNotFoundException("The sample label no longer exists", labelPath);
            }

            string? imagePath = null;
            if (!string.IsNullOrWhiteSpace(entry.ImageFilePath))
            {
                imagePath = ValidatePathInsideAllowedRoots(
                    entry.ImageFilePath,
                    AllowedImageRoots,
                    "image");
            }

            var reviewPath = ValidatePathInsideAllowedRoots(
                GetReviewPath(entry.SampleId),
                new[] { Path.Combine("review", "labels") },
                "review sidecar");

            await ReviewWriteGate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var retainedSharedImage = imagePath != null
                                          && File.Exists(imagePath)
                                          && await IsImageReferencedByAnotherLabelAsync(
                                              imagePath,
                                              labelPath,
                                              cancellationToken);

                // Removing the label first keeps the human index consistent even if a later
                // image deletion is interrupted. At worst that leaves a harmless orphan image,
                // never a label that points at a missing image.
                var targets = new List<string> { labelPath, reviewPath };
                if (imagePath != null && !retainedSharedImage)
                {
                    targets.Add(imagePath);
                }

                var releasedBytes = 0L;
                var deletedFileCount = 0;
                foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(target))
                    {
                        continue;
                    }

                    releasedBytes += new FileInfo(target).Length;
                    File.Delete(target);
                    deletedFileCount++;
                }

                var deletedUtc = DateTime.UtcNow;
                var audit = new DatasetDeletionAuditEvent
                {
                    DeletedUtc = deletedUtc,
                    SampleId = entry.SampleId,
                    DeletedFileCount = deletedFileCount,
                    ReleasedBytes = releasedBytes,
                    RetainedSharedImage = retainedSharedImage
                };

                try
                {
                    var auditPath = Path.Combine(
                        _rootDirectory,
                        "review",
                        $"deletions-{deletedUtc:yyyy-MM}.jsonl");
                    await AppendAuditLineAsync(
                        auditPath,
                        JsonSerializer.Serialize(audit, CompactJson),
                        CancellationToken.None);
                }
                catch
                {
                    // The sample has already been deleted. A best-effort tombstone must never
                    // turn a successful deletion into a misleading UI failure.
                }

                return new DatasetSampleDeletionResult
                {
                    SampleId = entry.SampleId,
                    DeletedFileCount = deletedFileCount,
                    ReleasedBytes = releasedBytes,
                    RetainedSharedImage = retainedSharedImage
                };
            }
            finally
            {
                ReviewWriteGate.Release();
            }
        }

        private async Task<DatasetReviewEntry> LoadEntryAsync(
            string labelPath,
            ISet<string> seenIds,
            CancellationToken cancellationToken)
        {
            var fallbackId = Path.GetFileNameWithoutExtension(labelPath);
            try
            {
                var labelBytes = await File.ReadAllBytesAsync(labelPath, cancellationToken);
                var record = JsonSerializer.Deserialize<DatasetSampleRecord>(labelBytes, PrettyJson)
                             ?? throw new InvalidDataException("Label JSON deserialized to null");
                ValidateSampleId(record.SampleId);

                if (!seenIds.Add(record.SampleId))
                {
                    throw new InvalidDataException($"Duplicate sample id: {record.SampleId}");
                }

                var imagePath = ResolveDatasetRelativePath(record.Image.RelativePath);
                var originalHash = Convert.ToHexString(SHA256.HashData(labelBytes)).ToLowerInvariant();
                var reviewPath = GetReviewPath(record.SampleId);
                DatasetReviewOverlay? overlay = null;
                if (File.Exists(reviewPath))
                {
                    overlay = JsonSerializer.Deserialize<DatasetReviewOverlay>(
                        await File.ReadAllBytesAsync(reviewPath, cancellationToken),
                        PrettyJson);
                    if (overlay == null || !string.Equals(overlay.SampleId, record.SampleId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Review sidecar does not match its sample id");
                    }
                    if (!string.IsNullOrWhiteSpace(overlay.OriginalLabelSha256)
                        && !string.Equals(
                            overlay.OriginalLabelSha256,
                            originalHash,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Original teacher label changed after this review was recorded");
                    }
                }

                return new DatasetReviewEntry
                {
                    SampleId = record.SampleId,
                    LabelFilePath = labelPath,
                    ImageFilePath = imagePath,
                    ReviewFilePath = File.Exists(reviewPath) ? reviewPath : null,
                    OriginalLabelSha256 = originalHash,
                    Record = record,
                    Review = overlay
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new DatasetReviewEntry
                {
                    SampleId = fallbackId,
                    LabelFilePath = labelPath,
                    LoadError = LogRedactor.RedactSensitiveText(ex.Message)
                };
            }
        }

        private string ResolveDatasetRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new InvalidDataException("Image path is empty");
            }

            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized))
            {
                throw new InvalidDataException("Image path must be relative to the dataset root");
            }

            var fullPath = ValidatePathInsideRoot(Path.Combine(_rootDirectory, normalized));
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Referenced dataset image is missing", fullPath);
            }
            return fullPath;
        }

        private string GetReviewPath(string sampleId)
        {
            ValidateSampleId(sampleId);
            return ValidatePathInsideRoot(Path.Combine(
                _rootDirectory,
                "review",
                "labels",
                sampleId + ".review.json"));
        }

        private async Task<bool> IsImageReferencedByAnotherLabelAsync(
            string imagePath,
            string currentLabelPath,
            CancellationToken cancellationToken)
        {
            var targetImagePath = Path.GetFullPath(imagePath);
            var currentPath = Path.GetFullPath(currentLabelPath);
            var foundUnreadableLabel = false;

            foreach (var relativeRoot in AllowedLabelRoots)
            {
                var labelRoot = Path.Combine(_rootDirectory, relativeRoot);
                if (!Directory.Exists(labelRoot))
                {
                    continue;
                }

                foreach (var candidateLabelPath in Directory.EnumerateFiles(
                             labelRoot,
                             "*.json",
                             SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(
                            Path.GetFullPath(candidateLabelPath),
                            currentPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var record = JsonSerializer.Deserialize<DatasetSampleRecord>(
                            await File.ReadAllBytesAsync(candidateLabelPath, cancellationToken),
                            PrettyJson);
                        if (record == null || string.IsNullOrWhiteSpace(record.Image.RelativePath))
                        {
                            foundUnreadableLabel = true;
                            continue;
                        }

                        var normalized = record.Image.RelativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar);
                        if (Path.IsPathRooted(normalized))
                        {
                            foundUnreadableLabel = true;
                            continue;
                        }

                        var candidateImagePath = ValidatePathInsideRoot(
                            Path.Combine(_rootDirectory, normalized));
                        if (string.Equals(
                                candidateImagePath,
                                targetImagePath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Be conservative: an unreadable label might still reference the same
                        // image, so keep the image while deleting the selected label/sidecar.
                        foundUnreadableLabel = true;
                    }
                }
            }

            return foundUnreadableLabel;
        }

        private string ValidatePathInsideAllowedRoots(
            string path,
            IEnumerable<string> allowedRelativeRoots,
            string description)
        {
            var fullPath = ValidatePathInsideRoot(path);
            foreach (var relativeRoot in allowedRelativeRoots)
            {
                var allowedPrefix = Path.GetFullPath(Path.Combine(_rootDirectory, relativeRoot))
                                        .TrimEnd(
                                            Path.DirectorySeparatorChar,
                                            Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return fullPath;
                }
            }

            throw new InvalidDataException(
                $"Dataset {description} path is outside its allowed subtree");
        }

        private string ValidatePathInsideRoot(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Dataset path escaped the configured root");
            }
            return fullPath;
        }

        private static void ValidateSampleId(string? sampleId)
        {
            if (string.IsNullOrWhiteSpace(sampleId)
                || sampleId.Length > 160
                || sampleId.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
            {
                throw new InvalidDataException("Sample id contains unsupported characters");
            }
        }

        private static void ValidateHumanLabel(DatasetHumanLabel? label)
        {
            if (label == null)
            {
                throw new InvalidDataException("A corrected review requires a human label");
            }

            if (label.Condition == WeatherCondition.Unknown
                || !Enum.IsDefined(typeof(WeatherCondition), label.Condition))
            {
                throw new InvalidDataException("Choose a concrete weather condition");
            }

            if (!double.IsFinite(label.CloudCoverage)
                || label.CloudCoverage < 0
                || label.CloudCoverage > 100)
            {
                throw new InvalidDataException("Human cloud coverage must be between 0 and 100");
            }

            if (label.RainDetected && label.Condition != WeatherCondition.Rainy)
            {
                throw new InvalidDataException("Rain detected requires the Rainy condition");
            }

            if (label.FogDetected && label.Condition != WeatherCondition.Foggy)
            {
                throw new InvalidDataException("Fog detected requires the Foggy condition");
            }
        }

        private static string? SanitizeNotes(string? notes)
        {
            var sanitized = LogRedactor.RedactSensitiveText(notes)?.Trim();
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return null;
            }
            return sanitized.Length <= 4000 ? sanitized : sanitized.Substring(0, 4000);
        }

        private static async Task AtomicWriteBytesAsync(
            string path,
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(path)
                            ?? throw new InvalidOperationException("Review file has no parent directory");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                    // A stale temp file is recoverable and must not hide a successful review.
                }
            }
        }

        private static async Task AppendAuditLineAsync(
            string path,
            string line,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }

        private static JsonSerializerOptions CreateJsonOptions(bool indented)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = indented,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
