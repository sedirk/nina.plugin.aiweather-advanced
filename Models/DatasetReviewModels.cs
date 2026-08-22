using System;

namespace AIWeather.Models
{
    public static class DatasetReviewStatuses
    {
        public const string Unreviewed = "unreviewed";
        public const string Accepted = "accepted";
        public const string Corrected = "corrected";
        public const string Rejected = "rejected";

        public static bool IsValid(string? status)
        {
            return status is Unreviewed or Accepted or Corrected or Rejected;
        }
    }

    /// <summary>
    /// A human correction intentionally excludes the runtime IsSafe decision. Safety is a
    /// policy output that also depends on thresholds, freshness and external sensors; it is
    /// not a pure visual label.
    /// </summary>
    public sealed class DatasetHumanLabel
    {
        public WeatherCondition Condition { get; init; }
        public double CloudCoverage { get; init; }
        public bool RainDetected { get; init; }
        public bool FogDetected { get; init; }
    }

    /// <summary>
    /// Mutable review information is stored as a sidecar. The original teacher label stays
    /// immutable for audit and can always be compared with the human correction.
    /// </summary>
    public sealed class DatasetReviewOverlay
    {
        public int SchemaVersion { get; init; } = 1;
        public string SampleId { get; init; } = string.Empty;
        public int Revision { get; init; }
        public string Status { get; init; } = DatasetReviewStatuses.Unreviewed;
        public DateTime ReviewedUtc { get; init; }
        public string Reviewer { get; init; } = "local-user";
        public string OriginalLabelSha256 { get; init; } = string.Empty;
        public DatasetHumanLabel? HumanLabel { get; init; }
        public string? Notes { get; init; }
    }

    public sealed class DatasetReviewAuditEvent
    {
        public int SchemaVersion { get; init; } = 1;
        public DateTime RecordedUtc { get; init; }
        public DatasetReviewOverlay Review { get; init; } = new DatasetReviewOverlay();
    }

    public sealed class DatasetReviewEntry
    {
        public string SampleId { get; init; } = string.Empty;
        public string LabelFilePath { get; init; } = string.Empty;
        public string? ImageFilePath { get; init; }
        public string? ReviewFilePath { get; set; }
        public string OriginalLabelSha256 { get; init; } = string.Empty;
        public DatasetSampleRecord? Record { get; init; }
        public DatasetReviewOverlay? Review { get; set; }
        public string? LoadError { get; init; }

        public string EffectiveReviewStatus
        {
            get
            {
                if (DatasetReviewStatuses.IsValid(Review?.Status))
                {
                    return Review!.Status;
                }

                var embedded = Record?.Review?.Status;
                return DatasetReviewStatuses.IsValid(embedded)
                    ? embedded!
                    : DatasetReviewStatuses.Unreviewed;
            }
        }
    }
}
