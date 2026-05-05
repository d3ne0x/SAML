using System;
using System.Collections.Generic;
using System.IO;

namespace getSAMLResponse;

internal sealed class CaptureOptions
{
    public string? LoginUrl { get; private init; }
    public int TimeoutSeconds { get; private init; } = 300;
    public string? MatchUrlContains { get; private init; }
    public string? UserDataFolder { get; private init; }
    public bool ShowHelp { get; private init; }
    public bool Debug { get; private init; }

    public static string Usage => """
getSAMLResponse - interactive WebView2 SAMLResponse capture tool

Usage:
  getSAMLResponse.exe <IdP login URL> [options]
  getSAMLResponse.exe --url <IdP login URL> [options]

Options:
  --url <url>                 IdP initiated login URL or CyberArk SAML logon URL.
  --timeout <seconds>         Maximum wait time. Default: 300. Use 0 for no timeout.
  --match-url-contains <text> Optional URL substring to limit POST inspection.
                              Default inspects all document POST requests and looks for SAMLResponse.
  --user-data-folder <path>   Optional WebView2 profile folder. Default uses a temp isolated profile.
  --debug                     Print navigation/status messages to stderr.
  -h, --help                  Show help.

PowerShell:
  $saml = .\getSAMLResponse.exe "https://idp.example.com/app/.../sso/saml"

Exit codes:
  0 = SAMLResponse printed to stdout
  1 = capture failed, cancelled, or timed out
  2 = invalid arguments
""";

    public static CaptureOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CaptureOptions { ShowHelp = true };
        }

        string? url = null;
        string? matchUrlContains = null;
        string? userDataFolder = null;
        var timeout = 300;
        var showHelp = false;
        var debug = false;

        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                case "/?":
                    showHelp = true;
                    break;

                case "--debug":
                    debug = true;
                    break;

                case "--url":
                    url = ReadValue(args, ref i, "--url");
                    break;

                case "--timeout":
                    var timeoutValue = ReadValue(args, ref i, "--timeout");
                    if (!int.TryParse(timeoutValue, out timeout) || timeout < 0)
                    {
                        timeout = -1;
                    }
                    break;

                case "--match-url-contains":
                    matchUrlContains = ReadValue(args, ref i, "--match-url-contains");
                    break;

                case "--user-data-folder":
                    userDataFolder = ReadValue(args, ref i, "--user-data-folder");
                    break;

                default:
                    positional.Add(arg);
                    break;
            }
        }

        if (url is null && positional.Count > 0)
        {
            url = positional[0];
        }

        return new CaptureOptions
        {
            LoginUrl = url,
            TimeoutSeconds = timeout,
            MatchUrlContains = string.IsNullOrWhiteSpace(matchUrlContains) ? null : matchUrlContains,
            UserDataFolder = string.IsNullOrWhiteSpace(userDataFolder) ? null : userDataFolder,
            ShowHelp = showHelp,
            Debug = debug
        };
    }

    public bool IsValid(out string error)
    {
        if (ShowHelp)
        {
            error = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(LoginUrl))
        {
            error = "Missing IdP login URL.";
            return false;
        }

        if (!Uri.TryCreate(LoginUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "The IdP login URL must be an absolute http or https URL.";
            return false;
        }

        if (TimeoutSeconds < 0)
        {
            error = "Timeout must be a non-negative integer.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(UserDataFolder))
        {
            try
            {
                Directory.CreateDirectory(UserDataFolder);
            }
            catch (Exception ex)
            {
                error = $"Unable to access WebView2 user data folder '{UserDataFolder}': {ex.Message}";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static string ReadValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {optionName}.");
        }

        index++;
        return args[index];
    }
}
