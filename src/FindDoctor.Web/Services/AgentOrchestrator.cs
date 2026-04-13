using Azure.AI.OpenAI;
using OpenAI.Chat;
using OAIChatMessage = OpenAI.Chat.ChatMessage;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FindDoctor.Web.Models;

namespace FindDoctor.Web.Services;

/// <summary>
/// Orchestrates the conversation flow:
/// 1. Interprets user intent using Azure OpenAI
/// 2. Extracts search filters from natural language
/// 3. Executes search
/// 4. Formats results for chat display
/// 
/// This is the "brain" of the chatbot.
/// </summary>
public class AgentOrchestrator
{
    private readonly AzureOpenAIClient _openAiClient;
    private readonly AzureSearchService _searchService;
    private readonly IConfiguration _config;
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(
        AzureOpenAIClient openAiClient,
        AzureSearchService searchService,
        IConfiguration config,
        ILogger<AgentOrchestrator> logger)
    {
        _openAiClient = openAiClient;
        _searchService = searchService;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Main chat endpoint: process user query and return results
    /// </summary>
    public async Task<ChatSearchResponse> ProcessUserQueryAsync(
        string userQuery,
        double? userLatitude = null,
        double? userLongitude = null)
    {
        try
        {
            _logger.LogInformation($"Processing query: {userQuery}");
            
            // Step 1: Extract search filters from natural language
            SearchFilters filters;
            try
            {
                filters = await ExtractSearchFiltersAsync(userQuery);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenAI extraction unavailable; using local fallback filter parsing.");
                filters = BuildFallbackFilters(userQuery);
            }

            filters = NormalizeFilters(userQuery, filters);

            _logger.LogInformation($"Extracted filters - DoctorName: {filters.DoctorName}, Specialty: {filters.Specialty}, Condition: {filters.Condition}, Location: {filters.Location}");
            
            // Step 2: Check if we need clarification
            if (filters.SearchIsAmbiguous)
            {
                return new ChatSearchResponse
                {
                    RequiresClarification = true,
                    ClarifyingQuestion = BuildClarifyingQuestion(filters),
                    UserQueryResponse = $"I'd like to help you find a doctor. {BuildClarifyingQuestion(filters)}"
                };
            }
            
            // Step 3: Execute search
            var results = await _searchService.HybridSearchAsync(
                filters,
                userQuery,
                userLatitude,
                userLongitude);
            
            // Step 4: Format response
            var response = new ChatSearchResponse
            {
                Results = results,
                UserQueryResponse = FormatResultsMessage(results, filters),
                RequiresClarification = false
            };
            
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error processing query: {ex.Message}");
            return new ChatSearchResponse
            {
                RequiresClarification = false,
                UserQueryResponse = "I ran into a temporary issue while searching. Please try again in a moment."
            };
        }
    }

    private SearchFilters BuildFallbackFilters(string userQuery)
    {
        var normalized = Regex.Replace(userQuery.ToLowerInvariant(), "[^a-z0-9\\s]", " ");
        var stopWords = new HashSet<string>
        {
            "find", "me", "a", "an", "the", "doctor", "dr", "near", "with", "for", "please", "my"
        };

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !stopWords.Contains(t))
            .ToList();

        if (tokens.Count == 0)
        {
            return new SearchFilters { SearchIsAmbiguous = true };
        }

        return new SearchFilters
        {
            Specialty = null,
            Condition = string.Join(' ', tokens),
            OnlineSchedulingOnly = normalized.Contains("online"),
            SearchIsAmbiguous = false
        };
    }

