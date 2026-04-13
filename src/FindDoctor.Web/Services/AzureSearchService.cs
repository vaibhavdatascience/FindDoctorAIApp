using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
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
        try
        {
            // Build the search query
            var searchOptions = BuildSearchOptions(filters, userLat, userLon);
            
            // Execute search with the query combining specialty and condition intent
            var query = BuildSearchQuery(filters, useTolerantMatching: false);
            _logger.LogInformation($"Executing search: {query}");
            
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

                var doctorResult = new DoctorSearchResult
                {
                    Doctor = MapToDoctorModel(doc.Document),
                    RelevanceScore = doc.Score ?? 0,
                    DistanceMiles = CalculateDistance(
                        userLat, userLon,
                        doc.Document.Latitude,
                        doc.Document.Longitude),
                    RankingScore = CalculateRankingScore(
                        doc.Score ?? 0,
                        userLat, userLon,
                        doc.Document.Latitude,
                        doc.Document.Longitude)
                };
                
                results.Add(doctorResult);
            }

            // Fallback pass for misspellings and close variants (no synonym map required).
            if (results.Count == 0 && query != "*")
            {
                var tolerantQuery = BuildSearchQuery(filters, useTolerantMatching: true);
                _logger.LogInformation($"No results for strict query. Retrying with tolerant query: {tolerantQuery}");

                var tolerantResult = await _searchClient.SearchAsync<DoctorDocument>(
                    tolerantQuery,
                    searchOptions);

                await foreach (var doc in tolerantResult.Value.GetResultsAsync())
                {
                    if (!IsRelevantConditionMatch(filters, doc.Document))
                    {
                        continue;
                    }

                    var doctorResult = new DoctorSearchResult
                    {
                        Doctor = MapToDoctorModel(doc.Document),
                        RelevanceScore = doc.Score ?? 0,
                        DistanceMiles = CalculateDistance(
                            userLat, userLon,
                            doc.Document.Latitude,
                            doc.Document.Longitude),
                        RankingScore = CalculateRankingScore(
                            doc.Score ?? 0,
                            userLat, userLon,
                            doc.Document.Latitude,
                            doc.Document.Longitude)
                    };

                    results.Add(doctorResult);
                }
            }
            
            // Sort by ranking score (combined relevance + distance)
            results = results
                .OrderByDescending(r => r.RankingScore)
                .Take(10) // Top 10 results
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
        options.Select.Add("Gender");
        options.Select.Add("Languages");
        options.Select.Add("OfficeLocationName");
        options.Select.Add("City");
        options.Select.Add("State");
        options.Select.Add("Zip");
        options.Select.Add("Phone");
        options.Select.Add("OffersOnlineScheduling");
        options.Select.Add("Latitude");
        options.Select.Add("Longitude");
        
        return options;
    }

    private static bool IsRelevantConditionMatch(SearchFilters filters, DoctorDocument doc)
    {
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

    /// <summary>
    /// Calculate distance in miles between two coordinates.
    /// Returns null if either coordinate is missing.
    /// </summary>
    private double? CalculateDistance(double? lat1, double? lon1, double? lat2, double? lon2)
    {
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
        return new Doctor
        {
            DoctorId = doc.DoctorId,
            FirstName = doc.FirstName,
            LastName = doc.LastName,
            Specialty = doc.Specialty,
            ProviderType = doc.ProviderType,
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
    public string ProviderType { get; set; } = string.Empty;
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
}
