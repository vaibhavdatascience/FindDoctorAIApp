namespace FindDoctor.Web.Models;

/// <summary>
/// Represents a healthcare provider/doctor
/// </summary>
public class Doctor
{
    public string DoctorId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string ProviderType { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public List<string> Languages { get; set; } = new();
    public string OfficeLocationName { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Zip { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public bool OffersOnlineScheduling { get; set; }
    
    /// <summary>
    /// Formatted full name for display
    /// </summary>
    public string FullName => $"Dr. {FirstName} {LastName}";
    
    /// <summary>
    /// Formatted address for display
    /// </summary>
    public string Address => $"{City}, {State} {Zip}";
}

/// <summary>
/// Search filter extracted from natural language query
/// </summary>
public partial class SearchFilters
{
    public string? DoctorName { get; set; }
    public string? Specialty { get; set; }
    public string? Condition { get; set; }
    public string? Location { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? RadiusMiles { get; set; }
    public string? Gender { get; set; }
    public List<string> Languages { get; set; } = new();
    public bool? OnlineSchedulingOnly { get; set; }
    public bool SearchIsAmbiguous { get; set; }
}

/// <summary>
/// Ranked doctor search result
/// </summary>
public class DoctorSearchResult
{
    public Doctor Doctor { get; set; } = new();
    
    /// <summary>
    /// Relevance score (0-1, higher is better)
    /// </summary>
    public double RelevanceScore { get; set; }
    
    /// <summary>
    /// Distance from user location in miles (null if not filtered by location)
    /// </summary>
    public double? DistanceMiles { get; set; }
    
    /// <summary>
    /// Combined ranking score (relevance + distance)
    /// </summary>
    public double RankingScore { get; set; }
}

/// <summary>
/// Chat message from user or assistant
/// </summary>
public class ChatMessage
{
    public string Role { get; set; } = "user"; // "user" or "assistant"
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Search request from chat endpoint
/// </summary>
public class ChatSearchRequest
{
    public string Query { get; set; } = string.Empty;
    public double? UserLatitude { get; set; }
    public double? UserLongitude { get; set; }
}

/// <summary>
/// Structured search results for chat
/// </summary>
public class ChatSearchResponse
{
    public string UserQueryResponse { get; set; } = string.Empty;
    public List<DoctorSearchResult> Results { get; set; } = new();
    public string ClarifyingQuestion { get; set; } = string.Empty;
    public bool RequiresClarification { get; set; }
}