    /// <summary>
    /// Use Azure OpenAI to understand user intent and extract filters.
    /// This is semantic understanding - no static synonym maps.
    /// </summary>
    private async Task<SearchFilters> ExtractSearchFiltersAsync(string userQuery)
    {
        var system = @"You are a medical search assistant. Extract search filters from user queries.
Respond ONLY with valid JSON (no markdown, no explanation).

    Critical rules:
    - Do NOT default to Dermatology when the query does not indicate skin-related symptoms.
    - If user asks for disease/condition (for example: brain tumor, seizures, stroke), set condition and leave specialty null unless explicitly stated.
    - Only set specialty when clearly supported by the query text.

JSON schema:
{
    ""doctorName"": ""string or null - doctor's first/last name if user asks for a specific provider"",
    ""specialty"": ""string or null - medical specialty like Dermatology, Cardiology"",
    ""condition"": ""string or null - medical condition like acne, heart disease"",
    ""location"": ""string or null - city, state, zip, or 'near me'"",
    ""gender"": ""string or null - 'male' or 'female'"",
    ""onlineScheduling"": ""boolean or null - true if user wants online scheduling"",
    ""isAmbiguous"": ""boolean - true if the query is too vague to search""
}

Examples:
- ""find Dr. Aziza Wahby"" -> {""doctorName"": ""Aziza Wahby"", ""specialty"": null, ""condition"": null, ""isAmbiguous"": false}
- ""dermatologist near Cleveland"" -> {""specialty"": ""Dermatology"", ""location"": ""Cleveland""}
- ""female skin doctor for acne"" -> {""specialty"": ""Dermatology"", ""condition"": ""acne"", ""gender"": ""female""}
- ""brain tumor doctor near me"" -> {""specialty"": null, ""condition"": ""brain tumor"", ""location"": ""near me"", ""isAmbiguous"": false}
- ""heart doctor with online scheduling"" -> {""specialty"": ""Cardiology"", ""onlineScheduling"": true}
- ""find me a doctor"" -> {""isAmbiguous"": true} (too vague)";

        var deploymentName = _config["Azure:OpenAI:ModelDeploymentName"] ?? "gpt-4";
        var chatClient = _openAiClient.GetChatClient(deploymentName);

        var messages = new OAIChatMessage[]
        {
            OAIChatMessage.CreateSystemMessage(system),
            OAIChatMessage.CreateUserMessage(userQuery)
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0.1f,
            MaxOutputTokenCount = 200,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };

        var response = await chatClient.CompleteChatAsync(messages, options);

        var content = response.Value.Content[0].Text;
        _logger.LogInformation($"OpenAI extraction response: {content}");

        try
        {
            var jsonDoc = JsonDocument.Parse(content);
            var root = jsonDoc.RootElement;

            var filters = new SearchFilters
            {
                DoctorName = root.TryGetProperty("doctorName", out var doctorName) && doctorName.ValueKind != JsonValueKind.Null
                    ? doctorName.GetString()
                    : null,
                Specialty = root.TryGetProperty("specialty", out var spec) && spec.ValueKind != JsonValueKind.Null
                    ? spec.GetString()
                    : null,
                Condition = root.TryGetProperty("condition", out var cond) && cond.ValueKind != JsonValueKind.Null
                    ? cond.GetString()
                    : null,
                Location = root.TryGetProperty("location", out var loc) && loc.ValueKind != JsonValueKind.Null
                    ? loc.GetString()
                    : null,
                Gender = root.TryGetProperty("gender", out var gender) && gender.ValueKind != JsonValueKind.Null
                    ? gender.GetString()
                    : null,
                OnlineSchedulingOnly = root.TryGetProperty("onlineScheduling", out var online) && online.ValueKind != JsonValueKind.Null
                    ? online.GetBoolean()
                    : null,
                SearchIsAmbiguous = root.TryGetProperty("isAmbiguous", out var ambiguous) && ambiguous.GetBoolean()
            };

            return filters;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to parse OpenAI response: {ex.Message}");
            // Return empty filters on parse error
            return new SearchFilters { SearchIsAmbiguous = true };
        }
    }

    private SearchFilters NormalizeFilters(string userQuery, SearchFilters filters)
    {
        if (filters.SearchIsAmbiguous)
            return filters;

        var normalizedQuery = Regex.Replace(userQuery.ToLowerInvariant(), "[^a-z0-9\\s]", " ");
        var queryTokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

        var specialty = filters.Specialty?.Trim();
        var condition = filters.Condition?.Trim();

        // If neither specialty nor condition is set, treat likely-person queries as provider-name search.
        if (string.IsNullOrWhiteSpace(filters.DoctorName) && string.IsNullOrWhiteSpace(specialty) && string.IsNullOrWhiteSpace(condition))
        {
            if (TryExtractLikelyDoctorName(userQuery, out var extractedName))
            {
                filters.DoctorName = extractedName;
                filters.SearchIsAmbiguous = false;
                return filters;
            }
        }

        // If specialty appears to be inferred but not present in user query, trust condition-first search.
        if (!string.IsNullOrEmpty(specialty))
        {
            var specialtyTokens = Regex.Replace(specialty.ToLowerInvariant(), "[^a-z0-9\\s]", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 3)
                .ToList();

            var specialtyMentioned = specialtyTokens.Any(t =>
                queryTokens.Contains(t) ||
                queryTokens.Any(q => TokensLikelyEquivalent(t, q)));

            if (!specialtyMentioned)
            {
                // No explicit specialty in user text. Treat the request as condition-driven.
                filters.Specialty = null;
                if (string.IsNullOrWhiteSpace(condition))
                {
                    var stopWords = new HashSet<string>
                    {
                        "find", "me", "a", "an", "the", "doctor", "doctors", "dr", "near", "with", "for", "please", "my", "specialist"
                    };

                    var inferredCondition = normalizedQuery
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Where(t => !stopWords.Contains(t))
                        .ToList();

                    filters.Condition = inferredCondition.Count > 0
                        ? string.Join(' ', inferredCondition)
                        : null;
                }
            }
        }

        return filters;
    }

