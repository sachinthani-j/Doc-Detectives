using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace DocDetectives.Web.Services;

/// <summary>
/// Remembers the pages visited in this browser tab (one per circuit), so a back button knows whether the browser's
/// own back step stays inside the app and where it leads. Browser back/forward is followed too.
/// </summary>
public sealed class NavigationHistory : IDisposable
{
    private readonly NavigationManager _nav;
    private readonly List<string> _stack = [];

    public NavigationHistory(NavigationManager nav)
    {
        _nav = nav;
        _stack.Add(Normalize(nav.Uri));
        _nav.LocationChanged += OnLocationChanged;
    }

    /// <summary>Raised when the visited pages change.</summary>
    public event Action? Changed;

    /// <summary>True when the browser's back step returns to an earlier page of this app.</summary>
    public bool CanGoBack => _stack.Count > 1;

    /// <summary>The page the browser's back step returns to (relative to the app base), or null.</summary>
    public string? PreviousUrl => CanGoBack ? _stack[^2] : null;

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var url = Normalize(e.Location);
        if (url == _stack[^1]) return;

        // Arriving at the page before the current one is a back step; anything else is a new step forward.
        if (_stack.Count > 1 && url == _stack[^2])
        {
            _stack.RemoveAt(_stack.Count - 1);
        }
        else
        {
            _stack.Add(url);
        }

        Changed?.Invoke();
    }

    private string Normalize(string uri) => _nav.ToBaseRelativePath(_nav.ToAbsoluteUri(uri).ToString()).Split('#')[0];

    /// <summary>A short name for a page of this app, for "Back to …".</summary>
    public static string PageName(string relativeUrl)
    {
        var path = relativeUrl.Split('?')[0].Trim('/').ToLowerInvariant();
        return path switch
        {
            "" => "Overview",
            "assets" => "Assets",
            "assets/view" or "asset" => "asset",
            "documents" => "Documents",
            "documents/view" => "document",
            "ask" => "Ask the manuals",
            "upload" or "ingest" => "Upload manuals",
            _ => "previous page"
        };
    }

    public void Dispose() => _nav.LocationChanged -= OnLocationChanged;
}
