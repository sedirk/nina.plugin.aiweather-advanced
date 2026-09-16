using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIWeather.Services
{
    public enum GeminiRequestProfile
    {
        /// <summary>Gemini 1.x/2.x: sampling temperature and the thinking budget switched off.</summary>
        Gemini2 = 0,

        /// <summary>Gemini 3.x and the -latest alias: output budget and JSON output only.</summary>
        Lean = 1,

        /// <summary>Last resort for any model: the output budget alone.</summary>
        Bare = 2,
    }


    // Upstream request profiles, shared by every Gemini pool entry. ADVANCED learns a
    // simpler profile on 400 for the next pool cycle/check, retaining one call per visit.
    public static class GeminiRequestPolicy
    {
        public const int DefaultMaxOutputTokens = 8192;
        public static GeminiRequestProfile StartingProfileFor(string? modelName)
        {
            return !string.IsNullOrEmpty(modelName) && Regex.IsMatch(modelName, @"gemini-[12]\.", RegexOptions.IgnoreCase)
                ? GeminiRequestProfile.Gemini2
                : GeminiRequestProfile.Lean;
        }

        /// <summary>The next, poorer shape to try; null once the bare request has been refused too.</summary>
        public static GeminiRequestProfile? NextProfile(GeminiRequestProfile profile)
        {
            return profile switch
            {
                GeminiRequestProfile.Gemini2 => GeminiRequestProfile.Lean,
                GeminiRequestProfile.Lean => GeminiRequestProfile.Bare,
                _ => null,
            };
        }

        /// <summary>
        /// Whether a failure is about the request's shape. Google answers every unsupported
        /// parameter with a bare 400 INVALID_ARGUMENT that does not name the parameter, so
        /// the status is all there is to go on - and it is enough: a key, quota or server
        /// problem never comes back as 400.
        /// </summary>
        public static bool IsRequestRejected(int status) => status == 400;

        /// <summary>
        /// The budget to ask for, given what the model itself reports as its output limit.
        /// Asking for more than the model allows is one more way to get a 400.
        /// </summary>
        public static int ClampBudget(int? modelOutputTokenLimit)
        {
            return modelOutputTokenLimit is > 0
                ? Math.Min(DefaultMaxOutputTokens, modelOutputTokenLimit.Value)
                : DefaultMaxOutputTokens;
        }

        /// <summary>Read outputTokenLimit from a /models/{name} metadata answer, if present.</summary>
        public static int? ParseOutputTokenLimit(string? metadataJson)
        {
            if (string.IsNullOrWhiteSpace(metadataJson)) { return null; }

            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("outputTokenLimit", out var limit)
                    && limit.ValueKind == JsonValueKind.Number
                    && limit.TryGetInt32(out var value))
                {
                    return value;
                }
            }
            catch (JsonException)
            {
                // Not metadata; the default budget applies.
            }

            return null;
        }

        /// <summary>
        /// Build the request for one image in the given shape. The budget always travels;
        /// JSON output is requested where the shape allows it, so the model does not wrap
        /// the object in prose; temperature and the thinking budget only in the 1.x/2.x shape.
        /// </summary>
        public static string BuildRequestBody(string promptText, string base64Image, GeminiRequestProfile profile, int maxOutputTokens)
        {
            var generationConfig = new Dictionary<string, object>
            {
                ["maxOutputTokens"] = maxOutputTokens,
            };

            if (profile != GeminiRequestProfile.Bare)
            {
                generationConfig["responseMimeType"] = "application/json";
            }

            if (profile == GeminiRequestProfile.Gemini2)
            {
                generationConfig["temperature"] = 0.1;
                generationConfig["thinkingConfig"] = new Dictionary<string, object> { ["thinkingBudget"] = 0 };
            }

            var payload = new
            {
                contents = new object[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = promptText },
                            new
                            {
                                inlineData = new
                                {
                                    mimeType = "image/jpeg",
                                    data = base64Image
                                }
                            }
                        }
                    }
                },
                generationConfig
            };

            return JsonSerializer.Serialize(payload);
        }

        /// <summary>
        /// True when the model stopped because it hit the output limit, which is the one
        /// failure that produces a half-written answer.
        /// </summary>
        public static bool WasCutShort(JsonElement root)
        {
            if (!root.TryGetProperty("candidates", out var candidates)
                || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() == 0)
            {
                return false;
            }

            return candidates[0].TryGetProperty("finishReason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                && string.Equals(reason.GetString(), "MAX_TOKENS", StringComparison.OrdinalIgnoreCase);
        }

    }
}