    private static bool TryExtractLikelyDoctorName(string userQuery, out string doctorName)
    {
        doctorName = string.Empty;

        var normalized = Regex.Replace(userQuery, "(?i)\\bdr\\.?\\b", " ");
        normalized = Regex.Replace(normalized, "[^a-zA-Z\\s'-]", " ");

        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "find", "me", "a", "an", "the", "doctor", "doctors", "near", "with", "for", "please", "my", "specialist"
        };

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !stopWords.Contains(t))
            .ToList();

        if (tokens.Count < 2)
            return false;

        // Use first two meaningful tokens as a likely "First Last" pattern.
        doctorName = $"{Capitalize(tokens[0])} {Capitalize(tokens[1])}";
        return true;
    }

    private static string Capitalize(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return token;
        if (token.Length == 1) return token.ToUpperInvariant();
        return char.ToUpperInvariant(token[0]) + token[1..].ToLowerInvariant();
    }

    private static bool TokensLikelyEquivalent(string a, string b)
    {
        if (a == b) return true;

        var aNorm = a.ToLowerInvariant();
        var bNorm = b.ToLowerInvariant();

        // Handles pairs like dermatology <-> dermatologist
        var minLen = Math.Min(aNorm.Length, bNorm.Length);
        if (minLen >= 6 && aNorm[..6] == bNorm[..6])
            return true;

        // Handles small token variations/plurals
        if (aNorm.Length >= 4 && bNorm.StartsWith(aNorm[..4])) return true;
        if (bNorm.Length >= 4 && aNorm.StartsWith(bNorm[..4])) return true;

        return false;
    }

    /// <summary>
    /// Build a helpful clarifying question if the search is ambiguous
    /// </summary>
    private string BuildClarifyingQuestion(SearchFilters filters)
    {
        if (string.IsNullOrEmpty(filters.DoctorName) && string.IsNullOrEmpty(filters.Specialty) && string.IsNullOrEmpty(filters.Condition))
        {
            return "What type of doctor or medical specialty are you looking for? (e.g., Dermatologist, Cardiologist, or condition like acne, heart disease)";
        }

        if (string.IsNullOrEmpty(filters.Location))
        {
            return "What location would you prefer? (city, state, or ZIP code)";
        }

        return "Can you provide more details about your search?";
    }

    /// <summary>
    /// Format search results into a natural, conversational response
    /// </summary>
    private string FormatResultsMessage(List<DoctorSearchResult> results, SearchFilters filters)
    {
        if (results.Count == 0)
        {
            return $"I couldn't find doctors matching your criteria. Try adjusting your search or location.";
        }

        var specialty = !string.IsNullOrWhiteSpace(filters.DoctorName)
            ? $"doctor named {filters.DoctorName}"
            : filters.Specialty ?? filters.Condition ?? "available";
        var location = !string.IsNullOrEmpty(filters.Location)
            ? $" near {filters.Location}"
            : "";

        var message = !string.IsNullOrWhiteSpace(filters.DoctorName)
            ? $"Found {results.Count} {specialty}{location}:"
            : $"Found {results.Count} {specialty} doctor(s){location}:";
        
        // Add top 3 results as brief summary
        foreach (var result in results.Take(3))
        {
            var distance = result.DistanceMiles.HasValue
                ? $" ({result.DistanceMiles:F1} mi away)"
                : "";
            message += $"\n  - Dr. {result.Doctor.FirstName} {result.Doctor.LastName} - {result.Doctor.OfficeLocationName}{distance}";
        }

        if (results.Count > 3)
        {
            message += $"\n  ... and {results.Count - 3} more.";
        }

        return message;
    }
}

