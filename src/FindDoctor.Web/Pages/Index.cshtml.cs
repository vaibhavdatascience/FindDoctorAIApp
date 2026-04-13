using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FindDoctor.Web.Pages;

/// <summary>
/// Index page model for the chat UI.
/// Most logic is handled by the /api/chat endpoint.
/// </summary>
public class IndexModel : PageModel
{
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(ILogger<IndexModel> logger)
    {
        _logger = logger;
    }

    public void OnGet()
    {
        _logger.LogInformation("Chat page loaded");
    }
}
