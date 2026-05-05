using System;
using System.CommandLine;
using System.Threading;
using System.Windows.Forms;

namespace getSAMLResponse;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = CaptureOptions.Parse(args);

        if (options.ShowHelp)
        {
            Console.Error.WriteLine(CaptureOptions.Usage);
            return 0;
        }

        if (!options.IsValid(out var validationError))
        {
            Console.Error.WriteLine(validationError);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CaptureOptions.Usage);
            return 2;
        }

        ApplicationConfiguration.Initialize();

        using var timeoutCts = options.TimeoutSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds))
            : new CancellationTokenSource();

        string? samlResponse = null;
        Exception? capturedError = null;

        using var form = new SamlCaptureForm(options, timeoutCts.Token);
        form.SamlResponseCaptured += (_, response) =>
        {
            samlResponse = response;
            form.Close();
        };
        form.CaptureFailed += (_, error) =>
        {
            capturedError = error;
            form.Close();
        };
        form.UserCancelled += (_, _) =>
        {
            capturedError = new OperationCanceledException("SAML login was cancelled before a SAMLResponse was captured.");
        };

        timeoutCts.Token.Register(() =>
        {
            if (!form.IsDisposed)
            {
                capturedError = new TimeoutException($"Timed out after {options.TimeoutSeconds} seconds waiting for SAMLResponse.");
                form.BeginInvoke(() => form.Close());
            }
        });

        Application.Run(form);

        if (!string.IsNullOrWhiteSpace(samlResponse))
        {
            Console.Out.WriteLine(samlResponse);
            return 0;
        }

        Console.Error.WriteLine(capturedError?.Message ?? "No SAMLResponse was captured.");
        return 1;
    }
}
