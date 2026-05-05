using System;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace getSAMLResponse;

internal sealed class SamlCaptureForm : Form
{
    private readonly CaptureOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly WebView2 _webView;
    private bool _captured;
    private bool _initialised;

    public event EventHandler<string>? SamlResponseCaptured;
    public event EventHandler<Exception>? CaptureFailed;
    public event EventHandler? UserCancelled;

    public SamlCaptureForm(CaptureOptions options, CancellationToken cancellationToken)
    {
        _options = options;
        _cancellationToken = cancellationToken;

        Text = "SAML Authentication";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 960;
        Height = 760;
        MinimizeBox = true;
        MaximizeBox = true;
        ShowIcon = false;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            AllowExternalDrop = false,
            DefaultBackgroundColor = System.Drawing.Color.White
        };

        Controls.Add(_webView);
        Shown += async (_, _) => await InitialiseAndNavigateAsync();
        FormClosing += (_, _) =>
        {
            if (!_captured && _initialised)
            {
                UserCancelled?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    private async Task InitialiseAndNavigateAsync()
    {
        try
        {
            var userDataFolder = _options.UserDataFolder;
            var tempProfile = false;

            if (string.IsNullOrWhiteSpace(userDataFolder))
            {
                userDataFolder = Path.Combine(Path.GetTempPath(), "getSAMLResponse", Guid.NewGuid().ToString("N"));
                tempProfile = true;
            }

            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);

            ConfigureWebView();
            _webView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            _webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
            _initialised = true;
            Debug($"Navigating to {_options.LoginUrl}");
            _webView.CoreWebView2.Navigate(_options.LoginUrl!);

            if (tempProfile)
            {
                Disposed += (_, _) => TryDeleteDirectory(userDataFolder);
            }
        }
        catch (Exception ex)
        {
            CaptureFailed?.Invoke(this, ex);
        }
    }

    private void ConfigureWebView()
    {
        var settings = _webView.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsStatusBarEnabled = false;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_captured)
        {
            e.Cancel = true;
            return;
        }

        Debug($"Navigation: {e.Uri}");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            Debug($"Navigation completed with WebView2 status: {e.WebErrorStatus}");
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_captured || _cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            if (!string.Equals(e.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_options.MatchUrlContains) &&
                !e.Request.Uri.Contains(_options.MatchUrlContains, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var body = ReadRequestBody(e.Request.Content);
            if (string.IsNullOrWhiteSpace(body) || !body.Contains("SAMLResponse", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Debug($"Inspecting SAML POST to {e.Request.Uri}");
            var samlResponse = TryExtractSamlResponse(body);

            if (string.IsNullOrWhiteSpace(samlResponse))
            {
                return;
            }

            _captured = true;
            e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(Encoding.UTF8.GetBytes("SAMLResponse captured. You can close this window.")),
                200,
                "OK",
                "Content-Type: text/plain; charset=utf-8");

            SamlResponseCaptured?.Invoke(this, samlResponse);
        }
        catch (Exception ex)
        {
            CaptureFailed?.Invoke(this, ex);
        }
    }

    private static string ReadRequestBody(Stream? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var body = reader.ReadToEnd();

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        return WebUtility.HtmlDecode(body);
    }

    private static string? TryExtractSamlResponse(string body)
    {
        var decodedBody = WebUtility.HtmlDecode(body);
        var form = HttpUtility.ParseQueryString(decodedBody);
        var samlResponse = FirstValue(form, "SAMLResponse");

        if (!string.IsNullOrWhiteSpace(samlResponse))
        {
            return samlResponse.Trim();
        }

        var marker = "SAMLResponse=";
        var markerIndex = decodedBody.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var valueStart = markerIndex + marker.Length;
        var valueEnd = decodedBody.IndexOf('&', valueStart);
        var rawValue = valueEnd >= 0
            ? decodedBody[valueStart..valueEnd]
            : decodedBody[valueStart..];

        return string.IsNullOrWhiteSpace(rawValue)
            ? null
            : WebUtility.UrlDecode(rawValue).Trim();
    }

    private static string? FirstValue(NameValueCollection values, string key)
    {
        foreach (var candidate in values.AllKeys)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                return values[candidate];
            }
        }

        return null;
    }

    private void Debug(string message)
    {
        if (_options.Debug)
        {
            Console.Error.WriteLine($"[getSAMLResponse] {message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
