using Azure;
using Azure.Core.GeoJson;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using System.Globalization;
using System.Text.RegularExpressions;
using FindDoctor.Web.Models;

namespace FindDoctor.Web.Services;

/// <summary>
/// Handles Azure AI Search operations for doctor discovery.
/// Implements hybrid search (keyword + semantic) + vector search + geo-distance ranking.
/// </summary>
public class AzureSearchService
{
    private readonly SearchClient _searchClient;
    private readonly string _indexName;
    private readonly ILogger<AzureSearchService> _logger;

    public AzureSearchService(SearchClient searchClient, IConfiguration config, ILogger<AzureSearchService> logger)
    {
        _searchClient = searchClient;
        _indexName = config["Azure:Search:IndexName"] ?? "doctors";
        _logger = logger;
    }

    public async Task<(double? Latitude, double? Longitude, string? ResolvedLocationLabel)> ResolveCoordinatesFromLocationAsync(string locationText)
    {
        // Explicit user locations (ZIP/city/office text) are converted into a
        // usable geo anchor by querying the provider index itself. This keeps
        // location resolution aligned with available provider geography and avoids
        // external geocoding dependencies for this workflow.
        if (string.IsNullOrWhiteSpace(locationText))
            return (null, null, null);

        try
        {
            var options = new SearchOptions
            {
                Size = 1,
                QueryType = SearchQueryType.Full,
                SearchMode = SearchMode.All
            };

            options.Select.Add("Latitude");
            options.Select.Add("Longitude");
            options.Select.Add("City");
            options.Select.Add("State");
            options.Select.Add("Zip");
            options.Select.Add("OfficeLocationName");

            var zipMatch = Regex.Match(locationText, @"\b\d{5}(?:-\d{4})?\b");
            SearchResults<DoctorDocument> searchResponse;

            if (zipMatch.Success)
            {
                var zip = zipMatch.Value[..5];
                options.Filter = $"Zip eq '{zip}'";
                searchResponse = await _searchClient.SearchAsync<DoctorDocument>("*", options);
            }
            else
            {
                var loc = EscapeLucene(locationText.Trim());
                var query = $"(OfficeLocationName:({loc}) OR City:({loc}) OR State:({loc}) OR Zip:({loc}))";
                searchResponse = await _searchClient.SearchAsync<DoctorDocument>(query, options);
            }

            await foreach (var result in searchResponse.GetResultsAsync())
            {
                var doc = result.Document;
                if (!doc.Latitude.HasValue || !doc.Longitude.HasValue)
                    continue;

                var label = !string.IsNullOrWhiteSpace(doc.City)
                    ? $"{doc.City}, {doc.State} {doc.Zip}".Trim()
                    : doc.OfficeLocationName;

                return (doc.Latitude.Value, doc.Longitude.Value, label);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve explicit location '{LocationText}'", locationText);
        }

        return (null, null, null);
    }

    /// <summary>
    /// Execute hybrid search (keyword + semantic ranking + vector search)
    /// across specialty, conditions, and clinical terms.
    /// Results are ranked by relevance and optionally by distance.
    /// </summary>
    public async Task<List<DoctorSearchResult>> HybridSearchAsync(
        SearchFilters filters,
        string userQuery,
        double? userLat = null,
        double? userLon = null)
    {
        // HybridSearchAsync executes the full search lifecycle:
        // 1) build strict query from extracted filters,
        // 2) execute semantic-enabled search,
        // 3) apply condition relevance guardrails,
        // 4) retry with tolerant matching when strict pass returns nothing,
        // 5) compute ranking metrics,
        // 6) sort according to location availability.
        try
        {
            // Build the search query
            var searchOptions = BuildSearchOptions(filters, userLat, userLon);
            
            // Execute search with the query combining specialty and condition intent
            var query = BuildSearchQuery(filters, useTolerantMatching: false);
            _logger.LogInformation("Azure AI Search input query: {Query}", query);
            
            var result = await _searchClient.SearchAsync<DoctorDocument>(
                query,
                searchOptions);
            
            var results = new List<DoctorSearchResult>();
            
            await foreach (var doc in result.Value.GetResultsAsync())
            {
                if (!IsRelevantConditionMatch(filters, doc.Document))
                {
                    continue;
                }

                var matchedPreferenceLevel = GetMatchedClinicalPreferenceLevel(filters, doc.Document);
                var preferenceBoost = CalculateClinicalPreferenceBoost(matchedPreferenceLevel);
                var baseRankingScore = CalculateRankingScore(
                    doc.Score ?? 0,
                    userLat, userLon,
                    doc.Document.Latitude,
                    doc.Document.Longitude);

                var doctorResult = new DoctorSearchResult
                {
                    Doctor = MapToDoctorModel(doc.Document),
                    RelevanceScore = doc.Score ?? 0,
                    ClinicalPreferenceLevel = matchedPreferenceLevel,
                    DistanceMiles = CalculateDistance(
                        userLat, userLon,
                        doc.Document.Latitude,
                        doc.Document.Longitude),
                    RankingScore = baseRankingScore + preferenceBoost
                };
                
                results.Add(doctorResult);
            }

            // Fallback pass for misspellings and close variants (no synonym map required).
            if (results.Count == 0 && query != "*")
            {
                var tolerantQuery = BuildSearchQuery(filters, useTolerantMatching: true);
                _logger.LogInformation("No results for strict query. Retrying with tolerant Azure AI Search input query: {Query}", tolerantQuery);

                var tolerantResult = await _searchClient.SearchAsync<DoctorDocument>(
                    tolerantQuery,
                    searchOptions);

                await foreach (var doc in tolerantResult.Value.GetResultsAsync())
                {
                    if (!IsRelevantConditionMatch(filters, doc.Document))
                    {
                        continue;
                    }

                    var matchedPreferenceLevel = GetMatchedClinicalPreferenceLevel(filters, doc.Document);
                    var preferenceBoost = CalculateClinicalPreferenceBoost(matchedPreferenceLevel);
                    var baseRankingScore = CalculateRankingScore(
                        doc.Score ?? 0,
                        userLat, userLon,
                        doc.Document.Latitude,
                        doc.Document.Longitude);

                    var doctorResult = new DoctorSearchResult
                    {
                        Doctor = MapToDoctorModel(doc.Document),
                        RelevanceScore = doc.Score ?? 0,
                        ClinicalPreferenceLevel = matchedPreferenceLevel,
                        DistanceMiles = CalculateDistance(
                            userLat, userLon,
                            doc.Document.Latitude,
                            doc.Document.Longitude),
                        RankingScore = baseRankingScore + preferenceBoost
                    };

                    results.Add(doctorResult);
                }
            }
            
            // When coordinates are available, sort by distance ascending (nearest first).
            // When no location, fall back to relevance ranking.
            var hasLocation = userLat.HasValue && userLon.HasValue;

            results = hasLocation
                ? results
                    .OrderByDescending(r => r.ClinicalPreferenceLevel)
                    .ThenBy(r => r.DistanceMiles ?? double.MaxValue)
                    .ThenByDescending(r => r.RankingScore)
                    .Take(5)
                    .ToList()
                : results
                    .OrderByDescending(r => IsUhProviderYes(r.Doctor.UHProvider))
                    .ThenByDescending(r => r.ClinicalPreferenceLevel)
                    .ThenByDescending(r => r.RankingScore)
                    .Take(5)
                    .ToList();
            
            _logger.LogInformation($"Search returned {results.Count} results");
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Search error: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Build search query from filters.
    /// Combines specialty and condition into a hybrid query.
    /// </summary>
    private string BuildSearchQuery(SearchFilters filters, bool useTolerantMatching)
    {
        // Query composition is field-aware: doctor names, specialties, and clinical
        // terms map to different index columns. We join clauses with AND to enforce
        // explicit constraints while still allowing broad textual matching within
        // each domain-specific field set.
        var queryParts = new List<string>();

        // Doctor name search - targets FirstName/LastName
        if (!string.IsNullOrWhiteSpace(filters.DoctorName))
        {
            queryParts.Add(BuildDoctorNameQuery(filters.DoctorName, useTolerantMatching));
        }
        
        // Specialty search - targets Specialty, SpecialtiesCombined fields
        if (!string.IsNullOrEmpty(filters.Specialty))
        {
            var specialtyValue = useTolerantMatching
                ? BuildTolerantFieldQuery(filters.Specialty)
                : EscapeLucene(filters.Specialty);

            queryParts.Add($"(Specialty:({specialtyValue}) OR SpecialtiesCombined:({specialtyValue}))");
        }
        
        // Condition search - targets ClinicalTerms and ClinicalAliases (semantic match)
        if (!string.IsNullOrEmpty(filters.Condition))
        {
            var conditionValue = useTolerantMatching
                ? BuildTolerantFieldQuery(filters.Condition)
                : EscapeLucene(filters.Condition);

            queryParts.Add($"(ClinicalTerms:({conditionValue}) OR ClinicalAliases:({conditionValue}))");
        }
        
        // Gender filter
        if (!string.IsNullOrEmpty(filters.Gender))
        {
            queryParts.Add($"Gender:{filters.Gender}");
        }
        
        // Online scheduling filter
        if (filters.OnlineSchedulingOnly.HasValue && filters.OnlineSchedulingOnly.Value)
        {
            queryParts.Add("OffersOnlineScheduling:true");
        }
        
        // If no filters, return all
        if (queryParts.Count == 0)
        {
            return "*";
        }
        
        return string.Join(" AND ", queryParts);
    }

    private string BuildDoctorNameQuery(string doctorName, bool useTolerantMatching)
    {
        // Name search balances precision and recall: for two-token names it tries
        // first+last pairing first, but also includes loose matching to handle
        // partial user input. Tolerant mode enables fuzzy/prefix behavior for typo
        // resilience.
        var tokens = doctorName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (tokens.Count == 0)
            return "*";

        if (tokens.Count == 1)
        {
            var tokenQuery = useTolerantMatching
                ? BuildTolerantFieldQuery(tokens[0])
                : EscapeLucene(tokens[0]);

            return $"(FirstName:({tokenQuery}) OR LastName:({tokenQuery}))";
        }

        var firstToken = useTolerantMatching ? BuildTolerantFieldQuery(tokens[0]) : EscapeLucene(tokens[0]);
        var lastToken = useTolerantMatching ? BuildTolerantFieldQuery(tokens[^1]) : EscapeLucene(tokens[^1]);

        // Support both exact first+last and loose token match across both name fields.
        return $"((FirstName:({firstToken}) AND LastName:({lastToken})) OR (FirstName:({firstToken}) OR LastName:({lastToken})))";
    }

    private static string BuildTolerantFieldQuery(string input)
    {
        // Tolerant matching expands each token into fuzzy, prefix, and stem-prefix
        // variants. This captures misspellings and morphology changes (plural,
        // suffix forms) without maintaining static synonym dictionaries.
        var tokens = input
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 1)
            .ToList();

        if (tokens.Count == 0)
            return EscapeLucene(input);

        // For each token, combine fuzzy + prefix + simple stem prefix matching.
        // Example: dermatologist -> (dermatologist~2 OR dermatologist* OR dermatolog*)
        var expanded = tokens.Select(token =>
        {
            var escaped = EscapeLucene(token);
            var stem = EscapeLucene(StemToken(token));
            return $"({escaped}~2 OR {escaped}* OR {stem}*)";
        });

        return string.Join(" AND ", expanded);
    }

    private static string StemToken(string token)
    {
        // Lightweight stemming reduces common suffix noise so tolerant queries can
        // match medically adjacent word forms while staying explainable.
        var t = token.ToLowerInvariant();

        if (t.Length > 5 && t.EndsWith("ists")) return t[..^4];
        if (t.Length > 4 && t.EndsWith("ist")) return t[..^3];
        if (t.Length > 4 && t.EndsWith("ies")) return t[..^3] + "y";
        if (t.Length > 4 && t.EndsWith("ing")) return t[..^3];
        if (t.Length > 3 && t.EndsWith("ed")) return t[..^2];
        if (t.Length > 3 && t.EndsWith("es")) return t[..^2];
        if (t.Length > 3 && t.EndsWith("s")) return t[..^1];
        if (t.Length > 4 && t.EndsWith("y")) return t[..^1];

        return t;
    }

    private static string EscapeLucene(string input)
    {
        // Escaping protects query syntax and prevents user text from accidentally
        // breaking Lucene expressions (for example special characters in names or
        // location strings).
        var escaped = input;
        var specialChars = new[] { "\\", "+", "-", "&&", "||", "!", "(", ")", "{", "}", "[", "]", "^", "\"", "~", "*", "?", ":", "/" };
        foreach (var c in specialChars)
        {
            escaped = escaped.Replace(c, "\\" + c);
        }
        return escaped;
    }

    /// <summary>
    /// Configure search options for hybrid search.
    /// Enables semantic ranking and geo-distance boosting.
    /// </summary>
    private SearchOptions BuildSearchOptions(
        SearchFilters filters,
        double? userLat,
        double? userLon)
    {
        // Search options are configured for high-recall retrieval plus semantic
        // reranking. We pull a wider candidate set (Size=50), project only required
        // fields, and use Full query mode so composed Lucene clauses are honored.
        // Semantic configuration "default" is defined in index creation service.
        var options = new SearchOptions
        {
            Size = 50, // Retrieve more for ranking
            IncludeTotalCount = true,
            QueryType = SearchQueryType.Full,
            SearchMode = SearchMode.All, // AND logic for filters
        };
        
        // Enable semantic ranking
        options.SemanticSearch = new SemanticSearchOptions
        {
            SemanticConfigurationName = "default",
        };
        
        // Select fields to return
        options.Select.Add("DoctorId");
        options.Select.Add("FirstName");
        options.Select.Add("LastName");
        options.Select.Add("Specialty");
        options.Select.Add("ClinicalTerms");
        options.Select.Add("ClinicalAliases");
        options.Select.Add("ClinicalPreferenceMap");
        options.Select.Add("Gender");
        options.Select.Add("Languages");
        options.Select.Add("UHProvider");
        options.Select.Add("OfficeLocationName");
        options.Select.Add("City");
        options.Select.Add("State");
        options.Select.Add("Zip");
        options.Select.Add("Phone");
        options.Select.Add("OffersOnlineScheduling");
        options.Select.Add("Latitude");
        options.Select.Add("Longitude");
        options.Select.Add("GeoLocation");

        if (userLat.HasValue && userLon.HasValue)
        {
            var radiusMiles = filters.RadiusMiles.GetValueOrDefault(50);
            var radiusKm = radiusMiles * 1.60934;
            var lon = userLon.Value.ToString(CultureInfo.InvariantCulture);
            var lat = userLat.Value.ToString(CultureInfo.InvariantCulture);
            var km = radiusKm.ToString("0.###", CultureInfo.InvariantCulture);

            // Pre-filter candidate set on the server for better precision and performance.
            options.Filter = $"geo.distance(GeoLocation, geography'POINT({lon} {lat})') le {km}";
        }
        
        return options;
    }

    private static bool IsRelevantConditionMatch(SearchFilters filters, DoctorDocument doc)
    {
        // This post-filter protects condition-driven searches from loosely related
        // specialty-only hits. When condition is present (without explicit
        // specialty), we require phrase/token evidence in clinical terms/aliases.
        if (string.IsNullOrWhiteSpace(filters.Condition))
            return true;

        // If user asked for explicit specialty, allow specialty-based retrieval as-is.
        if (!string.IsNullOrWhiteSpace(filters.Specialty))
            return true;

        var condition = filters.Condition.Trim().ToLowerInvariant();
        var haystack = $"{doc.ClinicalTerms} {doc.ClinicalAliases}".ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(haystack))
            return false;

        // Exact phrase match is strongest.
        if (haystack.Contains(condition))
            return true;

        // For multi-token conditions, require all tokens as whole words.
        var tokens = condition
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .Distinct()
            .ToList();

        if (tokens.Count == 0)
            return true;

        return tokens.All(token => Regex.IsMatch(haystack, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase));
    }

    private static bool HasExplicitCriteria(SearchFilters filters)
    {
        // Explicit criteria indicates the user supplied concrete constraints beyond
        // a generic prompt; useful for ranking branch decisions and future tuning.
        return !string.IsNullOrWhiteSpace(filters.DoctorName)
            || !string.IsNullOrWhiteSpace(filters.Specialty)
            || !string.IsNullOrWhiteSpace(filters.Condition)
            || !string.IsNullOrWhiteSpace(filters.Gender)
            || (filters.Languages?.Count > 0)
            || (filters.OnlineSchedulingOnly ?? false);
    }

    private static bool IsUhProviderYes(string? value)
    {
        // Centralized UH provider normalization keeps sorting/filtering consistent
        // across mixed casing values from source datasets.
        return string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetMatchedClinicalPreferenceLevel(SearchFilters filters, DoctorDocument doc)
    {
        if (string.IsNullOrWhiteSpace(filters.Condition) || string.IsNullOrWhiteSpace(doc.ClinicalPreferenceMap))
            return 0;

        var map = ParseClinicalPreferenceMap(doc.ClinicalPreferenceMap);
        if (map.Count == 0)
            return 0;

        var condition = filters.Condition.Trim().ToLowerInvariant();
        var maxPreference = 0;

        foreach (var entry in map)
        {
            if (condition.Contains(entry.Key, StringComparison.OrdinalIgnoreCase)
                || entry.Key.Contains(condition, StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(condition, $@"\b{Regex.Escape(entry.Key)}\b", RegexOptions.IgnoreCase))
            {
                maxPreference = Math.Max(maxPreference, entry.Value);
            }
        }

        if (maxPreference > 0)
            return maxPreference;

        var tokens = condition
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            if (map.TryGetValue(token, out var preference))
                maxPreference = Math.Max(maxPreference, preference);
        }

        return maxPreference;
    }

    private static Dictionary<string, int> ParseClinicalPreferenceMap(string serializedMap)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var entries = serializedMap.Split('|', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries)
        {
            var separatorIndex = entry.LastIndexOf(':');
            if (separatorIndex <= 0 || separatorIndex == entry.Length - 1)
                continue;

            var key = entry[..separatorIndex].Trim();
            var value = entry[(separatorIndex + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!int.TryParse(value, out var parsed))
                parsed = 0;

            result[key] = Math.Max(result.GetValueOrDefault(key), parsed);
        }

        return result;
    }

    private static double CalculateClinicalPreferenceBoost(int preferenceLevel)
    {
        if (preferenceLevel <= 0)
            return 0;

        // Keep preference impactful but bounded so existing relevance/distance logic still applies.
        return Math.Min(preferenceLevel, 10) / 10.0;
    }

    /// <summary>
    /// Calculate distance in miles between two coordinates.
    /// Returns null if either coordinate is missing.
    /// </summary>
    private double? CalculateDistance(double? lat1, double? lon1, double? lat2, double? lon2)
    {
        // Haversine distance in miles. Null-safe behavior is intentional so search
        // can continue even when coordinate data is incomplete.
        if (!lat1.HasValue || !lon1.HasValue || !lat2.HasValue || !lon2.HasValue)
            return null;
        
        const double earthRadiusMiles = 3959;
        
        var dLat = ToRadian(lat2.Value - lat1.Value);
        var dLon = ToRadian(lon2.Value - lon1.Value);
        
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadian(lat1.Value)) * Math.Cos(ToRadian(lat2.Value)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusMiles * c;
    }

    private double ToRadian(double degree) => degree * Math.PI / 180;

    /// <summary>
    /// Calculate combined ranking score:
    /// - 70% relevance (semantic + keyword match)
    /// - 30% distance (closer is better)
    /// </summary>
    private double CalculateRankingScore(
        double relevanceScore,
        double? userLat, double? userLon,
        double? docLat, double? docLon)
    {
        // Ranking score blends semantic relevance with geo proximity. Relevance is
        // normalized from Azure score scale and distance contribution is capped by
        // a 50-mile envelope. Even when final sorting is distance-first, this score
        // remains a useful tie-breaker and diagnostic signal.
        // Normalize relevance score (typically 0-4 in Azure Search)
        var normalizedRelevance = Math.Min(relevanceScore / 4.0, 1.0);
        
        // If no location, return relevance only
        if (!userLat.HasValue || !userLon.HasValue)
            return normalizedRelevance;
        
        // Calculate distance score (0-1, inverted: closer = higher score)
        var distance = CalculateDistance(userLat, userLon, docLat, docLon);
        if (!distance.HasValue)
            return normalizedRelevance;
        
        // Assume 50 miles is max reasonable distance (beyond 50 miles, score approaches 0)
        var distanceScore = Math.Max(0, 1 - (distance.Value / 50.0));
        
        // Combined score: 70% relevance, 30% distance
        return (normalizedRelevance * 0.7) + (distanceScore * 0.3);
    }

    /// <summary>
    /// Map Azure Search document to Doctor model
    /// </summary>
    private Doctor MapToDoctorModel(DoctorDocument doc)
    {
        // Map index document model to API response model and normalize fields that
        // must remain consistent for UI logic (for example UHProvider casing).
        return new Doctor
        {
            DoctorId = doc.DoctorId,
            FirstName = doc.FirstName,
            LastName = doc.LastName,
            Specialty = doc.Specialty,
            ProviderType = doc.ProviderType,
            UHProvider = NormalizeUhProvider(doc.UHProvider),
            Gender = doc.Gender,
            Languages = doc.Languages?.ToList() ?? new(),
            OfficeLocationName = doc.OfficeLocationName,
            City = doc.City,
            State = doc.State,
            Zip = doc.Zip,
            Phone = doc.Phone,
            OffersOnlineScheduling = doc.OffersOnlineScheduling
        };
    }

    private static string NormalizeUhProvider(string? value)
    {
        // Normalize legacy source variants to one canonical value for deterministic
        // downstream sorting and display.
        return string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ? "yes" : "No";
    }
}

/// <summary>
/// Maps to Azure AI Search document schema
/// </summary>
public class DoctorDocument
{
    public string DoctorId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string SpecialtiesCombined { get; set; } = string.Empty;
    public string ClinicalTerms { get; set; } = string.Empty;
    public string ClinicalAliases { get; set; } = string.Empty;
    public string ClinicalPreferenceMap { get; set; } = string.Empty;
    public string ProviderType { get; set; } = string.Empty;
    public string UHProvider { get; set; } = "No";
    public string Gender { get; set; } = string.Empty;
    public IEnumerable<string> Languages { get; set; } = new List<string>();
    public string OfficeLocationName { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Zip { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public bool OffersOnlineScheduling { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public GeoPoint? GeoLocation { get; set; }
}
